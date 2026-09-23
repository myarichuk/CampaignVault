# CampaignVault

**A living world engine for TTRPGs that prevents hallucination and scales across sessions.**

Turn any LLM into a DM that remembers your world. CampaignVault tracks NPCs, time, combat, and consequences across sessions without losing coherence. It handles simulation, mechanical resolution, and scene assembly—leaving narrative to you.

Supports **D&D 5e**, **Pathfinder 2e**, and **Narrative** (d6 Oracle) rulesets.

---

## What It Does

- **Persistent World State** — NPCs, locations, lore, factions, and quests survive session to session
- **Prevents Hallucination** — All scenes and encounters pull from the database, not LLM imagination
- **Time Moves Forward** — Days advance, resources recover, rumors decay, quest deadlines approach
- **Atomic Mutations** — Resolve combat, make NPC changes, and advance time in a single transaction
- **Psychological NPCs** — Track wants, fears, moods, relationships so the LLM can roleplay authentically
- **Simulation** — Background rules evolve the world: NPC needs accumulate, fatigue sets in, factions shift stance
- **Open Play** — Add flavor on the fly; the engine auto-cleans transient content when areas go dormant

---

## Quick Start

### Local Play (Your Computer)

```bash
# 1. Build the Docker image
docker build -t campaignvault:latest -f Dockerfile .

# 2. Run the server
docker run -p 5275:5275 -e CAMPAIGN_DB_PATH=/app/data -v campaign_data:/app/data campaignvault:latest

# 3. Verify it's working
curl http://localhost:5275/health

# 4. Point your LLM to http://localhost:5275
# The MCP server auto-discovers all tools
```

For detailed setup, deployment, and configuration: [INSTALLATION.md](./INSTALLATION.md)

---

## Core Workflow

| Goal | Tool |
|------|------|
| Start a session | `start_session` — time, active quests, NPCs in crisis, pressures |
| Explore a scene | `get_entity` on a location ID — people, items, rumors, combat |
| Understand an NPC | `get_entity` on a character ID — psychology, memories, mood, pressures |
| Resolve an action | `take_turn` — HP changes, item transfers, time passing, all atomic |
| Find something | `search_world` — keyword search across everything |
| Advance time | `advance_world` — days pass, simulation rules fire, world evolves |

For the full tool list and patterns, call `get_help` inside a campaign (built-in DM manual with examples).

---

## Features

- **Multi-ruleset support** — D&D 5e (SRD 5.1), Pathfinder 2e (ORC License), and a narrative-first system
- **Combat tracking** — Initiative, turn order, HP, status effects, resolvers per system
- **Atomic transactions** — Batch changes across NPCs, items, time, and state without conflicts
- **NPC minds** — Wants, fears, moods, relationships, memories, custom needs (paranoia, wanderlust, etc.)
- **Situational awareness** — Proactive warnings about ticking clocks, broken quests, missing links
- **Engagement & spatial anchors** — Track who is doing what to whom, and where they are relative to each other
- **Multi-campaign support** — Run multiple worlds in the same server
- **Unified search** — Keyword search across lore, characters, and locations
- **Climate & gear** — Temperature, insulation, environmental hazards surface as narrative pressure

---

## Rulesets

### D&D 5e
Official SRD 5.1 rules: ability checks, saving throws, advantage/disadvantage, spell slots, multiclass stacking.

### Pathfinder 2e
Full ORC License support: four degrees of success, action economy, conditions, persistent damage.

### Narrative
A pure story-focused ruleset with d6 Oracle resolution. No classes, levels, or hit points—ideal for indie games and experimental systems.

---

## Skills Requirement & Tool Schema Mode

CampaignVault assumes your LLM client **loads the skills in [`claude_skills/`](./claude_skills)** (or an equivalent unified skill set). The skills are the source of truth for which `$type` verbs exist and how to use them.

`take_turn`'s advertised input schema is controlled by `CampaignVault:ToolSchemaMode` (`appsettings.json`, or env var `CampaignVault__ToolSchemaMode`):

| Mode | What tools/list advertises | Use when |
|------|----------------------------|----------|
| `Stub` (default) | The request envelope, plus `changes[]` items that only require `$type`. Verbs/fields are **not** listed; the description tells the model to consult its skills and call `get_commit_schema` (no args = index, `type=X` = one verb's fields). ~300 tokens. | Normal operation with skills loaded |
| `Full` | A `$defs` entry per `$type` (~7k tokens). | Clients with no skill mechanism, or debugging schemas |

**Why Stub is the default:**
- **Cost.** The full schema reprinted on every connector discovery cost ~7k tokens per beat, most of it verbs a campaign never uses (plugin verbs included).
- **Staleness.** Some clients (e.g. Grok Web) cache tool schemas. A cached full schema goes wrong when plugins or fields change; a constant stub can never be stale.
- **One source of truth.** Skills describe the verbs; `get_commit_schema` returns live server truth on demand; the server validates every commit regardless.

---

## Documentation

- **[INSTALLATION.md](./INSTALLATION.md)** — Docker, local dev, remote deployment (ngrok, Fly.io), authentication
- **[PLUGINS.md](./PLUGINS.md)** — Homebrew content packs (custom items/spells/creatures), new rulesets, and code plugins; includes the trust model and an installation guide
- **[ARCHITECTURE.md](./ARCHITECTURE.md)** — System design, simulation rules, pressure system, ruleset integration
- **[System Prompt](./recommended-system-prompt.md)** — Copy-paste LLM system prompt (or [opencode-specific](./recommended-system-prompt.opencode.md))
- **[Licensing](./LICENSING.md)** — Game content attribution and legal notes
- **[Commercial Use](./COMMERCIAL.md)** — Dual licensing and RavenDB Community Edition requirements

---

## Example: Campaign Seed & Play

```
1. Create campaign: /create_campaign slug="campaign-name" ruleset=Dnd5e
2. Seed world: /world_build with locations, NPCs, items, quests
3. Start session: /start_session campaignName="campaign-name"
4. Play: /get_entity on locations/NPCs, /take_turn for actions
5. Advance time: /advance_world to trigger simulation and pressure
```

See `get_help` for full patterns and copy-paste examples.

---

## Development

Code is organized in `src/CampaignVault/`:

- **Models** — `Character`, `WorldChanges`, ruleset extensions
- **Repository** — Database access, scene assembly, queries
- **Rulesets** — D&D 5e, PF2e, Narrative resolvers and mechanics
- **Simulation** — Background rules (time, NPC needs, quest decay, etc.)
- **Pressure** — Read-side warnings about world state
- **Tools** — LLM-facing MCP tool implementations

Run tests:
```bash
dotnet test
```

Build and run locally:
```bash
dotnet run
```

---

## Contributing

We welcome contributions. Please:

1. **Read [ARCHITECTURE.md](./ARCHITECTURE.md)** for an overview of the system design
2. **Add tests** for new features (in `tests/CampaignVault.UnitTests/`)
3. **Keep simulation rules and pressure system in sync** — if you add a new change type, add a handler; if you add a new entity property, consider pressure-side signals
4. **Test with a real campaign** before submitting — run `dotnet run` locally and test against `localhost:5275`
5. **Follow the code style** — look at existing handlers and rules for patterns

Bugs and feature requests: open an issue on GitHub.

---

## Licensing

**Code:** Dual-licensed
- **Personal/non-commercial use:** Free (PolyForm Noncommercial 1.0.0)
- **Commercial use:** Requires a commercial license

**Game Content:**
- **D&D 5e:** Official SRD (CC-BY-4.0)
- **Pathfinder 2e:** ORC License

**Important:** RavenDB Community Edition (used locally) requires a free license key for production. See [COMMERCIAL.md](./COMMERCIAL.md).

For full details: [LICENSING.md](./LICENSING.md)

---

## Status

CampaignVault is in active development. The core engine, simulation, and tool surface are stable. We're continuously improving ruleset coverage, pressure signals, and LLM integration patterns.

See [REFACTOR_STATUS.md](./REFACTOR_STATUS.md) for recent changes and current work.
