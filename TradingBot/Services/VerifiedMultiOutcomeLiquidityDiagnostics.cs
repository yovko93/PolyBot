using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Services.MultiOutcome;

namespace TradingBot.Services;

public sealed record VerifiedLiquidityLeg(string MarketId,string OutcomeName,string? TokenId,bool HasOrderbook,
    decimal? BestBid,decimal? BestAsk,decimal? BidSize,decimal? AskSize,bool IsEmptyBook,bool MissingBid,
    bool MissingAsk,bool MissingNoAsk,bool MissingYesAsk,decimal? Spread,decimal? Mid,decimal? LastTradePrice,
    double? OrderbookAgeMs,string RequiredSide,string RequiredPriceField,string LiquidityBlocker);
public sealed record VerifiedLiquidityGroup(string GroupId,string GroupKey,IReadOnlyList<string> MarketIds,
    IReadOnlyList<string> OutcomeNames,IReadOnlyList<string?> TokenIds,string Strategy,bool IsActiveComplete,
    bool IsOrderbookable,bool ValidPriced,string FirstBlockingReason,IReadOnlyList<string> AllBlockingReasons,
    IReadOnlyList<VerifiedLiquidityLeg> PerLegOrderbook,decimal? BestPossiblePartialEdge,int MissingLegCount,
    int AvailableLegCount,string RecommendedAction);
public sealed record VerifiedLiquiditySnapshot(int Evaluated5m,int EmptyBook5m,int MissingAsk5m,int MissingBid5m,
    int MissingYesAsk5m,int MissingNoAsk5m,int WideSpread5m,int ValidPriced5m,decimal BestAvailableLegCoverage5m,
    string TopLiquidityBlocker5m,long EvaluatedTotal,long EmptyBookTotal,long MissingAskTotal,long MissingBidTotal,
    long ValidPricedTotal);

/// <summary>Shadow-only evidence about active verified groups. It cannot influence paper eligibility.</summary>
public static class VerifiedMultiOutcomeLiquidityDiagnostics
{
    private static readonly object Sync=new(); private static readonly List<VerifiedLiquidityGroup> Groups=[];
    private static long _evaluatedTotal,_emptyTotal,_missingAskTotal,_missingBidTotal,_validTotal;
    public static VerifiedLiquiditySnapshot Current {get;private set;}=new(0,0,0,0,0,0,0,0,0,"None",0,0,0,0,0);

    public static void Observe(ResolvedVerifiedGroup group,IReadOnlyList<(Market Market,BinaryOrderBookSnapshot? Book)> books,bool validPriced,DateTime now)
    {
        var legs=books.Select(x=>Leg(x.Market,x.Book,now)).ToArray();
        var reasons=legs.Select(x=>x.LiquidityBlocker).Where(x=>x!="None").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var available=legs.Count(x=>x.BestAsk.HasValue); var partial=available==0?(decimal?)null:1m-legs.Where(x=>x.BestAsk.HasValue).Sum(x=>x.BestAsk!.Value);
        var first=reasons.FirstOrDefault()??(validPriced?"None":"InvalidPricing");
        var action=first.Contains("EmptyBook",StringComparison.OrdinalIgnoreCase)?"WaitForOrderbookLiquidity":first.Contains("MissingAsk",StringComparison.OrdinalIgnoreCase)?"MonitorAskLiquidity":first.Contains("MissingBid",StringComparison.OrdinalIgnoreCase)?"MonitorBidLiquidity":"KeepShadowMonitoring";
        lock(Sync) Groups.Add(new(group.GroupKey,group.GroupKey,group.MarketIds,books.Select(x=>x.Market.question).ToArray(),legs.Select(x=>x.TokenId).ToArray(),"VerifiedMultiOutcome",true,true,validPriced,first,reasons,legs,partial,legs.Length-available,available,action));
    }

    public static VerifiedLiquiditySnapshot Publish(string root,DateTime now)
    {
        lock(Sync){var groups=Groups.ToArray();Groups.Clear();var evaluated=groups.Length;var empty=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.IsEmptyBook));var ma=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.MissingAsk));var mb=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.MissingBid));var mya=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.MissingYesAsk));var mna=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.MissingNoAsk));var wide=groups.Count(g=>g.PerLegOrderbook.Any(l=>l.Spread>.20m));var valid=groups.Count(g=>g.ValidPriced);var coverage=groups.Length==0?0:groups.Max(g=>g.PerLegOrderbook.Count==0?0m:(decimal)g.AvailableLegCount/g.PerLegOrderbook.Count);var top=groups.SelectMany(g=>g.AllBlockingReasons).GroupBy(x=>x).OrderByDescending(x=>x.Count()).FirstOrDefault()?.Key??"None";_evaluatedTotal+=evaluated;_emptyTotal+=empty;_missingAskTotal+=ma;_missingBidTotal+=mb;_validTotal+=valid;Current=new(evaluated,empty,ma,mb,mya,mna,wide,valid,coverage,top,_evaluatedTotal,_emptyTotal,_missingAskTotal,_missingBidTotal,_validTotal);
            var payload=new{generatedAtUtc=now,processRunId=ProcessRunContext.ProcessRunId,diagnosticsOnly=true,paperOpenAllowed=false,counters=Current,topBlockedGroups=groups.Where(g=>!g.ValidPriced).OrderBy(g=>g.MissingLegCount).Take(50)};var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase});SafeExportWriter.WriteJson(Path.Combine(root,"exports","verified-multioutcome-liquidity-latest.json"),json,"verified-multioutcome-liquidity-latest");SafeExportWriter.AppendText(Path.Combine(root,"exports","verified-multioutcome-liquidity-history.jsonl"),JsonSerializer.Serialize(payload)+Environment.NewLine,"verified-multioutcome-liquidity-history");SafeExportWriter.WriteJson(Path.Combine(root,"exports","debug","verified-multioutcome-liquidity","top-blocked-groups.json"),json,"verified-multioutcome-liquidity-top-blocked");return Current;}
    }
    private static VerifiedLiquidityLeg Leg(Market m,BinaryOrderBookSnapshot? b,DateTime now){var t=VerifiedGroupPricingService.ResolveBinaryTokens(m);var bid=b?.NoBid;var ask=b?.NoAsk;var empty=b is not null&&b.YesBid is null&&b.YesAsk is null&&b.NoBid is null&&b.NoAsk is null;var blocker=b is null?"MissingNoAsk_OrderbookUnavailable":empty?"MissingNoAsk_EmptyBook":ask is null?"MissingNoAsk":bid is null?"MissingBid":"None";return new(m.id,m.question,t.NoTokenId,b is not null,bid?.Price,ask?.Price,bid?.Size,ask?.Size,empty,bid is null,ask is null,b?.NoAsk is null,b?.YesAsk is null,bid is not null&&ask is not null?ask.Price-bid.Price:null,bid is not null&&ask is not null?(ask.Price+bid.Price)/2:null,null,b is null?null:Math.Max(0,(now-b.TimestampUtc).TotalMilliseconds),"NO","BestAsk",blocker);}
}
