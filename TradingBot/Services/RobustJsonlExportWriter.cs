using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record JsonlExportHealthSnapshot(long ErrorsTotal, string LastErrorKind, string LastErrorPath,
    DateTime? LastErrorUtc, bool WriterDisabled, bool FallbackActive, int QueueDepth, long DroppedRecordsTotal,
    long RotationCount, string Health);
public sealed record InvalidArtifactsExportSnapshot(string ExportHealth,bool FallbackActive,string CurrentPath,string LastError,long RecordsWrittenTotal,long RecordsDroppedTotal);

/// <summary>Process-wide, bounded and serialized JSONL writer for non-critical diagnostic evidence.</summary>
public static class RobustJsonlExportWriter
{
    private sealed record Entry(string LogicalName,string Json);
    private static readonly ConcurrentQueue<Entry> Queue=[];
    private static readonly SemaphoreSlim Signal=new(0);
    private static readonly object Sync=new();
    private static CancellationTokenSource? _cts; private static Task? _worker;
    private static JsonlExportOptions _options=new(); private static string _root=Directory.GetCurrentDirectory();
    private static long _errors,_dropped,_rotations,_written; private static int _depth,_failures;
    private static string _lastKind="None",_lastPath="None"; private static DateTime? _lastErrorUtc;
    private static bool _disabled,_fallback; private static DateTime _lastVerboseErrorUtc=DateTime.MinValue; private static readonly Dictionary<string,string> ActivePaths=new(StringComparer.OrdinalIgnoreCase);

    public static void Configure(JsonlExportOptions options,string root)
    { SafeExportWriter.Configure(options,root); lock(Sync){_options=options;_root=root;_disabled=false;_fallback=false;_failures=0;_errors=0;_dropped=0;_rotations=0;_written=0;_lastKind="None";_lastPath="None";_lastErrorUtc=null;ActivePaths.Clear();while(Queue.TryDequeue(out _)){}Interlocked.Exchange(ref _depth,0);if(_worker is null||_worker.IsCompleted){_cts=new();_worker=Task.Run(()=>Run(_cts.Token));}} }

    public static bool Enqueue(string logicalName,object value)
    {
        if(_disabled){Interlocked.Increment(ref _dropped);return false;}
        if(Interlocked.Increment(ref _depth)>_options.JsonlWriteQueueCapacity){Interlocked.Decrement(ref _depth);Interlocked.Increment(ref _dropped);return false;}
        Queue.Enqueue(new(logicalName,JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})));
        Signal.Release(); return true;
    }

    public static void ObserveExternalFailure(IOException error,string path) => RecordFailure(error,path);

    public static JsonlExportHealthSnapshot Snapshot()
    { lock(Sync)return new(_errors,_lastKind,_lastPath,_lastErrorUtc,_disabled,_fallback,Volatile.Read(ref _depth),_dropped,_rotations,_disabled?"Disabled":_fallback?"Fallback":_failures>0?"Degraded":"Ok"); }
    public static InvalidArtifactsExportSnapshot InvalidArtifactsSnapshot(){lock(Sync)return new(_disabled?"Disabled":_fallback?"Fallback":_failures>0?"Degraded":"Ok",_fallback,_lastPath,_lastKind=="None"?"None":$"{_lastKind}:{_lastPath}",_written,_dropped);}

    private static async Task Run(CancellationToken ct)
    { while(!ct.IsCancellationRequested){try{await Signal.WaitAsync(TimeSpan.FromSeconds(1),ct);}catch(OperationCanceledException){break;}var batch=new List<Entry>(256);while(batch.Count<256&&Queue.TryDequeue(out var e)){Interlocked.Decrement(ref _depth);batch.Add(e);}foreach(var group in batch.GroupBy(x=>x.LogicalName))await Write(group.Key,group.Select(x=>x.Json),ct);} }

    private static async Task Write(string name,IEnumerable<string> records,CancellationToken ct)
    {
        var data=string.Join(Environment.NewLine,records)+Environment.NewLine; var primary=PathFor(name,false);
        for(var attempt=0;attempt<=_options.JsonlMaxRetries;attempt++)
        { try{RotateIfNeeded(name,primary,Encoding.UTF8.GetByteCount(data));Heal(primary);if(!SafeExportWriter.AppendText(primary,data,name,critical:false))throw new IOException("Safe export append failed");var health=SafeExportWriter.StreamSnapshot(name);lock(Sync){_failures=0;_fallback=health.FallbackActive;_lastKind="None";_lastPath=health.FallbackPath??primary;_written+=records.Count();}return;}
          catch(IOException ex){RecordFailure(ex,primary);if(attempt<_options.JsonlMaxRetries)await Task.Delay(_options.JsonlBackoffMs*(attempt+1),ct);}
          catch(UnauthorizedAccessException ex){RecordFailure(ex,primary);break;} }
        var fallback=PathFor(name,true);
        try{Heal(fallback);if(!SafeExportWriter.AppendText(fallback,data,name,critical:false))throw new IOException("Safe fallback append failed");lock(Sync){_failures=0;_fallback=true;_lastKind="None";_lastPath=fallback;_written+=records.Count();}}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){RecordFailure(ex,fallback);if(_failures>=_options.JsonlDisableAfterConsecutiveFailures)_disabled=true;}
    }

    private static string PathFor(string name,bool fallback)
    { lock(Sync){var key=name+(fallback?":fallback":":primary");if(ActivePaths.TryGetValue(key,out var p))return p;var stem=Path.GetFileNameWithoutExtension(name).Replace("paper-phase1-","");var file=$"{stem}-{ProcessRunContext.ProcessRunId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}{(fallback?"-fallback":"")}.jsonl";var dir=fallback?Path.Combine(_root,"exports","debug","fallback",stem):Path.Combine(_root,"exports","debug",stem);return ActivePaths[key]=Path.Combine(dir,file);} }
    private static void Heal(string path){var parent=Path.GetDirectoryName(path)!;Directory.CreateDirectory(parent);if(Directory.Exists(path))throw new UnauthorizedAccessException("JSONL target is a directory");if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0){try{File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);}catch{/* writer rotates below */}}}
    private static void RotateIfNeeded(string name,string path,int bytes)
    {var max=(long)_options.JsonlMaxFileMb*1024*1024;if(!File.Exists(path)||new FileInfo(path).Length+bytes<=max)return;lock(Sync){var rotated=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"-rotated-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".jsonl");File.Move(path,rotated);Interlocked.Increment(ref _rotations);} }
    private static void RecordFailure(Exception ex,string path)
    {
        var emit=false; lock(Sync){_errors++;_failures++;_lastKind=ex.GetType().Name;_lastPath=path;_lastErrorUtc=DateTime.UtcNow;if(_lastErrorUtc.Value-_lastVerboseErrorUtc>=TimeSpan.FromMinutes(10)){_lastVerboseErrorUtc=_lastErrorUtc.Value;emit=true;}}
        if(emit) Phase1ConsoleLogging.RecordNonBlockingEvent("EXPORT_WRITE_ERROR",new Dictionary<string,object?>{{"kind",ex.GetType().Name},{"path",path},{"message",ex.Message},{"export","InvalidPositiveArtifacts"}});
    }
}
