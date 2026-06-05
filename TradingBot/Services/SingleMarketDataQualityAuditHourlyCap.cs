namespace TradingBot.Services;

public sealed class SingleMarketDataQualityAuditHourlyCap
{
    private readonly object _gate = new();
    private DateTime _windowStartUtc = DateTime.MinValue;
    private int _count;
    private bool _capReachedLogged;

    public bool TryReserve(int maxPerHour, DateTime nowUtc, out bool capReachedLogDue, out int cappedCount)
    {
        capReachedLogDue = false;
        cappedCount = Math.Max(0, maxPerHour);
        if (maxPerHour <= 0) return true;

        lock (_gate)
        {
            if (_windowStartUtc == DateTime.MinValue || nowUtc - _windowStartUtc >= TimeSpan.FromHours(1))
            {
                _windowStartUtc = nowUtc;
                _count = 0;
                _capReachedLogged = false;
            }

            if (_count >= maxPerHour)
            {
                if (!_capReachedLogged)
                {
                    _capReachedLogged = true;
                    capReachedLogDue = true;
                }
                return false;
            }

            _count++;
            return true;
        }
    }
}
