using System.Text.Json;
using TradingBot.Options;
using TradingBot.Api;
using TradingBot.Services;

namespace TradingBot.Tests;

public sealed class RobustJsonlExportWriterTests
{
    [Fact]
    public async Task SerialWriterUsesRunScopedDebugJsonlWithoutJsonPointer()
    {
        var root=Path.Combine(Path.GetTempPath(),$"polybot-jsonl-{Guid.NewGuid():N}");
        RobustJsonlExportWriter.Configure(new JsonlExportOptions{JsonlWriteQueueCapacity=10},root);
        Assert.True(RobustJsonlExportWriter.Enqueue("paper-phase1-invalid-positive-artifacts.jsonl",new{candidateId="test"}));

        string? active=null;
        for(var i=0;i<50&&active is null;i++){active=Directory.Exists(Path.Combine(root,"exports","debug","invalid-positive-artifacts"))?Directory.GetFiles(Path.Combine(root,"exports","debug","invalid-positive-artifacts"),"*.jsonl").SingleOrDefault():null;await Task.Delay(20);}
        Assert.NotNull(active);
        Assert.Contains(ProcessRunContext.ProcessRunId,active);
        Assert.True(File.Exists(active!));
        Assert.Single(File.ReadLines(active!));
        Assert.DoesNotContain("SINGLE_MARKET_ERROR",await File.ReadAllTextAsync(active!));
        Assert.False(File.Exists(Path.Combine(root,"exports","debug","invalid-positive-artifacts","paper-phase1-invalid-positive-artifacts-jsonl.json")));
    }
}
