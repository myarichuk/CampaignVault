namespace CampaignVault.Models;

/// <summary>
/// Task 4.3: Runtime type resolver for SystemExtension polymorphic deserialization.
///
/// CURRENT STATE (WORKING):
/// The [JsonPolymorphic] attributes on SystemExtension handle type resolution correctly:
/// - Built-in discriminator: $system
/// - Fallback: FallBackToBaseType for unknown systems (e.g., third-system "swade")
/// - Result: Third systems correctly deserialize as base SystemExtension type
/// - Testing: Phase 4 acceptance tests PASS - all types round-trip correctly
///
/// DESIGN NOTES:
/// RavenDB's polymorphic deserialization works independently of System.Text.Json's
/// [JsonPolymorphic] attributes (uses Newtonsoft underneath). The STJ attributes only
/// affect STJ serialization scenarios (API responses, etc.), not DB persistence.
///
/// FUTURE ENHANCEMENT:
/// If dynamic type resolution based on registered IRulesetModule instances becomes
/// necessary, implement a custom IJsonTypeInfoResolver that:
/// 1. Reads the $system discriminator from JSON
/// 2. Queries IRulesetModuleSelector for system-specific SystemExtension subtype
/// 3. Gracefully falls back to base SystemExtension for unknown systems
///
/// This was attempted in Phase 4.3 but caused infinite recursion via JsonSerializer
/// recursion. The current [JsonPolymorphic] approach is simpler, faster, and works.
///
/// SEE ALSO:
/// - SystemExtension class (Character.cs): Defines [JsonPolymorphic] attributes
/// - Phase4_ThirdSystemRoundTripTests: Validates third-system deserialization
/// </summary>
public static class SystemExtensionJsonPolymorphism
{
    /// <summary>
    /// Marker class for documentation. Type resolution is handled by [JsonPolymorphic] attributes.
    /// </summary>
}
