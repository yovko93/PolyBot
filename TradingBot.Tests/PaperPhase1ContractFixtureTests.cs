using TradingBot.Services;
using TradingBot.Api;
using Xunit;

namespace TradingBot.Tests;

public sealed class PaperPhase1ContractFixtureTests
{
    private static PaperPositionBook Book() => new(Path.Combine(Path.GetTempPath(), $"phase1-contract-{Guid.NewGuid():N}.csv"));

    [Fact]
    public void Dry_replay_reaches_paper_eligible_without_open_or_execution_counter()
    {
        var result = PaperPhase1ContractFixtureService.Run(new(), dryReplayOnly: true, allowPaperOpen: false, Book());

        Assert.True(result.CleanPositive && result.RealWatchPositive && result.ExecutableLike && result.PaperEligible);
        Assert.Equal(4, result.AlertLevel);
        Assert.Equal("PaperEligibleDetected", result.AlertName);
        Assert.False(result.OpenAttempted);
        Assert.False(result.Opened);
        Assert.Equal(0, result.RealScannerCountedExecutions);
        Assert.Equal("DryReplayOnly", result.Reason);
        Assert.True(result.Consistent);
    }

    [Fact]
    public void Explicit_open_contract_opens_exactly_one_non_synthetic_paper_position()
    {
        var book = Book();
        var result = PaperPhase1ContractFixtureService.Run(new(), dryReplayOnly: false, allowPaperOpen: true, book);

        Assert.True(result.PaperEligible && result.OpenAttempted && result.Opened);
        Assert.Equal(5, result.AlertLevel);
        Assert.Equal("PaperOpened", result.AlertName);
        Assert.Single(book.OpenPositions);
        Assert.Equal(1, result.RealScannerCountedExecutions);
        Assert.Equal(0, result.SyntheticCanaryCountedExecutions);
        Assert.StartsWith("PAPER-PHASE1-", result.PositionId);
        Assert.False(result.Position!.IsSyntheticCanary);
        Assert.Equal("RealScannerFixture", result.Position.SourceKind);
        Assert.Equal("SingleMarketBuyBoth", result.Position.Strategy);
        Assert.Equal(.012m, result.Position.NetEdgeAtOpen);
        Assert.True(result.Position.TotalCost <= 5m && result.Position.ExpectedProfit > 0m);

        var duplicate = PaperPhase1ContractFixtureService.Run(new(), false, true, book);
        Assert.False(duplicate.Opened);
        Assert.Single(book.OpenPositions);
    }

    [Fact]
    public void Missing_no_ask_high_edge_is_invalid_and_never_progresses_or_opens()
    {
        var fixture = new PaperPhase1ContractCandidate(HasNoAsk: false, AfterSafetyEdge: .997m);
        var result = PaperPhase1ContractFixtureService.Run(fixture, false, true, Book());

        Assert.Contains("MissingNoAsk", PaperPhase1ContractFixtureService.DataQualityReasons(fixture));
        Assert.Equal("InvalidPositiveArtifact", result.Classification);
        Assert.False(result.CleanPositive);
        Assert.False(result.RealWatchPositive);
        Assert.False(result.PaperEligible);
        Assert.False(result.OpenAttempted);
        Assert.False(result.Opened);
        Assert.Equal("MissingNoAsk", result.Reason);
    }

    [Fact]
    public void Quality_clean_below_min_fixture_stays_waiting_without_open()
    {
        var fixture = new PaperPhase1ContractCandidate(AfterSafetyEdge: -.003m);
        var result = PaperPhase1ContractFixtureService.Run(fixture, false, false, Book());

        Assert.Empty(PaperPhase1ContractFixtureService.DataQualityReasons(fixture));
        Assert.False(result.PaperEligible);
        Assert.Equal(0, result.AlertLevel);
        Assert.Equal("WaitingForEdge", result.AlertName);
        Assert.False(result.Opened);
        Assert.Equal("BelowMinEdge", result.Reason);
    }

    [Fact]
    public void Fixture_open_and_settle_balances_the_complete_lifecycle()
    {
        var book=Book(); var opened=PaperPhase1ContractFixtureService.Run(new(),false,true,book);
        var settled=PaperPhase1ContractFixtureService.SettlePosition(book,opened,opened.PositionId,opened.Position!.GuaranteedPayout,"FixtureContractSettlement");
        Assert.True(settled.SettlementRequested&&settled.SettlementSucceeded&&!settled.SettlementRejected);
        Assert.Equal(1,settled.PaperOpened); Assert.Equal(1,settled.PaperClosed); Assert.Equal(0,settled.PaperOpenPositions);
        Assert.Equal(0m,settled.PaperExposure); Assert.Equal(0m,settled.PaperLocked); Assert.True(settled.PaperRealizedPnl>0m);
        Assert.True(settled.LifecycleBalanceOk); Assert.Empty(book.OpenPositions); Assert.Single(book.ClosedPositions);
    }

    [Fact]
    public void Duplicate_and_unknown_settlements_are_rejected_without_changing_lifecycle_counts()
    {
        var book=Book(); var opened=PaperPhase1ContractFixtureService.Run(new(),false,true,book);
        var first=PaperPhase1ContractFixtureService.SettlePosition(book,opened,opened.PositionId,opened.Position!.GuaranteedPayout,"FixtureContractSettlement");
        var duplicate=PaperPhase1ContractFixtureService.SettlePosition(book,first,opened.PositionId,opened.Position.GuaranteedPayout,"Again");
        Assert.True(duplicate.SettlementRejected); Assert.Equal("AlreadyClosed",duplicate.SettlementLastReason);
        Assert.Equal(1,duplicate.PaperClosed); Assert.Single(book.ClosedPositions);
        var unknown=PaperPhase1ContractFixtureService.SettlePosition(book,first,"PAPER-PHASE1-UNKNOWN",1m,"Unknown");
        Assert.True(unknown.SettlementRejected); Assert.Equal("PositionNotFound",unknown.SettlementLastReason);
        Assert.Single(book.ClosedPositions);
    }

    [Fact]
    public void Real_settlement_path_rejects_canary_position()
    {
        var book=Book();
        var canary=book.AddSyntheticCanaryPosition("PAPER-CANARY-1","m","yes","no",.4m,.4m,1m,.8m,1m,.2m,.2m,.1m,"canary",ProcessRunContext.ProcessRunId)!;
        var result=PaperPhase1ContractFixtureService.SettlePosition(book,new(),canary.PositionId,1m,"NotAllowed");
        Assert.True(result.SettlementRejected); Assert.Equal("SyntheticCanaryNotAllowed",result.SettlementLastReason);
        Assert.Single(book.OpenPositions); Assert.Empty(book.ClosedPositions);
    }
}
