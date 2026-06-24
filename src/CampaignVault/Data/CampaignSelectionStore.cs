using System.Collections.Concurrent;

namespace CampaignVault.Data;

/// <summary>
/// Process-wide store mapping MCP session IDs to the selected campaign slug.
/// Requires a non-empty session ID for all reads and writes — no process-wide fallback.
/// </summary>
public sealed class CampaignSelectionStore
{
    public const string UnselectedSentinel = "";

    private readonly ConcurrentDictionary<string, Entry> _selections = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;

    public CampaignSelectionStore(TimeProvider? timeProvider = null, TimeSpan? idleTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? ResolveIdleTimeout();
    }

    private static TimeSpan ResolveIdleTimeout()
    {
        var hoursText = Environment.GetEnvironmentVariable("CAMPAIGN_SELECTION_TTL_HOURS");
        if (double.TryParse(hoursText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
        {
            return TimeSpan.FromHours(hours);
        }

        return TimeSpan.FromHours(2);
    }

    private sealed record Entry(string CampaignName, long LastAccessTicks);

    public bool HasSelection(string? sessionId)
    {
        if (!TryResolveKey(sessionId, out var key))
        {
            return false;
        }

        return _selections.TryGetValue(key, out var entry)
               && !string.IsNullOrEmpty(entry.CampaignName);
    }

    public string GetCurrent(string? sessionId)
    {
        if (!TryResolveKey(sessionId, out var key))
        {
            return UnselectedSentinel;
        }

        if (_selections.TryGetValue(key, out var entry))
        {
            Touch(key, entry);
            return entry.CampaignName;
        }

        return UnselectedSentinel;
    }

    public void SetCurrent(string? sessionId, string campaignName)
    {
        var key = RequireSessionKey(sessionId);

        if (string.IsNullOrWhiteSpace(campaignName))
        {
            campaignName = UnselectedSentinel;
        }
        else
        {
            campaignName = CampaignSlug.Canonicalize(campaignName);
        }

        _selections[key] = new Entry(campaignName, _timeProvider.GetUtcNow().Ticks);
        PruneExpired();
    }

    public void Clear(string? sessionId)
    {
        if (TryResolveKey(sessionId, out var key))
        {
            _selections.TryRemove(key, out _);
        }
    }

    internal static bool TryResolveKey(string? sessionId, out string key)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            key = string.Empty;
            return false;
        }

        key = sessionId.Trim();
        return true;
    }

    internal static string RequireSessionKey(string? sessionId)
    {
        if (!TryResolveKey(sessionId, out var key))
        {
            throw new CampaignSessionRequiredException();
        }

        return key;
    }

    private void Touch(string key, Entry entry)
    {
        _selections[key] = entry with { LastAccessTicks = _timeProvider.GetUtcNow().Ticks };
    }

    private void PruneExpired()
    {
        var cutoff = _timeProvider.GetUtcNow().Subtract(_idleTimeout).Ticks;

        foreach (var (key, entry) in _selections)
        {
            if (entry.LastAccessTicks < cutoff)
            {
                _selections.TryRemove(key, out _);
            }
        }
    }
}