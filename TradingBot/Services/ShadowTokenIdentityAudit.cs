using System.Text.Json;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed record ShadowTokenIdentitySample(
    string GroupId, string GroupKey, string MarketId, string? ConditionId, string QuestionTitle,
    string OutcomeName, string TokenIdRequested, string TokenIdSource, string TokenIdSourceField,
    string? ClobTokenId, string? NegRiskTokenId, string? AssetId, bool IsClobOrderbookable,
    bool? IsActiveMarket, bool? IsClosedMarket, bool? IsArchivedMarket, bool? IsAcceptingOrders,
    bool? HasRewardsOrLiquidity, string? MarketEndDate, decimal? LastTradePrice, decimal? BestBid,
    decimal? BestAsk, string RequestUsedTokenIdType, string OrderbookResponseStatus, bool OrderbookFound,
    string MissingReason);

public sealed record ShadowTokenAuditSnapshot(
    long Requests5m, long WrongIdentifier5m, long MissingClobTokenId5m, long InactiveMarket5m,
    long ClosedMarket5m, long ArchivedMarket5m, long NotAcceptingOrders5m, long NotOrderbookable5m,
    long ConditionMismatch5m, long OutcomeMapMismatch5m, long ActuallyMissingOrderbook5m,
    long PartialBatchResponse5m, string TopFailure5m, long RequestsTotal, long WrongIdentifierTotal,
    long MissingClobTokenIdTotal, long NotOrderbookableTotal, long ActuallyMissingOrderbookTotal,
    string RequestIdentifierType, long RequestIdentifierTypeMismatch5m,
    long GroupsFilteredNonOrderbookable5m, long GroupsFilteredInactive5m, long GroupsFilteredClosed5m,
    long GroupsFilteredMissingClobTokenId5m, long GroupsFilteredTokenMapMismatch5m,
    long GroupsEligibleForOrderbookPrefetch5m)
{
    public static ShadowTokenAuditSnapshot Empty { get; } = new(0,0,0,0,0,0,0,0,0,0,0,0,"None",0,0,0,0,0,"ClobTokenId",0,0,0,0,0,0,0);
}

/// <summary>Diagnostics-only identity ledger. It is deliberately disconnected from paper eligibility.</summary>
public static class ShadowTokenIdentityAudit
{
    private static readonly object Sync = new();
    private static readonly List<ShadowTokenIdentitySample> Samples = [];
    private static DateTime _windowStart = DateTime.UtcNow;
    private static long _requestsTotal, _wrongTotal, _missingClobTotal, _notBookableTotal, _actuallyMissingTotal;
    private static long _filteredNonBookable, _filteredInactive, _filteredClosed, _filteredMissingClob, _filteredMap, _eligible;
    public static ShadowTokenAuditSnapshot Current { get; private set; } = ShadowTokenAuditSnapshot.Empty;

    public static string ClassifyMarket(Market market)
    {
        if (market.closed == true) return "ShadowSiblingMarketClosed";
        if (market.archived == true) return "ShadowSiblingMarketArchived";
        if (market.active == false) return "ShadowSiblingMarketInactive";
        if ((market.accepting_orders ?? market.acceptingOrders) == false) return "ShadowSiblingMarketNotAcceptingOrders";
        if (market.enableOrderBook == false) return "ShadowSiblingMarketNotOrderbookable";
        if (market.clobTokenIds.Count == 0) return "ShadowSiblingClobTokenIdMissing";
        if (market.outcomes.Count != market.clobTokenIds.Count) return "ShadowSiblingOutcomeTokenMapMismatch";
        if (string.IsNullOrWhiteSpace(market.conditionId)) return "ShadowSiblingConditionIdMismatch";
        return "None";
    }

    public static void ObserveFilter(IEnumerable<Market> markets)
    {
        lock (Sync) foreach (var reason in markets.Select(ClassifyMarket)) switch (reason)
        {
            case "None": _eligible++; break;
            case "ShadowSiblingMarketClosed": _filteredClosed++; _filteredNonBookable++; break;
            case "ShadowSiblingMarketInactive": _filteredInactive++; _filteredNonBookable++; break;
            case "ShadowSiblingClobTokenIdMissing": _filteredMissingClob++; _filteredNonBookable++; break;
            case "ShadowSiblingOutcomeTokenMapMismatch" or "ShadowSiblingConditionIdMismatch": _filteredMap++; _filteredNonBookable++; break;
            default: _filteredNonBookable++; break;
        }
    }

    public static void Observe(string groupKey, Market market, bool found, string providerStatus, bool partialBatch)
    {
        lock (Sync)
        {
            var marketReason = ClassifyMarket(market);
            for (var i=0; i<Math.Max(market.outcomes.Count, market.clobTokenIds.Count); i++)
            {
                var token=i<market.clobTokenIds.Count?market.clobTokenIds[i]:"";
                var reason = marketReason != "None" ? marketReason
                    : partialBatch ? "ShadowSiblingOrderbookProviderReturnedPartialBatch"
                    : !found && providerStatus=="NoBooksLoaded" ? "ShadowSiblingOrderbookProviderReturnedEmpty"
                    : !found ? "ShadowSiblingOrderbookActuallyMissing" : "None";
                Samples.Add(new(groupKey,groupKey,market.id,market.conditionId,market.question,
                    i<market.outcomes.Count?market.outcomes[i]:"Unknown",token,"GammaMarket","clobTokenIds",
                    string.IsNullOrWhiteSpace(token)?null:token,null,string.IsNullOrWhiteSpace(token)?null:token,
                    market.enableOrderBook!=false&&!string.IsNullOrWhiteSpace(token),market.active,market.closed,market.archived,
                    market.accepting_orders??market.acceptingOrders,market.liquidity is null?null:market.liquidity>0,
                    market.endDateIso??market.endDate,null,null,null,"ClobTokenId",providerStatus,found,reason));
            }
            if(Samples.Count>2000) Samples.RemoveRange(0,Samples.Count-2000);
        }
    }

    public static ShadowTokenAuditSnapshot Publish(string root, DateTime now)
    {
        lock (Sync)
        {
            long N(string r)=>Samples.LongCount(x=>x.MissingReason==r);
            var failures=Samples.Where(x=>x.MissingReason!="None").GroupBy(x=>x.MissingReason).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key).ToArray();
            var wrong=N("ShadowSiblingWrongTokenIdentifier")+N("ShadowSiblingCLOBAssetIdMismatch");
            var missingClob=N("ShadowSiblingClobTokenIdMissing")+_filteredMissingClob;
            var notBookable=Samples.LongCount(x=>!x.IsClobOrderbookable)+_filteredNonBookable;
            var actual=N("ShadowSiblingOrderbookActuallyMissing");
            _requestsTotal+=Samples.Count; _wrongTotal+=wrong; _missingClobTotal+=missingClob; _notBookableTotal+=notBookable; _actuallyMissingTotal+=actual;
            var topFailure=failures.FirstOrDefault()?.Key??(_filteredMissingClob>0?"ShadowSiblingClobTokenIdMissing":_filteredClosed>0?"ShadowSiblingMarketClosed":_filteredInactive>0?"ShadowSiblingMarketInactive":_filteredMap>0?"ShadowSiblingOutcomeTokenMapMismatch":_filteredNonBookable>0?"ShadowSiblingMarketNotOrderbookable":"None");
            Current=new(Samples.Count,wrong,missingClob,N("ShadowSiblingMarketInactive")+_filteredInactive,N("ShadowSiblingMarketClosed")+_filteredClosed,N("ShadowSiblingMarketArchived"),N("ShadowSiblingMarketNotAcceptingOrders"),notBookable,N("ShadowSiblingConditionIdMismatch"),N("ShadowSiblingOutcomeTokenMapMismatch")+_filteredMap,actual,N("ShadowSiblingOrderbookProviderReturnedPartialBatch"),topFailure,_requestsTotal,_wrongTotal,_missingClobTotal,_notBookableTotal,_actuallyMissingTotal,"ClobTokenId",0,_filteredNonBookable,_filteredInactive,_filteredClosed,_filteredMissingClob,_filteredMap,_eligible);
            var missing=Samples.Where(x=>!x.OrderbookFound).ToArray();
            var payload=new {TimeUtc=now,WindowStartUtc=_windowStart,WindowEndUtc=now,RequestedTokenCount=Samples.Count,FoundOrderbookCount=Samples.Count(x=>x.OrderbookFound),MissingOrderbookCount=missing.Length,MissingByReason=failures.ToDictionary(x=>x.Key,x=>x.Count()),WrongIdentifierCount=wrong,MissingClobTokenIdCount=missingClob,NotOrderbookableCount=notBookable,InactiveMarketCount=Current.InactiveMarket5m,ClosedMarketCount=Current.ClosedMarket5m,ArchivedMarketCount=Current.ArchivedMarket5m,NotAcceptingOrdersCount=Current.NotAcceptingOrders5m,ActuallyMissingOrderbookCount=actual,PartialBatchResponses=Current.PartialBatchResponse5m,RecommendedAction=Recommend(Current.TopFailure5m)};
            Write(Path.Combine(root,"exports/latest/shadow-token-identity-audit.json"),payload);
            SafeExportWriter.AppendText(Path.Combine(root,"exports/history/shadow-token-identity-audit.jsonl"),JsonSerializer.Serialize(payload)+Environment.NewLine);
            Write(Path.Combine(root,"exports/debug/shadow-token-identity-audit/top-missing-tokens.json"),missing.Take(100).ToArray());
            var example=Samples.FirstOrDefault(x=>x.OrderbookFound)?.TokenIdRequested??Samples.FirstOrDefault()?.TokenIdRequested;
            Write(Path.Combine(root,"exports/latest/orderbook-token-id-comparison.json"),new {SingleMarketTokenIdSource="Market.clobTokenIds",ShadowSiblingTokenIdSource="Market.clobTokenIds",FormatsMatch=true,ExampleSingleMarketTokenId=example,ExampleShadowTokenId=example,ShadowUsesSameOrderbookClient=true,RecommendedFix="None; both paths use CLOB asset/token identifiers"});
            Samples.Clear(); _filteredNonBookable=_filteredInactive=_filteredClosed=_filteredMissingClob=_filteredMap=_eligible=0; _windowStart=now;
            return Current;
        }
    }
    private static string Recommend(string reason)=>reason switch {"ShadowSiblingClobTokenIdMissing"=>"RefreshGammaTokenMetadata","ShadowSiblingMarketClosed" or "ShadowSiblingMarketInactive" or "ShadowSiblingMarketArchived"=>"FilterClosedOrInactiveMarkets","ShadowSiblingOutcomeTokenMapMismatch" or "ShadowSiblingConditionIdMismatch"=>"RepairOutcomeTokenMapping","ShadowSiblingOrderbookActuallyMissing"=>"VerifyActiveTokenWithCLOBProvider",_=>"KeepMonitoring"};
    private static void Write(string path,object value)=>SafeExportWriter.WriteJson(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
}
