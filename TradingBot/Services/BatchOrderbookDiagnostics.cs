namespace TradingBot.Services;

public sealed record BatchOrderbookDiagnosticSnapshot(long TokensRequested, long Batches, long Loaded,
    long Missing, long Errors, long NoBooksLoaded, long RetryAttempts, long RateLimited,
    long Timeouts, long ProviderErrors, long MalformedResponses, long EmptyResponses,
    string LastStatus, string LastHttpStatus, string LastErrorKind, string LastErrorMessageShort,
    string LastEndpoint, long LastLatencyMs, bool CircuitBreakerOpen, long FallbackAttempts)
{
    public static BatchOrderbookDiagnosticSnapshot Empty { get; } = new(0,0,0,0,0,0,0,0,0,0,0,0,"None","None","None","None","None",0,false,0);
}

public static class BatchOrderbookDiagnostics
{
    private static readonly object Sync=new();
    public static BatchOrderbookDiagnosticSnapshot Current { get; private set; }=BatchOrderbookDiagnosticSnapshot.Empty;
    public static void Requested(int tokens) { lock(Sync) Current=Current with { TokensRequested=Current.TokensRequested+tokens,Batches=Current.Batches+1 }; }
    public static void Retry() { lock(Sync) Current=Current with { RetryAttempts=Current.RetryAttempts+1 }; }
    public static void Fallback() { lock(Sync) Current=Current with { FallbackAttempts=Current.FallbackAttempts+1 }; }
    public static void Result(int requested,int loaded,string status,string httpStatus,string kind,string message,long latency,bool circuitOpen=false)
    { lock(Sync) { var missing=Math.Max(0,requested-loaded); Current=Current with { Loaded=Current.Loaded+loaded,Missing=Current.Missing+missing,Errors=Current.Errors+(status=="Ok"?0:1),NoBooksLoaded=Current.NoBooksLoaded+(status=="NoBooksLoaded"?1:0),RateLimited=Current.RateLimited+(httpStatus=="429"?1:0),Timeouts=Current.Timeouts+(kind=="Timeout"?1:0),ProviderErrors=Current.ProviderErrors+(kind=="ProviderError"?1:0),MalformedResponses=Current.MalformedResponses+(kind=="MalformedResponse"?1:0),EmptyResponses=Current.EmptyResponses+(status=="NoBooksLoaded"?1:0),LastStatus=status,LastHttpStatus=httpStatus,LastErrorKind=kind,LastErrorMessageShort=Short(message),LastEndpoint="https://clob.polymarket.com/books",LastLatencyMs=latency,CircuitBreakerOpen=circuitOpen }; } }
    public static void CircuitOpen() { lock(Sync) Current=Current with { CircuitBreakerOpen=true,LastStatus="CircuitOpen",LastErrorKind="CircuitBreakerOpen" }; }
    private static string Short(string value){value=(value??"").Replace('\r',' ').Replace('\n',' ');return value.Length<=160?value:value[..160];}
}
