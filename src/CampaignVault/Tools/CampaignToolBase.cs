using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace CampaignVault.Tools;

public abstract class CampaignToolBase
{
    protected readonly CampaignRepository _repository;
    protected readonly CampaignDocumentKeys _keys;
    protected readonly ILogger _logger;

    protected CampaignToolBase(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ILogger? logger = null)
    {
        _repository = repository;
        _keys = keys;
        _logger = logger ?? NullLogger.Instance;
    }

    protected const string NoCampaignSelectedSummary =
        "campaignName is required on every tool call (e.g. 'dragon-heist').";

    protected static bool TryGetEffectiveCampaign(string? explicitName, out string effective)
    {
        if (CampaignSlug.TryCanonicalize(explicitName, out effective))
        {
            return true;
        }

        effective = string.Empty;
        return false;
    }

    protected Task<ToolResult<T>> ExecuteForCampaignAsync<T>(
        string campaignName,
        Func<string, IAsyncDocumentSession, Task<ToolResult<T>>> action,
        bool saveChanges = true)
    {
        if (!TryGetEffectiveCampaign(campaignName, out var effective))
        {
            return Task.FromResult(new ToolResult<T>(
                false,
                Error: ToolErrors.NoCampaignSelected,
                Summary: NoCampaignSelectedSummary));
        }

        return ExecuteAsync(session => action(effective, session), saveChanges);
    }

    protected async Task<ToolResult<T>> ExecuteAsync<T>(Func<IAsyncDocumentSession, Task<ToolResult<T>>> action, bool saveChanges = true)
    {
        int maxRetries = 2;
        int actionAttempt = 0;
        int saveAttempt = 0;

        while (true)
        {
            using var session = _repository.OpenSession();

            ToolResult<T> result;
            try
            {
                result = await action(session);
            }
            catch (CampaignNotSelectedException ex)
            {
                _logger.LogWarning(ex, "Campaign not selected");
                return new ToolResult<T>(false, Error: ToolErrors.NoCampaignSelected, Summary: ex.Message);
            }
            catch (ConcurrencyException ex)
            {
                if (++actionAttempt <= maxRetries)
                {
                    _logger.LogDebug(ex, "Concurrency conflict on action attempt {Attempt}, retrying", actionAttempt);
                    continue;
                }
                _logger.LogError(ex, "Concurrency conflict after {MaxRetries} retries", maxRetries);
                return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "State changed mid-operation. Re-fetch and retry.");
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "Validation failure in tool action");
                return new ToolResult<T>(false, Error: ToolErrors.InvalidArgument, Summary: ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in tool action");
                return new ToolResult<T>(false, Error: ToolErrors.InternalError,
                    Summary: "An internal error occurred while processing this request. It has been logged for investigation.");
            }

            if (!result.Success)
            {
                return result;
            }

            if (saveChanges)
            {
                try
                {
                    await session.SaveChangesAsync();
                }
                catch (ConcurrencyException ex)
                {
                    if (++saveAttempt <= maxRetries)
                    {
                        _logger.LogDebug(ex, "Concurrency conflict on save attempt {Attempt}, retrying", saveAttempt);
                        continue;
                    }
                    _logger.LogError(ex, "Concurrency conflict after {MaxRetries} save retries", maxRetries);
                    return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "Commit failed due to concurrent modification. Re-fetch and retry.");
                }
            }

            JsonSanitizer.SanitizeForToolResponse(result.Data);
            return result;
        }
    }

    protected async Task<Campaign> GetOrCreateCampaignMetaAsync(IAsyncDocumentSession session, string normalizedName, string defaultSystem, string? displayName = null, bool forceLock = false)
    {
        var campaignId = _keys.Meta(normalizedName);
        var campaign = await session.LoadAsync<Campaign>(campaignId);
        if (campaign == null)
        {
            campaign = new Campaign
            {
                Id = campaignId,
                Name = normalizedName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedName : displayName,
                System = defaultSystem,
                IsSystemLocked = forceLock
            };
            await session.StoreAsync(campaign, campaignId);

            var configId = _keys.Config(normalizedName);
            var config = await session.LoadAsync<CampaignConfig>(configId);
            if (config == null)
            {
                config = new CampaignConfig
                {
                    Id = configId,
                    ActiveSystem = defaultSystem
                };
                await session.StoreAsync(config, configId);
            }
            else
            {
                // A CampaignConfig may already exist from an implicit ruleset default applied before
                // this campaign was formally created (e.g. world_build before create_campaign —
                // see A1 in the tool-usage audit). The explicit system chosen here always wins.
                config.ActiveSystem = defaultSystem;
            }
        }
        else if (forceLock && !campaign.IsSystemLocked)
        {
            // `campaign` is a phantom meta doc auto-vivified by an earlier read tool call against
            // this slug (e.g. get_scene, get_session_briefing) before a real creation flow ever ran —
            // it has no System/IsSystemLocked set (IsSystemLocked only ever becomes true via a real
            // create_campaign or finalize_campaign_onboarding call). Adopt it here instead of silently
            // leaving the caller's explicitly chosen system/lock discarded.
            campaign.DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedName : displayName;
            campaign.System = defaultSystem;
            campaign.IsSystemLocked = true;

            var configId = _keys.Config(normalizedName);
            var config = await session.LoadAsync<CampaignConfig>(configId);
            if (config == null)
            {
                config = new CampaignConfig
                {
                    Id = configId,
                    ActiveSystem = defaultSystem
                };
                await session.StoreAsync(config, configId);
            }
            else
            {
                config.ActiveSystem = defaultSystem;
            }
        }
        return campaign;
    }
}