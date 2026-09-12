using System.Text;
using TradingBot.Options;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record SafeExportHealthSnapshot(
    long FreeDiskMb, int MinFreeDiskMb, int CriticalFreeDiskMb, string DiskStatus,
    bool NonCriticalDisabled, long CriticalWriteFailuresTotal, long NonCriticalWriteFailuresTotal,
    long WriteFailuresTotal, string LastFailedStream, string LastFailedPath, string LastExceptionType,
    string LastExceptionMessageShort, string Health, long RetentionDeletedFilesTotal,
    double RetentionFreedMbTotal, DateTime? RetentionLastRunUtc, string RetentionLastError,
    long FailedStreamsTotal, int FallbackStreamsActive, long PrimaryPathDeniedTotal,
    long FallbackWritesTotal, IReadOnlyDictionary<string, SafeExportStreamHealth> Streams,
    string Layout, string Root, string LatestDir, string HistoryDir, string DebugDir,
    int TopLevelGeneratedFilesCount, bool GitIgnored, bool RetentionEnabled);

public sealed record SafeExportStreamHealth(string Health, bool FallbackActive, string LastError,
    string PrimaryPath, string? FallbackPath, long Failures, long FallbackWrites);

/// <summary>Process-wide non-throwing boundary for every diagnostics/export file write.</summary>
public static class SafeExportWriter
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Disabled = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SafeExportStreamHealth> Streams = new(StringComparer.OrdinalIgnoreCase);
    private static JsonlExportOptions _options = new();
    private static string _root = Directory.GetCurrentDirectory(), _exportRoot=Path.Combine(Directory.GetCurrentDirectory(),"exports");
    private static DateTime _lastDiskCheckUtc = DateTime.MinValue, _lastRetentionUtc = DateTime.MinValue;
    private static long _freeDiskMb = -1, _criticalFailures, _nonCriticalFailures, _deleted, _primaryDenied, _fallbackWrites;
    private static double _freedMb;
    private static string _diskStatus = "Unknown", _lastStream = "None", _lastPath = "None", _lastType = "None", _lastMessage = "None", _retentionError = "None";

    public static void Configure(JsonlExportOptions options, string root)
    {
        lock (Sync) { _options = options; _root = Path.GetFullPath(root); _exportRoot=Path.GetFullPath(Path.IsPathRooted(options.Root)?options.Root:Path.Combine(_root,options.Root)); Failures.Clear(); Disabled.Clear(); Streams.Clear(); _criticalFailures=0; _nonCriticalFailures=0; _deleted=0; _primaryDenied=0; _fallbackWrites=0; _freedMb=0; _lastStream=_lastPath=_lastType=_lastMessage="None"; _diskStatus="Unknown"; _lastDiskCheckUtc=DateTime.MinValue; _lastRetentionUtc=DateTime.MinValue; }
        try{foreach(var directory in new[]{"latest","history","debug","archive"}) Directory.CreateDirectory(Path.Combine(_exportRoot,directory));}catch(Exception ex) when(IsHandled(ex)){RecordFailure("ExportLayout",_exportRoot,ex,false);}
        if(options.Layout.Equals("Consolidated",StringComparison.OrdinalIgnoreCase)&&!options.WriteLegacyPointerFiles) ArchiveLegacyTopLevelFiles();
        CheckDiskAndRetention(force: true);
    }

    public static bool WriteJson(string path, string json, string? streamName = null, bool? critical = null) =>
        WriteText(path, json, streamName, critical);

    public static bool WriteText(string path, string contents, string? streamName = null, bool? critical = null)
    {
        path = Normalize(path); var stream = Stream(streamName, path); path=ResolvePath(path,stream,false); var isCritical = critical ?? IsCritical(path);
        if(IsDebug(stream,path)&&!_options.DebugExportsEnabled)return true;
        if (!CanWrite(stream, isCritical)) return false;
        for (var attempt = 0; attempt <= _options.JsonlMaxRetries; attempt++)
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                ThrowIfInjected(); PreparePath(path);
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false))) { writer.Write(contents); writer.Flush(); file.Flush(true); }
                File.Move(temp, path, true); Success(stream,path); return true;
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
        path = Normalize(path); var stream = Stream(streamName, path); path=ResolvePath(path,stream,true); var isCritical = critical ?? IsCritical(path);
        if(!_options.HistoryEnabled)return true;
        if (!CanWrite(stream, isCritical)) return false;
        lock (Sync)
        {
            try { ThrowIfInjected(); PreparePath(path); RotateIfNeeded(path,IsDebug(stream,path)?_options.MaxDebugFileMb:_options.MaxHistoryFileMb); File.AppendAllText(path, contents, new UTF8Encoding(false)); Success(stream,path); return true; }
            catch (Exception ex) when (IsHandled(ex))
            {
                RecordFailure(stream, path, ex, isCritical);
                return TryAppendFallback(stream,path,contents,isCritical);
            }
        }
    }

    private static bool TryAppendFallback(string stream,string primary,string contents,bool critical)
    {
        var stem=Path.GetFileNameWithoutExtension(primary);
        var fallback=Path.Combine(_exportRoot,"debug","fallback",Sanitize(stream),$"{stem}-{ProcessRunContext.ProcessRunId}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
        try
        {
            PreparePath(fallback); File.AppendAllText(fallback,contents,new UTF8Encoding(false));
            lock(Sync){_fallbackWrites++;Failures[stream]=0;Disabled.Remove(stream);var old=Streams.GetValueOrDefault(stream);Streams[stream]=new("Fallback",true,"None",primary,fallback,old?.Failures??1,(old?.FallbackWrites??0)+1);ClearGlobalFailureIfRecovered(stream);}
            return true;
        }
        catch(Exception ex) when(IsHandled(ex)){RecordFailure(stream,fallback,ex,critical);return false;}
    }

    public static SafeExportHealthSnapshot Snapshot()
    {
        CheckDiskAndRetention();
        lock (Sync)
        {
            var unhealthy=Streams.Values.Any(x=>x.Health is "Degraded" or "Disabled");
            var health = _diskStatus is "CriticalLowDisk" ? "CriticalLowDisk" : _diskStatus is "LowDisk" ? "LowDisk" : Disabled.Count > 0 ? "Disabled" : unhealthy ? "Degraded" : "Ok";
            return new(_freeDiskMb, _options.MinFreeDiskMb, _options.CriticalFreeDiskMb, _diskStatus,
                _options.DisableNonCriticalExportsWhenLowDisk && _diskStatus is "LowDisk" or "CriticalLowDisk",
                _criticalFailures, _nonCriticalFailures, _criticalFailures + _nonCriticalFailures,
                _lastStream, _lastPath, _lastType, _lastMessage, health, _deleted, _freedMb,
                _lastRetentionUtc == DateTime.MinValue ? null : _lastRetentionUtc, _retentionError,
                Streams.Values.LongCount(x=>x.Health is "Degraded" or "Disabled"), Streams.Values.Count(x=>x.FallbackActive),
                _primaryDenied, _fallbackWrites, new Dictionary<string,SafeExportStreamHealth>(Streams,StringComparer.OrdinalIgnoreCase),
                _options.Layout,_options.Root,Relative("latest"),Relative("history"),Relative("debug"),TopLevelGeneratedCount(),GitIgnored(),_options.RetentionEnabled);
        }
    }

    private static bool CanWrite(string stream, bool critical)
    {
        CheckDiskAndRetention();
        lock (Sync) return !Disabled.Contains(stream) && (critical || !_options.DisableNonCriticalExportsWhenLowDisk || _diskStatus == "Ok" || _diskStatus == "Unknown");
    }
    private static bool TryFallback(string stream, string path, string contents, bool critical)
    {
        var fallback = Path.Combine(_exportRoot, "archive", "fallback", Path.GetFileName(path));
        var temp=fallback+"."+Environment.ProcessId+".tmp";
        try
        {
            ThrowIfInjected(); PreparePath(fallback);
            using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,64*1024,FileOptions.WriteThrough))
            using(var writer=new StreamWriter(file,new UTF8Encoding(false))){writer.Write(contents);writer.Flush();file.Flush(true);}
            File.Move(temp,fallback,true);
            lock(Sync){_fallbackWrites++;Failures[stream]=0;Disabled.Remove(stream);Streams[stream]=new("Fallback",true,$"{_lastType}:{_lastMessage}",path,fallback,Streams.GetValueOrDefault(stream)?.Failures??1,Streams.GetValueOrDefault(stream)?.FallbackWrites+1??1);} return true;
        }
        catch (Exception ex) when (IsHandled(ex)) { RecordFailure(stream, fallback, ex, critical); return false; }
        finally { TryDelete(temp); }
    }
    private static void Success(string stream,string path) { lock (Sync) { Failures[stream] = 0; Disabled.Remove(stream); var old=Streams.GetValueOrDefault(stream); Streams[stream]=new("Ok",false,"None",path,null,old?.Failures??0,old?.FallbackWrites??0); ClearGlobalFailureIfRecovered(stream); } }
    private static void ClearGlobalFailureIfRecovered(string stream){if(_lastStream.Equals(stream,StringComparison.OrdinalIgnoreCase)){_lastStream=_lastPath=_lastType=_lastMessage="None";}}
    private static void RecordFailure(string stream, string path, Exception ex, bool critical)
    {
        lock (Sync)
        {
            if (critical) _criticalFailures++; else _nonCriticalFailures++;
            if(ex is UnauthorizedAccessException&&!path.Contains(Path.Combine("archive","fallback"),StringComparison.OrdinalIgnoreCase)) _primaryDenied++;
            _lastStream = stream; _lastPath = path; _lastType = ex.GetType().Name;
            _lastMessage = ex.Message.Replace('\r', ' ').Replace('\n', ' '); if (_lastMessage.Length > 160) _lastMessage = _lastMessage[..160];
            Failures[stream] = Failures.GetValueOrDefault(stream) + 1;
            if (Failures[stream] >= _options.JsonlDisableAfterConsecutiveFailures) Disabled.Add(stream);
            Streams[stream]=new(Disabled.Contains(stream)?"Disabled":"Degraded",false,$"{_lastType}:{_lastMessage}",path,null,Failures[stream],Streams.GetValueOrDefault(stream)?.FallbackWrites??0);
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
            var exportRoot = _exportRoot; if (!Directory.Exists(exportRoot)) { lock (Sync) _lastRetentionUtc = now; return; }
            var files = new[] { "history", "debug", "archive" }.SelectMany(directory => { var path=Path.Combine(exportRoot,directory); return Directory.Exists(path)?Directory.GetFiles(path,"*",SearchOption.AllDirectories):Array.Empty<string>(); }).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new FileInfo(x)).Where(x => x.Name!=".gitkeep").OrderBy(x => x.LastWriteTimeUtc).ToList();
            var total = files.Sum(x => x.Length); var max = _options.RetentionMaxTotalMb * 1024L * 1024L; var cutoff = now.AddDays(-_options.RetentionMaxFileAgeDays);
            foreach (var file in files.Where(x => x.LastWriteTimeUtc < cutoff || total > max).ToArray()) { var bytes = file.Length; file.Delete(); total -= bytes; lock (Sync) { _deleted++; _freedMb += bytes / 1024d / 1024d; } }
            lock (Sync) { _lastRetentionUtc = now; _retentionError = "None"; }
        }
        catch (Exception ex) when (IsHandled(ex)) { lock (Sync) { _lastRetentionUtc = now; _retentionError = ex.GetType().Name + ":" + ex.Message; } }
    }
    private static bool IsCritical(string path) => path.Contains("release-status", StringComparison.OrdinalIgnoreCase) || path.Contains("operator-runbook", StringComparison.OrdinalIgnoreCase) || path.Contains("dashboard", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-account", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-execution", StringComparison.OrdinalIgnoreCase) || path.Contains("paper-position", StringComparison.OrdinalIgnoreCase);
    private static string Stream(string? name, string path) => string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name;
    public static SafeExportStreamHealth StreamSnapshot(string stream) { lock(Sync) return Streams.GetValueOrDefault(stream)??new("Ok",false,"None","None",null,0,0); }
    public static string Resolve(string path,bool history=false,string? streamName=null){path=Normalize(path);return ResolvePath(path,Stream(streamName,path),history);}
    private static string Normalize(string path) => Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar,Path.DirectorySeparatorChar));
    private static string ResolvePath(string original,string stream,bool append)
    {
        if(!Path.GetFullPath(original).StartsWith(_exportRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return original;
        var key=stream.ToLowerInvariant(); var source=Path.GetFileName(original);
        var required=key switch {
            "diagnosticsdashboardlatest" or "diagnostics-dashboard-latest"=>"diagnostics-dashboard.json",
            "dashboardlatest" or "dashboard-warnings-latest"=>"dashboard-warnings.json",
            "batch-book-errors-latest"=>"batch-book-health.json",
            "phase1-strategy-comparison-latest"=>"strategy-comparison.json",
            "phase1-strategy-comparison-history"=>"strategy-comparison.jsonl",
            "phase1-verified-multioutcome-discovery-latest"=>"verified-multioutcome-discovery.json",
            "phase1-verified-multioutcome-discovery-history"=>"verified-multioutcome-discovery.jsonl",
            "phase1-verified-multioutcome-completion-latest"=>"verified-multioutcome-completion.json",
            "phase1-verified-multioutcome-completion-history"=>"verified-multioutcome-completion.jsonl",
            "phase1-shadow-orderbook-availability-latest"=>"shadow-orderbook-availability.json",
            "phase1-shadow-orderbook-availability-history"=>"shadow-orderbook-availability.jsonl",
            "paper-phase1-invalid-positive-artifacts-latest"=>"invalid-positive-artifacts.json",
            "verified-multioutcome-liquidity-latest"=>"verified-multioutcome-liquidity.json",
            "verified-multioutcome-liquidity-history"=>"verified-multioutcome-liquidity.jsonl",
            "verified-multioutcome-liquidity-top-blocked"=>"top-blocked-groups.json",
            "dashboardhistory" or "dashboard-warnings-history"=>"dashboard-warnings.jsonl",
            "releasestatuslatest" or "paper-phase1-release-status-latest"=>"release-status.json",
            "operatorrunbooklatest" or "paper-phase1-operator-runbook-latest"=>"operator-runbook.json",
            "phase1summarylatest" or "phase1-summary-latest"=>"phase1-summary.json",
            "exporthealthlatest" or "export-health-latest"=>"export-health.json",
            _=>source.Replace("-latest",string.Empty,StringComparison.OrdinalIgnoreCase)
        };
        if(IsDebug(stream,original)){var category=DebugCategory(key);var debugName=append&&!required.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)?Path.ChangeExtension(required,".jsonl"):required;return Path.Combine(_exportRoot,"debug",category,debugName);}
        if(append){var history=required.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)?required:Path.ChangeExtension(required,".jsonl");return Path.Combine(_exportRoot,"history",history);}
        return Path.Combine(_exportRoot,"latest",required);
    }
    private static bool IsDebug(string stream,string path){var value=(stream+" "+path).ToLowerInvariant();return value.Contains("invalid-positive")||value.Contains("liquidity-top-blocked")||value.Contains("near-miss")||value.Contains("verification")||value.Contains("dry-run-order")||value.Contains("fill-simulation")||value.Contains("verbose-event");}
    private static string DebugCategory(string key)=>key.Contains("invalid-positive")?"invalid-positive-artifacts":key.Contains("liquidity-top-blocked")?"verified-multioutcome-liquidity":key.Contains("auto-candidate-near")?"auto-candidate-near-misses":key.Contains("verification")?"auto-candidate-verification":key.Contains("dry-run-order")?"dry-run-order-plans":key.Contains("fill-simulation")?"dry-run-fill-simulations":"verbose-events";
    private static string Sanitize(string value)=>string.Concat(value.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'-')).Trim('-');
    private static void RotateIfNeeded(string path,int maxMb){if(!File.Exists(path)||new FileInfo(path).Length<maxMb*1024L*1024L)return;var archive=Path.Combine(_exportRoot,"archive",DateTime.UtcNow.ToString("yyyyMMdd"));Directory.CreateDirectory(archive);File.Move(path,Path.Combine(archive,$"{Path.GetFileNameWithoutExtension(path)}-{ProcessRunContext.ProcessRunId}-{DateTime.UtcNow:HHmmssfff}{Path.GetExtension(path)}"),true);}
    private static string Relative(string child)=>Path.Combine(_options.Root,child).Replace('\\','/');
    private static int TopLevelGeneratedCount()=>Directory.Exists(_exportRoot)?Directory.GetFiles(_exportRoot).Count(x=>Path.GetFileName(x) is not "README.md" and not ".gitkeep"):0;
    private static void ArchiveLegacyTopLevelFiles()
    {
        try
        {
            if(!Directory.Exists(_exportRoot))return;
            var destination=Path.Combine(_exportRoot,"archive","legacy-top-level");
            foreach(var file in Directory.GetFiles(_exportRoot).Where(x=>Path.GetFileName(x) is not "README.md" and not ".gitkeep"))
            {
                Directory.CreateDirectory(destination);
                var target=Path.Combine(destination,Path.GetFileName(file));
                if(File.Exists(target)) target=Path.Combine(destination,$"{Path.GetFileNameWithoutExtension(file)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(file)}");
                File.Move(file,target);
            }
        }
        catch(Exception ex) when(IsHandled(ex)){RecordFailure("LegacyExportCleanup",_exportRoot,ex,false);}
    }
    private static bool GitIgnored(){try{for(var directory=new DirectoryInfo(_root);directory is not null;directory=directory.Parent){var path=Path.Combine(directory.FullName,".gitignore");if(File.Exists(path)&&File.ReadAllText(path).Contains("TradingBot/exports/**",StringComparison.Ordinal))return true;}return false;}catch{return false;}}
    private static void PreparePath(string path) { var parent=Path.GetDirectoryName(path)??throw new DirectoryNotFoundException(path); Directory.CreateDirectory(parent); if(Directory.Exists(path)) throw new UnauthorizedAccessException("Export path is a directory."); if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0) File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly); }
    private static bool IsHandled(Exception ex) => ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException || ex is Exception;
    private static void ThrowIfInjected() { if (_options.InjectDiskFullForTesting) throw new IOException("There is not enough space on the disk"); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
