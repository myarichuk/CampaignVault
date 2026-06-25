using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace CampaignVault.Tools;

public abstract class CampaignToolBase
{
    protected readonly CampaignRepository _repository;
    protected readonly CampaignDocumentKeys _keys;

    protected CampaignToolBase(
        CampaignRepository repository,
        CampaignDocumentKeys keys)
    {
        _repository = repository;
        _keys = keys;
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
                return new ToolResult<T>(false, Error: ToolErrors.NoCampaignSelected, Summary: ex.Message);
            }
            catch (ConcurrencyException)
            {
                if (++actionAttempt <= maxRetries) continue;
                return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "State changed mid-operation. Re-fetch and retry.");
            }
            catch (Exception ex)
            {
                return new ToolResult<T>(false, Error: ToolErrors.InternalError, Summary: ex.Message);
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
                catch (ConcurrencyException)
                {
                    if (++saveAttempt <= maxRetries) continue;
                    return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "Commit failed due to concurrent modification. Re-fetch and retry.");
                }
            }

            _repository.SanitizeForToolResponse(result.Data);
            return result;
        }
    }

    protected async Task<Campaign> GetOrCreateCampaignMetaAsync(IAsyncDocumentSession session, string normalizedName, RulesetSystem defaultSystem, string? displayName = null, bool forceLock = false)
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
        }
        return campaign;
    }
}