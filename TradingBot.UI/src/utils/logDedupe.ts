const LOG_DEDUPE_TTL_MS = 30_000;

export type LogLike = {
  id?: string;
  timestamp?: string;
  source?: string;
  level?: string;
  message?: string;
};

const hashString = (value: string): string => {
  let hash = 2166136261;
  for (let i = 0; i < value.length; i += 1) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16);
};

export const isCriticalLog = (log: LogLike): boolean => {
  const level = (log.level ?? '').toLowerCase();
  const message = log.message ?? '';
  return level === 'error' || message.includes('[MEMORY_CRITICAL]') || message.includes('[PAPER_CONFIG_ERROR]');
};

export const logDedupeKey = (log: LogLike): string => {
  const timestampMs = Date.parse(log.timestamp ?? '') || Date.now();
  const bucket = Math.floor(timestampMs / LOG_DEDUPE_TTL_MS);
  return `${bucket}|${log.source ?? ''}|${hashString(log.message ?? '')}`;
};

export const addLogDeduped = <T extends LogLike>(current: T[], next: T, max: number): T[] => {
  if (!isCriticalLog(next)) {
    const key = logDedupeKey(next);
    if (current.some((item) => !isCriticalLog(item) && logDedupeKey(item) === key)) return current;
  }
  return [next, ...current].slice(0, max);
};

export const dedupeLogSnapshot = <T extends LogLike>(logs: T[], max: number): T[] => {
  const seen = new Set<string>();
  const deduped: T[] = [];
  for (const log of logs) {
    if (!isCriticalLog(log)) {
      const key = logDedupeKey(log);
      if (seen.has(key)) continue;
      seen.add(key);
    }
    deduped.push(log);
    if (deduped.length >= max) break;
  }
  return deduped;
};
