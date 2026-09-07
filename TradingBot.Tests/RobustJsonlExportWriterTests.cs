using System.Text.Json;
using TradingBot.Options;
using TradingBot.Api;
using TradingBot.Services;

namespace TradingBot.Tests;

public sealed class RobustJsonlExportWriterTests
{
    [Fact]
    public async Task SerialWriterUsesRunScopedFileAndPublishesPointer()
    {
        var root=Path.Combine(Path.GetTempPath(),$"polybot-jsonl-{Guid.NewGuid():N}");
        RobustJsonlExportWriter.Configure(new JsonlExportOptions{JsonlWriteQueueCapacity=10},root);
        Assert.True(RobustJsonlExportWriter.Enqueue("paper-phase1-invalid-positive-artifacts.jsonl",new{candidateId="test"}));

        var pointer=Path.Combine(root,"exports","debug","invalid-positive-artifacts","paper-phase1-invalid-positive-artifacts-jsonl.json");
        for(var i=0;i<50&&!File.Exists(pointer);i++) await Task.Delay(20);

        Assert.True(File.Exists(pointer));
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(pointer));
        var active=document.RootElement.GetProperty("activePath").GetString()!;
        Assert.Contains(ProcessRunContext.ProcessRunId,active);
        Assert.True(File.Exists(active));
        Assert.Single(File.ReadLines(active));
        Assert.DoesNotContain("SINGLE_MARKET_ERROR",await File.ReadAllTextAsync(active));
    }
}
