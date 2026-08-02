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
    string System,
    bool IsSystemLocked,
    IReadOnlyList<PartyMemberSummary> Pcs,
    IReadOnlyList<PartyMemberSummary> Companions,
    string? LastSessionSummary,
    CampaignEntryHint EntryHint,
    string SharedCanonNote);

public record CampaignSuggestion(
    string Slug,
    string DisplayName,
    string System,
    int PcCount,
    string? LastEventSummary);

public record CampaignContextView(Campaign Campaign, CampaignPosture Posture);