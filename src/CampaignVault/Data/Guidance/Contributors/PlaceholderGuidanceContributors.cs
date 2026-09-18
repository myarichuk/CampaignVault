namespace CampaignVault.Data.Guidance.Contributors;

/// <summary>
/// These guidance topics are NOT YET IMPLEMENTED. Previously this file held stub classes implementing
/// IGuidanceContributor that were picked up by ConventionRegistration's assembly scan and invoked by
/// GuidanceOrchestrator on every call while silently returning an empty hint list — actively wired into
/// the hot path while delivering nothing, which made the orchestrator appear to cover these topics when
/// it didn't. Removed from the IGuidanceContributor scan entirely until one is actually implemented;
/// tracked here as a punch list rather than left registered-but-empty.
///
/// - FirstWorldBuildGuidanceContributor (World scope): checks SeedCoverage.Locations == 0.
/// - SpellcastingGuidanceContributor (Scene scope): checks party has spell slots and a Spell-category action.
/// - ItemDamageGuidanceContributor (Scene scope): checks item state degradation.
/// - PlotThreadStalenessGuidanceContributor (World scope): reuses PlotThreadStalenessContributor detection.
/// - SystemStatsGuidanceContributor (Scene scope): reuses IncompleteSystemStatsPressureContributor detection.
/// - NarrativeFocusGuidanceContributor (World scope): checks Campaign.NarrativeFocus empty after N commits.
/// - TimeRecordingGuidanceContributor (World scope): checks minutesElapsed never used but commits > threshold.
/// </summary>
internal static class PlaceholderGuidanceContributorsNotYetImplemented;
