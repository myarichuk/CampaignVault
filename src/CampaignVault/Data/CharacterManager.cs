using CampaignVault.Models;
using CampaignVault.Services;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface ICharacterManager
{
    Task<Character> UpsertCharacterAsync(IAsyncDocumentSession session, string campaignName, CharacterUpsertRequest character);
}

internal sealed class CharacterManager : ICharacterManager
{
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public CharacterManager(
        ILocalEmbeddingService embeddingService,
        ILogger<CharacterManager> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<Character> UpsertCharacterAsync(IAsyncDocumentSession session, string campaignName, CharacterUpsertRequest character)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
        {
            throw new ArgumentException("Character.Id is required for upsert.");
        }

        character.Id = CanonicalId.Normalize(character.Id, CanonicalId.Characters);

        var effectiveCampaignName = character.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        if (!CharacterPartyRules.TryValidate(character.IsPc, character.IsPartyCompanion, effectiveCampaignName,
                out var partyError))
        {
            throw new ArgumentException(partyError);
        }

        var existing = await session.LoadAsync<Character>(character.Id);
        Character result;
        if (existing != null)
        {
            existing.Name = character.Name;
            existing.ClassLevel = character.ClassLevel;
            existing.CurrentHp = character.CurrentHp;
            existing.MaxHp = character.MaxHp;

            existing.Notes = character.Notes;
            existing.CurrentAppearance = character.CurrentAppearance ?? existing.CurrentAppearance;
            existing.VisualTags = character.VisualTags ?? existing.VisualTags;
            existing.DistinctiveFeatures = character.DistinctiveFeatures ?? existing.DistinctiveFeatures;
            existing.Schedule = character.Schedule;
            existing.CurrentLocationId = character.CurrentLocationId;
            existing.CurrentActivity = character.CurrentActivity;
            existing.Psychology = character.Psychology ?? existing.Psychology;
            existing.Social = character.Social ?? existing.Social;
            existing.Needs = character.Needs ?? existing.Needs;
            existing.SystemStats = character.SystemStats ?? existing.SystemStats;
            existing.KeepAlive = character.KeepAlive;
            existing.IsPc = character.IsPc;
            existing.IsPartyCompanion = character.IsPartyCompanion;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            result = existing;
        }
        else
        {
            result = new Character
            {
                Id = character.Id,
                Name = character.Name,
                ClassLevel = character.ClassLevel,
                CurrentHp = character.CurrentHp,
                MaxHp = character.MaxHp,
                Notes = character.Notes,
                CurrentAppearance = character.CurrentAppearance,
                VisualTags = character.VisualTags ?? [],
                DistinctiveFeatures = character.DistinctiveFeatures ?? [],
                Schedule = character.Schedule,
                CurrentLocationId = character.CurrentLocationId,
                CurrentActivity = character.CurrentActivity,
                Psychology = character.Psychology ?? new PsychologyProfile(),
                Social = character.Social ?? new SocialProfile(),
                Needs = character.Needs ?? new NeedsProfile(),
                SystemStats = character.SystemStats ?? new SystemExtension(),
                KeepAlive = character.KeepAlive,
                IsPc = character.IsPc,
                IsPartyCompanion = character.IsPartyCompanion,
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
            };
            await session.StoreAsync(result, null, result.Id);
        }

        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);

        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: ["Character/Search"]);

        return result;
    }
}
