using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedExecutionSuppressionTests
{
    [Fact]
    public void DuplicateSuppressionCounter_Increments()
    {
        var c = new VerifiedBasketExecutionCoordinator(Microsoft.Extensions.Options.Options.Create(new TradingBot.Options.ExecutionOptions()));
        Assert.Equal(1, c.MarkDuplicateSuppressed("g1"));
        Assert.Equal(2, c.MarkDuplicateSuppressed("g1"));
    }
}
