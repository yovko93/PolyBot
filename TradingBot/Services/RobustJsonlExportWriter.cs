using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record JsonlExportHealthSnapshot(long ErrorsTotal, string LastErrorKind, string LastErrorPath,
    DateTime? LastErrorUtc, bool WriterDisabled, bool FallbackActive, int QueueDepth, long DroppedRecordsTotal,
    long RotationCount, string Health);

/// <summary>Process-wide, bounded and serialized JSONL writer for non-critical diagnostic evidence.</summary>
public static class RobustJsonlExportWriter
{
    private sealed record Entry(string LogicalName,string Json);
    private static readonly ConcurrentQueue<Entry> Queue=[];
    private static readonly SemaphoreSlim Signal=new(0);
    private static readonly object Sync=new();
    private static CancellationTokenSource? _cts; private static Task? _worker;
    private static JsonlExportOptions _options=new(); private static string _root=Directory.GetCurrentDirectory();
    private static long _errors,_dropped,_rotations; private static int _depth,_failures;
    private static string _lastKind="None",_lastPath="None"; private static DateTime? _lastErrorUtc;
    private static bool _disabled,_fallback; private static DateTime _lastVerboseErrorUtc=DateTime.MinValue; private static readonly Dictionary<string,string> ActivePaths=new(StringComparer.OrdinalIgnoreCase);

    public static void Configure(JsonlExportOptions options,string root)
    { lock(Sync){_options=options;_root=root;_disabled=false;_fallback=false;_failures=0;ActivePaths.Clear();if(_worker is null||_worker.IsCompleted){_cts=new();_worker=Task.Run(()=>Run(_cts.Token));}} }

    public static bool Enqueue(string logicalName,object value)
    {
        if(_disabled){Interlocked.Increment(ref _dropped);return false;}
        if(Interlocked.Increment(ref _depth)>_options.JsonlWriteQueueCapacity){Interlocked.Decrement(ref _depth);Interlocked.Increment(ref _dropped);return false;}
        Queue.Enqueue(new(logicalName,JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})));
        Signal.Release(); return true;
    }

    public static void ObserveExternalFailure(IOException error,string path) => RecordFailure(error,path);

    public static JsonlExportHealthSnapshot Snapshot()
    { lock(Sync)return new(_errors,_lastKind,_lastPath,_lastErrorUtc,_disabled,_fallback,Volatile.Read(ref _depth),_dropped,_rotations,_disabled?"Disabled":_errors>0||_fallback||_dropped>0?"Degraded":"Ok"); }

    private static async Task Run(CancellationToken ct)
    { while(!ct.IsCancellationRequested){try{await Signal.WaitAsync(TimeSpan.FromSeconds(1),ct);}catch(OperationCanceledException){break;}var batch=new List<Entry>(256);while(batch.Count<256&&Queue.TryDequeue(out var e)){Interlocked.Decrement(ref _depth);batch.Add(e);}foreach(var group in batch.GroupBy(x=>x.LogicalName))await Write(group.Key,group.Select(x=>x.Json),ct);} }

    private static async Task Write(string name,IEnumerable<string> records,CancellationToken ct)
    {
        var data=string.Join(Environment.NewLine,records)+Environment.NewLine; var primary=PathFor(name,false);
        for(var attempt=0;attempt<=_options.JsonlMaxRetries;attempt++)
        { try{RotateIfNeeded(name,primary,Encoding.UTF8.GetByteCount(data));Directory.CreateDirectory(Path.GetDirectoryName(primary)!);await File.AppendAllTextAsync(primary,data,Encoding.UTF8,ct);lock(Sync){_failures=0;_fallback=false;_lastPath=primary;}WritePointer(name,primary);return;}
          catch(IOException ex){RecordFailure(ex,primary);if(attempt<_options.JsonlMaxRetries)await Task.Delay(_options.JsonlBackoffMs*(attempt+1),ct);}
          catch(UnauthorizedAccessException ex){RecordFailure(ex,primary);break;} }
        var fallback=PathFor(name,true);
        try{Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);await File.AppendAllTextAsync(fallback,data,Encoding.UTF8,ct);lock(Sync){_fallback=true;_lastPath=fallback;}WritePointer(name,fallback);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){RecordFailure(ex,fallback);if(_failures>=_options.JsonlDisableAfterConsecutiveFailures)_disabled=true;}
    }

    private static string PathFor(string name,bool fallback)
    { lock(Sync){var key=name+(fallback?":fallback":":primary");if(ActivePaths.TryGetValue(key,out var p))return p;var stem=Path.GetFileNameWithoutExtension(name);var suffix="";if(_options.JsonlRotateByRun)suffix+="-"+ProcessRunContext.ProcessRunId;if(_options.JsonlRotateByDate)suffix+="-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");if(fallback)suffix+="-fallback";return ActivePaths[key]=Path.Combine(_root,"exports",stem+suffix+".jsonl");} }
    private static void RotateIfNeeded(string name,string path,int bytes)
    {var max=(long)_options.JsonlMaxFileMb*1024*1024;if(!File.Exists(path)||new FileInfo(path).Length+bytes<=max)return;lock(Sync){var rotated=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"-rotated-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".jsonl");File.Move(path,rotated);Interlocked.Increment(ref _rotations);} }
    private static void RecordFailure(Exception ex,string path)
    {
        var emit=false; lock(Sync){_errors++;_failures++;_lastKind=ex.GetType().Name;_lastPath=path;_lastErrorUtc=DateTime.UtcNow;if(_lastErrorUtc.Value-_lastVerboseErrorUtc>=TimeSpan.FromMinutes(10)){_lastVerboseErrorUtc=_lastErrorUtc.Value;emit=true;}}
        if(emit) Phase1ConsoleLogging.RecordNonBlockingEvent("EXPORT_WRITE_ERROR",new Dictionary<string,object?>{{"kind",ex.GetType().Name},{"path",path},{"message",ex.Message},{"export","InvalidPositiveArtifacts"}});
    }
    private static void WritePointer(string name,string path){try{var pointer=Path.Combine(_root,"exports",Path.GetFileNameWithoutExtension(name)+"-jsonl-latest.json");File.WriteAllText(pointer,JsonSerializer.Serialize(new{activePath=path,updatedAtUtc=DateTime.UtcNow,fallbackActive=_fallback}));}catch{/* pointer is convenience-only */}}
}
