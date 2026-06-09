# Design Spec: Grouping Keys Refactoring

**Date:** 2026-06-10
**Status:** Approved
**Topic:** Replacing Magic Strings in Pressure Grouping Keys with Constants and Helpers

---

## 1. Objective
Refactor the pressure grouping keys used throughout the `CampaignVault` codebase from hardcoded magic strings to localized class constants and type-safe factory methods. This increases code maintainability, reduces copy-paste errors, and enables compiler-checked references in test assertions and other consumers.

---

## 2. Design Decisions
* **Approach:** Pure Distributed Constants (localized within the generating classes).
* **Static Keys:** Declared as `public const string` inside the corresponding generator class.
* **Dynamic Keys:** Declared as `public static string` factory methods accepting parameters to construct the key dynamically in a type-safe manner.
* **Shared Keys:** Where keys are shared between classes (such as `PressureHintEnricher` and `CharacterDistressPressureContributor`), the helper class will reference the constants and factory methods of the primary contributor class to avoid code duplication.
* **Tests:** Update all assertions in `CampaignRepositoryTests.cs` to use the new constants/factory methods instead of hardcoded strings.

---

## 3. Mapping Specifications

### 3.1 Contributor & Core Engine Key Mapping

| Class / File | Type | Constant / Helper Declaration | Grouping Key Value |
| :--- | :--- | :--- | :--- |
| `AgingRumorPressureContributor` | Static | `public const string GroupingKey = "Rumor:Aging";` | `"Rumor:Aging"` |
| `DanglingItemPressureContributor` | Static | `public const string GroupingKey = "Item:DanglingHolder";` | `"Item:DanglingHolder"` |
| `FactionEconomyPressureContributor` | Dynamic | `public static string GetEconomicDemandGroupingKey(string factionId, string demand) => $"Faction:EconomicDemand:{factionId}:{demand}";` | `$"Faction:EconomicDemand:{factionId}:{demand}"` |
| `FactionOpportunisticPressureContributor` | Dynamic | `public static string GetOpportunisticGroupingKey(string factionId) => $"Faction:Opportunistic:{factionId}";` | `$"Faction:Opportunistic:{factionId}"` |
| `FactionRecentEventPressureContributor` | Static | `public const string PresenceChangeGroupingKey = "Faction:PresenceChange";`<br>`public const string ReputationGroupingKey = "Faction:Reputation";` | `"Faction:PresenceChange"`<br>`"Faction:Reputation"` |
| `FactionTerritoryPressureContributor` | Dynamic | `public static string GetHostileTerritoryGroupingKey(string factionId) => $"Faction:HostileTerritory:{factionId}";`<br>`public static string GetAlliedTerritoryGroupingKey(string factionId) => $"Faction:AlliedTerritory:{factionId}";` | `$"Faction:HostileTerritory:{factionId}"`<br>`$"Faction:AlliedTerritory:{factionId}"` |
| `LocationConnectivityPressureContributor` | Static | `public const string MissingReverseLinkGroupingKey = "Location:MissingReverseLink";` | `"Location:MissingReverseLink"` |
| `LocationFlavorPressureContributor` | Static | `public const string EmptyExpectsCrowdGroupingKey = "Location:EmptyExpectsCrowd";`<br>`public const string EnvironmentalTagsGroupingKey = "Location:EnvironmentalTags";`<br>`public const string FlavorVacuumGroupingKey = "Location:FlavorVacuum";`<br>`public const string DeadEndSuggestionGroupingKey = "Location:DeadEndSuggestion";` | `"Location:EmptyExpectsCrowd"`<br>`"Location:EnvironmentalTags"`<br>`"Location:FlavorVacuum"`<br>`"Location:DeadEndSuggestion"` |
| `LocationHallucinationPressureContributor` | Static | `public const string GroupingKey = "Location:Hallucinated";` | `"Location:Hallucinated"` |
| `LocationIntegrityPressureContributor` | Static | `public const string MissingTravelCommitGroupingKey = "Location:MissingTravelCommit";`<br>`public const string NoExitsGroupingKey = "Location:NoExits";` | `"Location:MissingTravelCommit"`<br>`"Location:NoExits"` |
| `MemoryDecayPressureContributor` | Dynamic | `public static string GetMemoryDecayGroupingKey(string npcId, string topic) => $"MemoryDecay:{npcId}:{topic}";` | `$"MemoryDecay:{npcId}:{topic}"` |
| `NeverVisitedTransientPressureContributor` | Static | `public const string GroupingKey = "Location:NeverVisitedTransients";` | `"Location:NeverVisitedTransients"` |
| `QuestDeadlinePressureContributor` | Static | `public const string ApproachingDeadlineGroupingKey = "Quest:ApproachingDeadline";`<br>`public const string MissedDeadlineGroupingKey = "Quest:MissedDeadline";` | `"Quest:ApproachingDeadline"`<br>`"Quest:MissedDeadline"` |
| `SceneQuestStalenessPressureContributor` | Static | `public const string GroupingKey = "Quest:Stale";` | `"Quest:Stale"` |
| `StuckTravelPressureContributor` | Static | `public const string GroupingKey = "Travel:Interrupted";` | `"Travel:Interrupted"` |
| `TransientQuestGiverPressureContributor` | Static | `public const string GroupingKey = "Character:TransientQuestGiver";` | `"Character:TransientQuestGiver"` |
| `UnresolvedEventPressureContributor` | Static | `public const string GroupingKey = "Event:Unresolved";` | `"Event:Unresolved"` |
| `UrgentInitiativePressureContributor` | Static | `public const string GroupingKey = "NpcInitiative:Urgent";` | `"NpcInitiative:Urgent"` |
| `Dnd5eExhaustionPressureContributor` | Static | `public const string GroupingKey = "Character:Attribute:exhaustion";` | `"Character:Attribute:exhaustion"` |
| `DefaultSimulationEngine` | Static | `public const string RumorsGroupingKey = "Simulation:Rumors";` | `"Simulation:Rumors"` |

### 3.2 Shared Keys Mapping

* **`CharacterDistressPressureContributor` (Primary Holder):**
  * `public const string CriticallyWoundedGroupingKey = "Character:CriticallyWounded";`
  * `public const string DyingGroupingKey = "Character:Dying";`
  * `public const string MoraleGroupingKey = "Character:Attribute:Morale";`
  * `public const string WillpowerGroupingKey = "Character:Attribute:Willpower";`
  * `public const string TemperatureLowGroupingKey = "Character:Attribute:TemperatureLow";`
  * `public const string TemperatureHighGroupingKey = "Character:Attribute:TemperatureHigh";`
  * `public static string GetStatusGroupingKey(string statusName) => $"Character:Status:{statusName}";`
  * `public static string GetNeedGroupingKey(string needKey) => $"Character:Need:{needKey}";`
  * `public static string GetAttributeGroupingKey(string attributeKey) => $"Character:Attribute:{attributeKey}";`
  * `public static string GetRelationshipGroupingKey(string targetId) => $"Character:Relationship:{targetId}";`

* **`PressureHintEnricher` (Consumer):**
  * References `CharacterDistressPressureContributor.CriticallyWoundedGroupingKey`
  * References `CharacterDistressPressureContributor.DyingGroupingKey`
  * References `CharacterDistressPressureContributor.GetNeedGroupingKey(kvp.Key)`

### 3.3 Tools Mapping

* **`CampaignTools`:**
  * `public const string EventGroupingKey = "Simulation:Event";`
  * `public const string UrgentGroupingKey = "NpcInitiative:Urgent";`

---

## 4. Test Updates
All magic string assertions in `CampaignRepositoryTests.cs` (lines 224-233) will be updated to point to the new constants and static factory methods on `CharacterDistressPressureContributor`.

---

## 5. Verification Plan
* Ensure code compiles successfully.
* Run all unit tests inside `tests/CampaignVault.Tests` to verify no regressions in pressure grouping or assertions.
