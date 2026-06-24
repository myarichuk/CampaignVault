using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public enum CampaignEntryHint
{
    NewCampaign,
    Resume,
    AddPc,
    AddCompanion
}

public record PartyMemberSummary(string Id, string Name, bool IsPc);

public record CampaignPosture(
    string Slug,
    string DisplayName,
    RulesetSystem System,
    bool IsSystemLocked,
    IReadOnlyList<PartyMemberSummary> Pcs,
    IReadOnlyList<PartyMemberSummary> Companions,
    string? LastSessionSummary,
    CampaignEntryHint EntryHint,
    string SharedCanonNote);

public record CampaignSuggestion(
    string Slug,
    string DisplayName,
    RulesetSystem System,
    int PcCount,
    string? LastEventSummary);

public record SelectCampaignResult(
    string Slug,
    CampaignPosture? Posture = null,
    IReadOnlyList<CampaignSuggestion>? Suggestions = null);

public record CampaignContextView(Campaign Campaign, CampaignPosture Posture);