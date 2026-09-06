using System.Text;
using TradingBot.Options;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record SafeExportHealthSnapshot(
    long FreeDiskMb, int MinFreeDiskMb, int CriticalFreeDiskMb, string DiskStatus,
    bool NonCriticalDisabled, long CriticalWriteFailuresTotal, long NonCriticalWriteFailuresTotal,
    long WriteFailuresTotal, string LastFailedStream, string LastFailedPath, string LastExceptionType,
    string LastExceptionMessageShort, string Health, long RetentionDeletedFilesTotal,
    double RetentionFreedMbTotal, DateTime? RetentionLastRunUtc, string RetentionLastError);

/// <summary>Process-wide non-throwing boundary for every diagnostics/export file write.</summary>
public static class SafeExportWriter
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Disabled = new(StringComparer.OrdinalIgnoreCase);
    private static JsonlExportOptions _options = new();
    private static string _root = Directory.GetCurrentDirectory();
    private static DateTime _lastDiskCheckUtc = DateTime.MinValue, _lastRetentionUtc = DateTime.MinValue;
    private static long _freeDiskMb = -1, _criticalFailures, _nonCriticalFailures, _deleted;
    private static double _freedMb;
    private static string _diskStatus = "Unknown", _lastStream = "None", _lastPath = "None", _lastType = "None", _lastMessage = "None", _retentionError = "None";

    public static void Configure(JsonlExportOptions options, string root)
    {
        lock (Sync) { _options = options; _root = root; Failures.Clear(); Disabled.Clear(); _criticalFailures=0; _nonCriticalFailures=0; _deleted=0; _freedMb=0; _lastStream=_lastPath=_lastType=_lastMessage="None"; _diskStatus="Unknown"; _lastDiskCheckUtc=DateTime.MinValue; _lastRetentionUtc=DateTime.MinValue; }
        CheckDiskAndRetention(force: true);
    }

    public static bool WriteJson(string path, string json, string? streamName = null, bool? critical = null) =>
        WriteText(path, json, streamName, critical);

    public static bool WriteText(string path, string contents, string? streamName = null, bool? critical = null)
    {
        var stream = Stream(streamName, path); var isCritical = critical ?? IsCritical(path);
        if (!CanWrite(stream, isCritical)) return false;
        for (var attempt = 0; attempt <= _options.JsonlMaxRetries; attempt++)
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                ThrowIfInjected(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false))) { writer.Write(contents); writer.Flush(); file.Flush(true); }
                File.Move(temp, path, true); Success(stream); return true;
            }
            catch (Exception ex) when (IsHandled(ex))
            {
                TryDelete(temp); RecordFailure(stream, path, ex, isCritical);
                if (ex is IOException && attempt < _options.JsonlMaxRetries) Thread.Sleep(_options.JsonlBackoffMs * (attempt + 1)); else break;
            }
        }
        return TryFallback(stream, path, contents, isCritical);
    }

    public static bool AppendText(string path, string contents, string? streamName = null, bool? critical = null)
    {
        var stream = Stream(streamName, path); var isCritical = critical ?? IsCritical(path);
        if (!CanWrite(stream, isCritical)) return false;
        lock (Sync)
        {
            try { ThrowIfInjected(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, contents, new UTF8Encoding(false)); Success(stream); return true; }
            catch (Exception ex) when (IsHandled(ex)) { RecordFailure(stream, path, ex, isCritical); return false; }
        }
    }

    public static SafeExportHealthSnapshot Snapshot()
    {
        CheckDiskAndRetention();
        lock (Sync)
        {
            var health = _diskStatus is "CriticalLowDisk" ? "CriticalLowDisk" : _diskStatus is "LowDisk" ? "LowDisk" : Disabled.Count > 0 ? "Disabled" : _criticalFailures + _nonCriticalFailures > 0 ? "Degraded" : "Ok";
            return new(_freeDiskMb, _options.MinFreeDiskMb, _options.CriticalFreeDiskMb, _diskStatus,
                _options.DisableNonCriticalExportsWhenLowDisk && _diskStatus is "LowDisk" or "CriticalLowDisk",
                _criticalFailures, _nonCriticalFailures, _criticalFailures + _nonCriticalFailures,
                _lastStream, _lastPath, _lastType, _lastMessage, health, _deleted, _freedMb,
                _lastRetentionUtc == DateTime.MinValue ? null : _lastRetentionUtc, _retentionError);
        }
    }

    private static bool CanWrite(string stream, bool critical)
    {
        CheckDiskAndRetention();
        lock (Sync) return !Disabled.Contains(stream) && (critical || !_options.DisableNonCriticalExportsWhenLowDisk || _diskStatus == "Ok" || _diskStatus == "Unknown");
    }
    private static bool TryFallback(string stream, string path, string contents, bool critical)
    {
        var fallback = Path.Combine(Path.GetTempPath(), "PolyBot-exports-fallback", ProcessRunContext.ProcessRunId, Path.GetFileName(path));
        try { ThrowIfInjected(); Directory.CreateDirectory(Path.GetDirectoryName(fallback)!); File.WriteAllText(fallback, contents); return false; }
        catch (Exception ex) when (IsHandled(ex)) { RecordFailure(stream, fallback, ex, critical); return false; }
    }
    private static void Success(string stream) { lock (Sync) Failures[stream] = 0; }
    private static void RecordFailure(string stream, string path, Exception ex, bool critical)
    {
        lock (Sync)
        {
            if (critical) _criticalFailures++; else _nonCriticalFailures++;
            _lastStream = stream; _lastPath = path; _lastType = ex.GetType().Name;
            _lastMessage = ex.Message.Replace('\r', ' ').Replace('\n', ' '); if (_lastMessage.Length > 160) _lastMessage = _lastMessage[..160];
            Failures[stream] = Failures.GetValueOrDefault(stream) + 1;
            if (Failures[stream] >= _options.JsonlDisableAfterConsecutiveFailures) Disabled.Add(stream);
        }
    }
    private static void CheckDiskAndRetention(bool force = false)
    {
        var now = DateTime.UtcNow;
        lock (Sync) if (!force && now - _lastDiskCheckUtc < TimeSpan.FromSeconds(_options.DiskCheckIntervalSeconds)) return;
        try
        {
            var full = Path.GetFullPath(_root); var drive = new DriveInfo(Path.GetPathRoot(full)!); var free = drive.AvailableFreeSpace / 1024 / 1024;
            lock (Sync) { _freeDiskMb = free; _diskStatus = free < _options.CriticalFreeDiskMb ? "CriticalLowDisk" : free < _options.MinFreeDiskMb ? "LowDisk" : "Ok"; _lastDiskCheckUtc = now; }
        }
        catch (Exception ex) { lock (Sync) { _diskStatus = "Unknown"; _lastDiskCheckUtc = now; _lastType = ex.GetType().Name; _lastMessage = ex.Message; } }
        if (_options.RetentionEnabled && (force || now - _lastRetentionUtc >= TimeSpan.FromMinutes(30))) RunRetention(now);
    }
    private static void RunRetention(DateTime now)
    {
        try
        {
            var exportRoot = Path.Combine(_root, "exports"); if (!Directory.Exists(exportRoot)) { lock (Sync) _lastRetentionUtc = now; return; }
            var files = _options.RetentionDeletePatterns.SelectMany(pattern => Directory.GetFiles(exportRoot, Path.GetFileName(pattern), SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new FileInfo(x)).Where(x => !x.Name.Contains("latest", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.LastWriteTimeUtc).ToList();
            var total = files.Sum(x => x.Length); var max = _options.RetentionMaxTotalMb * 1024L * 1024L; var cutoff = now.AddDays(-_options.RetentionMaxFileAgeDays);
            foreach (var file in files.Where(x => x.LastWriteTimeUtc < cutoff || total > max).ToArray()) { var bytes = file.Length; file.Delete(); total -= bytes; lock (Sync) { _deleted++; _freedMb += bytes / 1024d / 1024d; } }
            lock (Sync) { _lastRetentionUtc = now; _retentionError = "None"; }
        }
        catch (Exception ex) when (IsHandled(ex)) { lock (Sync) { _lastRetentionUtc = now; _retentionError = ex.GetType().Name + ":" + ex.Message; } }
    }
    private static bool IsCritical(string path) => path.Contains("release-status", StringComparison.OrdinalIgnoreCase) || path.Contains("operator-runbook", StringComparison.OrdinalIgnoreCase) || path.Contains("dashboard", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-account", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-execution", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-position", StringComparison.OrdinalIgnoreCase);
    private static string Stream(string? name, string path) => string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name;
    private static bool IsHandled(Exception ex) => ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException || ex is Exception;
    private static void ThrowIfInjected() { if (_options.InjectDiskFullForTesting) throw new IOException("There is not enough space on the disk"); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
