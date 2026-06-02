# Phase 6: Open-World & Transient Architecture Design

## Executive Summary
The goal of Phase 6 is to upgrade CampaignVault's engine from a "room-to-room" tracker into a massively scalable "Schrödinger's World" open-world engine. 
It establishes Opt-In Persistence, Engine-Driven Garbage Collection, and uses the `WorldPressure` system as a synthetic "Co-DM" to gently nag the LLM out of its natural lazy tendencies, ensuring the persistent database remains healthy and perfectly linked.

---

## 1. The Core Philosophy: "Schrödinger's World"
In a TTRPG, the DM describes hundreds of flavor elements (a baker, a cat, a passing carriage) that players never interact with. 
If the LLM is forced to create a database document for every passing carriage, the system will buckle. 

**Opt-In Persistence:** Everything is flavor text (and lives only in the LLM's short-term memory) *until* the players meaningfully interact with it. 
- If players ask about a baker, it's flavor.
- If players rob the baker, the LLM uses `commit` to permanently anchor the baker into the database.

---

## 2. Location Model Upgrades
We are avoiding redundant arrays like `AdjacentLocationIds` because the existing `List<LocationExit> Exits` property already perfectly maps adjacency with added benefits (Lock Conditions, Directions). 

We will add the following properties to `Location.cs` to support open-world crawling and GC:

* `public List<string> PointsOfInterest { get; set; } = [];` 
  * A lightweight string array for the LLM to log flavor nodes (e.g., `["A blacksmith", "A dirty tavern"]`) without anchoring full documents.
* `public string? AmbientCrowd { get; set; }` 
  * A narrative hint (e.g., `"10-20 rough sailors"`) used to prompt the LLM to generate transient NPCs when the room is empty.
* `public double? LastVisitedDay { get; set; }` 
  * Updated automatically by the engine whenever `GetSceneAsync` is called. Used for transient garbage collection.

---

## 3. The "Laziness-Proof" Location Handlers
LLMs are lazy. If they have to execute two separate mutations to link a newly created cellar to a tavern, they will forget the second step, breaking map adjacency.

**The Fix:**
We will introduce two new mutation handlers to the `commit` tool: `$type: location_create` and `$type: location_update`.

To counter laziness, `location_create` will feature an auto-linking mechanism:
```json
{
  "$type": "location_create",
  "locationId": "locs/tavern_cellar",
  "name": "Dank Cellar",
  "connectedFromLocationId": "locs/tavern",
  "connectionDescription": "A wooden trapdoor leading down"
}
```
**Engine Behavior:** When the Engine processes this mutation, it creates the Cellar, but it *also* automatically fetches `locs/tavern` and injects an Exit pointing to the new Cellar. The LLM does half the work, and the map stays perfectly linked.

---

## 4. Transient Auto-Garbage Collection (Engine GC)
If the LLM is forced to manually despawn the 20 transient NPCs they created in a market, they will ignore the task, causing database bloat. 

**Engine GC:**
- An NPC is considered **Transient** if `Schedule == null`.
- During `AdvanceWorldAsync`, the Engine will sweep the database. If a Transient NPC is in a location where `LastVisitedDay` is older than 1 day (meaning the players have left the chunk), the Engine will automatically despawn them (e.g., set `CurrentLocationId = null` or delete).

**Opt-In Persistence (NPC Promotion):**
If the LLM wants to save a favorite transient, they use `commit` with `$type: schedule_change` (or similar) to give the NPC a permanent routine. Once they have a Schedule, the Engine GC will ignore them forever.

---

## 5. The "Co-DM" WorldPressure Mechanics
To further counter LLM laziness, the Engine will inject synthetic warnings directly into the `WorldPressure` array returned by `GetSceneAsync`. Because LLMs are highly reactive to immediate warnings, this acts as a built-in "nag" to keep the database healthy.

### A. The "Void Scene" Pressure (Anti-Hallucination)
If the LLM calls `get_scene("locs/unbuilt_cellar")` and it doesn't exist, the engine will NOT crash. It will return a blank scene and inject:
> *"ENGINE WARNING: You requested 'locs/unbuilt_cellar' but it does not exist in the database! You are hallucinating. Use the `commit` tool (`$type: location_create`) immediately to anchor this room."*

### B. The "Dead End" Pressure (Anti-Orphaning)
If `get_scene` detects `Exits.Count == 0` (and `Type != Region`), it will inject:
> *"ENGINE WARNING: This location has no Exits. The players are soft-locked. Use `location_update` to add an exit back to the previous area."*

### C. The "Ghost Town" Pressure (Ambient Repopulation)
If `PresentNPCs.Count == 0` but the location has an `AmbientCrowd` string, the engine will inject:
> *"NARRATIVE PROMPT: This location is currently empty, but expects '{AmbientCrowd}'. Consider spawning flavorful transient NPCs via commit."*
