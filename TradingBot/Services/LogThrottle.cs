namespace TradingBot.Services;

public sealed class LogThrottle
{
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cycles = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldLog(string key, string fingerprint, bool onChangeOnly = true, int everyNCycles = 25, bool critical = false)
    {
        if (critical) return true;
        _cycles.TryGetValue(key, out var cycle);
        cycle++;
        _cycles[key] = cycle;
        var changed = !_fingerprints.TryGetValue(key, out var previous) || previous != fingerprint;
        _fingerprints[key] = fingerprint;
        var periodic = everyNCycles > 0 && cycle % everyNCycles == 0;
        return !onChangeOnly || changed || periodic;
    }
}
