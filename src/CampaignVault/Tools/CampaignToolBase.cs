using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace CampaignVault.Tools;

public abstract class CampaignToolBase
{
    protected readonly CampaignRepository _repository;
    protected readonly ICurrentCampaignContext _currentCampaign;
    protected readonly CampaignDocumentKeys _keys;

    protected CampaignToolBase(
        CampaignRepository repository,
        ICurrentCampaignContext currentCampaign,
        CampaignDocumentKeys keys)
    {
        _repository = repository;
        _currentCampaign = currentCampaign;
        _keys = keys;
    }

    protected const string NoCampaignSelectedSummary =
        "No campaign selected for this MCP session. Call select_campaign first, or pass campaignName explicitly. " +
        "When MCP_STATELESS=1, Mcp-Session-Id is unavailable and campaignName is required on every tool call.";

    protected string EffectiveCampaign(string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName.Trim().ToLowerInvariant();
        }

        if (!_currentCampaign.HasSelection)
        {
            throw new CampaignNotSelectedException();
        }

        return _currentCampaign.CurrentCampaignName;
    }

    protected bool TryGetEffectiveCampaign(string? explicitName, out string effective)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            effective = explicitName.Trim().ToLowerInvariant();
            return true;
        }

        if (!_currentCampaign.HasSelection)
        {
            effective = string.Empty;
            return false;
        }

        effective = _currentCampaign.CurrentCampaignName;
        return true;
    }

    protected Task<ToolResult<T>> ExecuteForCampaignAsync<T>(
        string? campaignName,
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
