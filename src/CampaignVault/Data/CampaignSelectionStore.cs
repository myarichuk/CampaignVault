using System.Collections.Concurrent;

namespace CampaignVault.Data;

/// <summary>
/// Process-wide store mapping MCP session IDs (or a process fallback key) to the selected campaign.
/// </summary>
public sealed class CampaignSelectionStore
{
    public const string UnselectedSentinel = "";
    private const string ProcessFallbackKey = "__process__";

    private readonly ConcurrentDictionary<string, Entry> _selections = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public CampaignSelectionStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed record Entry(string CampaignName, long LastAccessTicks);

    public bool HasSelection(string? sessionId)
    {
        var key = ResolveKey(sessionId);
        return _selections.TryGetValue(key, out var entry)
               && !string.IsNullOrEmpty(entry.CampaignName);
    }

    public string GetCurrent(string? sessionId)
    {
        var key = ResolveKey(sessionId);
        if (_selections.TryGetValue(key, out var entry))
        {
            Touch(key, entry);
            return entry.CampaignName;
        }

        return UnselectedSentinel;
    }

    public void SetCurrent(string? sessionId, string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            campaignName = UnselectedSentinel;
        }

        var key = ResolveKey(sessionId);
        _selections[key] = new Entry(
            campaignName.Trim().ToLowerInvariant(),
            _timeProvider.GetUtcNow().Ticks);
        PruneExpired();
    }

    public void Clear(string? sessionId)
    {
        _selections.TryRemove(ResolveKey(sessionId), out _);
    }

    internal static string ResolveKey(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? ProcessFallbackKey : sessionId.Trim();

    private void Touch(string key, Entry entry)
    {
        _selections[key] = entry with { LastAccessTicks = _timeProvider.GetUtcNow().Ticks };
    }

    private void PruneExpired(TimeSpan? idleTimeout = null)
    {
        idleTimeout ??= TimeSpan.FromHours(2);
        var cutoff = _timeProvider.GetUtcNow().Subtract(idleTimeout.Value).Ticks;

        foreach (var (key, entry) in _selections)
        {
            if (entry.LastAccessTicks < cutoff)
            {
                _selections.TryRemove(key, out _);
            }
        }
    }
}