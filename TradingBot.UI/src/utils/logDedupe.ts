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

const normalizeLogCategory = (log: LogLike): string => {
  const message = log.message ?? '';
  if (message.startsWith('[CONFIG]')
    || message.startsWith('[DIAGNOSTICS]')
    || message.startsWith('[SOAK_READINESS]')
    || message.startsWith('[COST_PROFILE')
    || message.startsWith('[PAPER_MODE')
    || message.startsWith('[PAPER_EFFECTIVE_RISK]')
    || message.includes('Bot API listening')
    || message.includes('ExecutionMode=')) return 'startup-config';
  return log.source ?? '';
};

export const logDedupeKey = (log: LogLike): string => {
  if (log.id) return `id:${log.id}`;
  const timestampMs = Date.parse(log.timestamp ?? '') || Date.now();
  const bucket = Math.floor(timestampMs / LOG_DEDUPE_TTL_MS);
  return `${bucket}|${normalizeLogCategory(log)}|${hashString(log.message ?? '')}`;
};

export const logContentDedupeKey = (log: LogLike): string => {
  const timestampMs = Date.parse(log.timestamp ?? '') || Date.now();
  const bucket = Math.floor(timestampMs / LOG_DEDUPE_TTL_MS);
  return `${bucket}|${normalizeLogCategory(log)}|${hashString(log.message ?? '')}`;
};

export const addLogDeduped = <T extends LogLike>(current: T[], next: T, max: number): T[] => {
  if (!isCriticalLog(next)) {
    const idKey = next.id ? logDedupeKey(next) : undefined;
    const contentKey = logContentDedupeKey(next);
    if (current.some((item) => !isCriticalLog(item) && ((idKey && logDedupeKey(item) === idKey) || logContentDedupeKey(item) === contentKey))) return current;
  }
  return [next, ...current].slice(0, max);
};

export const dedupeLogSnapshot = <T extends LogLike>(logs: T[], max: number): T[] => {
  const seen = new Set<string>();
  const deduped: T[] = [];
  for (const log of logs) {
    if (!isCriticalLog(log)) {
      const idKey = log.id ? logDedupeKey(log) : undefined;
      const contentKey = logContentDedupeKey(log);
      if ((idKey && seen.has(idKey)) || seen.has(contentKey)) continue;
      if (idKey) seen.add(idKey);
      seen.add(contentKey);
    }
    deduped.push(log);
    if (deduped.length >= max) break;
  }
  return deduped;
};
