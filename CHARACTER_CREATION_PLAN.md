# Character Creation Plan

A dedicated character builder that replaces the onboarding questions `pc_creation` →
`pc_roster` → `starting_level`, and a companion builder that produces a simplified per-system
stat block.

Both are **driven by rules data on the server**:
- A per-system recipe file says *what* to ask.
- C#, behind an optional SDK interface, enforces the rules that data shouldn't try to
  express.
- The existing bootstrap pipeline derives the numbers.

So the client never works out rules itself. The server builds the sheet directly from the
player's choices, with no LLM step in between. Plugins can add content to every list the
builder shows.

Written 2026-10-01 against `master` @ `925f0b6` plus the uncommitted onboarding work listed
under "Already done". Line numbers are from that tree, and `~` marks approximate ones.
Re-check them before starting a phase.

---

## Already done (this session, uncommitted)

These are context, not tasks. The builder builds on them.

- **One DM conversation for the whole setup.** The brainstorm chat carries across questions
  with "NEXT QUESTION" dividers (`VaultController.MoveToQuestion`,
  `OnboardingBrainstorm.MarkerRole` / `ModelMessages`). WRITE IT UP FROM OUR CONVERSATION
  appears on the question screen.
- **Plot questions moved** right after the world question (`OnboardingQuestionCatalog.cs`).
  The Narrative system skips `starting_level`.
- **Visible limits, nothing silent:**
  - 16k-character message cap with a counter; SEND is disabled over it.
  - 48k conversation budget; dropped messages are marked "NO LONGER SENT".
  - 6k soft cap on the write-up, with a warning.
- **Modal layout:**
  - A fixed `Dock` slot between the scrolling body and the footer (`Layers.cs`).
  - Head and foot don't shrink; multi-line fields stop at 220px tall.
- **Release CI:** the `UnityEditor.OSXStandalone` reference is now looked up by reflection
  (`Editor/MacArchitecture.cs`).

---

## Why

- **The level field is a free-text box.** `starting_level` is a Number question rendered as
  a text box. A mistyped value is rejected and the same question comes back; that's how the
  smoke test looped.
- **The PCs are only described, never built.** `describe-now` collects "Name — ancestry and
  class, concept" lines. `OnboardingBrief.cs:86-96` then tells the DM model to
  `world_build` full sheets, which is where stats get invented, and a model can make
  mistakes there.
- **The data layer exists but nothing guides the player through it:**
  - `lookup kind:'handbook'`: classes, races, backgrounds, feats, conditions and
    creatures, including homebrew (`CampaignManagementTools.cs:350`, `GetSystemHandbook`
    at `:382`).
  - `lookup kind:'spells'` by class and level (`:352`).
  - `lookup kind:'creatures'` with a level range (`:360`).
  - `lookup kind:'level_up'` (`GetPendingLevelUpChoices` at `:444`, backed by
    `ProgressionDefinitionProvider.GetPendingChoices` at `:119`).
- **The numbers are already derived by code.** `CharacterBootstrapApplier` and the
  `*Derive*Step`s (`Rulesets/Bootstrap/`) fill in HP, proficiency, saves, background
  skills, defense, spellcasting and racial bonuses from a few inputs. The builder only has
  to collect choices.

---

## Architecture

### Two layers: data says what to ask, C# enforces rules

```
RulesetData/<system>/creation.yaml   ──►  RecipeCharacterCreation (generic engine, core)
RulesetData/<system>/statblock.yaml  ──►        │  steps / options / validate / preview / commit
                                                ▼
             named validators (C#, per system) ◄┤
             Bootstrap pipeline (existing C#)  ◄┘  derives HP, AC, saves… for preview and commit
```

- **Recipe (data).** An ordered list of steps per creation kind (`pc`, `companion`).
  - Each step has a `key`, a `kind` from a small fixed set, a `source` (which provider or
    lookup it draws options from) and simple constraints.
  - Allowed constraints: counts, `countFrom`/`exclude` path references such as
    `class.skillChoices.count`, a `when` condition on a path, and `validators: [names]`.
  - **No expression language.** Anything beyond a path reference or a simple comparison is
    a named validator written in C#. This keeps the YAML from turning into a badly designed
    programming language.
- **Step kinds (fixed set):**
  - `pickOne`, `pickN`
  - `abilityScores` (standard array, point buy, roll)
  - `allocate` (PF2e boosts)
  - `spells`, `feats`, `identity`
  - `levelChoices` (for levels above 1)

  The client builds **one widget per step kind**, not one screen per system, so a new
  system or a plugin system needs no client work.
- **Stat block schema (data).** `statblock.yaml` lists the companion stat block's fields:
  name, type (`int`, `text`, `list`, `abilities`, `attacks`), range and grouping. The
  client draws a generic editor and the parchment from it. That also removes the
  `IsPf2e`-style branches from `CharacterSheetOverlay`.
- **SDK interface (C#):**
  - `ICharacterCreation { System; Steps(kind, ctx); Options(step, draft, ctx); Validate(draft, ctx); }`
  - `IRecipeValidator { Name; Validate(draft, step, ctx) }`

  Validators are discovered by convention scanning, like `IPluginTraitsUpgrader` and
  `IPluginGuidanceContributor`. `Commit` and `Preview` stay in core: they always go through
  the bootstrap pipeline and `world_build`.
- **No new member on `IRulesetModule`.** It's a published SDK interface
  (`CampaignVault.PluginSdk/Rulesets/IRulesetModule.cs`), so adding a member would break
  out-of-tree plugins. Instead, `ICharacterCreation` is a separate optional interface that
  `RulesetModuleSelector` (`Rulesets/IRulesetModuleSelector.cs`) looks up. When a system
  doesn't provide one, it falls back to `RecipeCharacterCreation` over that system's
  `creation.yaml`.

Example recipe (shape only):

```yaml
# RulesetData/dnd5e/creation.yaml
pc:
  - { key: race,       kind: pickOne, source: races }
  - { key: class,      kind: pickOne, source: classes }
  - { key: background, kind: pickOne, source: backgrounds }
  - key: abilities
    kind: abilityScores
    methods:
      standardArray: [15, 14, 13, 12, 10, 8]
      pointBuy: { budget: 27, min: 8, max: 15, cost: {8: 0, 9: 1, 10: 2, 11: 3, 12: 4, 13: 5, 14: 7, 15: 9} }
      roll: 4d6dropLowest
  - { key: skills, kind: pickN, source: classSkills, countFrom: class.skillChoices.count, exclude: background.skillProficiencies }
  - { key: spells, kind: spells, when: "class.casterType != None" }
  - { key: gear,   kind: pickN, source: startingEquipment, optional: true }
  - { key: identity, kind: identity }
companion:
  - { key: statblock, kind: identity, schema: statblock }
```

---

## Inventory (verified)

| | D&D 5e (`RulesetData/dnd5e`) | PF2e (`RulesetData/pf2e`) | SWADE |
|---|---|---|---|
| Classes / progressions | 12 / 12 | 8 / **4** (cleric, fighter, rogue, wizard) | empty folders |
| Races / ancestries | 9 | 3 (dwarf, elf, human); **no heritage data** | empty |
| Backgrounds | 5 (`skillProficiencies`, `feature`) | 2 | empty |
| Feats | 1 | 1,807: `level` on all, `classes` on 902, **no category** | empty |
| Spells | 319 (+ NecromancersGrimoire plugin) | present | empty |
| Creatures (companion templates) | 9 | 4 | empty |

The choice schema is in place: `ProgressionDefinition.cs` `LevelUpChoiceDefinition` with
`ChoiceType { Enum, AsiOrFeat, SpellSelection, FeatSelection, FreeText }` and `Options`
holding `ChoiceOption { Id, Label, Description }`. Choices are recorded on the character as
`Character.LevelUpChoices` (`Character.cs:422`,
`LevelUpChoiceRecord { Level, Key, Value }`).

Character fields:
- `Dnd5eExtension.cs:14-83`: the six ability scores, `Level`, `ClassLevels`, `Race`,
  `Background`, `Feats`, `SkillModifiers`, `SavingThrowModifiers`.
- `Pf2eExtension.cs:24-102`: ability mods, `Level`, `Ancestry`, `Heritage`, `Background`,
  and the four feat lists.
- Companions: `Character.IsPartyCompanion` (`:84`), with fixed HP in
  `SystemExtension.StatBlockHp` (`:383`).

The creature template shape (`dnd5e/creatures/goblin.yaml`: `level`, `challengeRating`,
`hp`, `defense`, `skills[]`, `abilities[]` as prose lines) is the starting point for
`statblock.yaml`.

### Data gaps (block specific phases)

1. **5e class skill choices.** The class YAML has no "choose N from list".
   `Dnd5eDeriveProficiencyStep.cs` (~`:100-111`) only derives background skills and tells
   the model to patch class skills in by hand. *Blocks phase 2.*
2. **Spells known or prepared aren't stored anywhere.** There's no field on
   `Dnd5eExtension` or `Pf2eExtension`, and `SpellSlotValidator` checks slots only. The
   known counts exist only as prose (e.g. `bard.yaml:14` "Know 2 cantrips, 4 spells").
   *Blocks the spells step.*
3. **5e starting equipment packages.** Not in the YAML. *The gear step is optional and
   falls back to "the DM equips to fit".*
4. **PF2e:**
   - Progressions are missing for bard, druid, ranger and witch.
   - There's no heritage data.
   - Feats have no `category` (ancestry, skill or general) and no ancestry or skill tags.

   *Blocks phase 6.* `scripts/generate_pf2e_feats.py` can emit these from Archives of
   Nethys traits. Regenerating needs network access (CLAUDE.md).

---

## Phase 0: Plugin rules content (inheritance and overrides)

> **Status: done (2026-10-01, committed in `df4b7d9`).**
> - `RulesetContentLayers<T>` (`Data/Templates/`) now layers all 10 providers.
> - `TemplateEdits` handles `+:`, `-:`, `patches:` and inherited `requires:`.
> - `RulesetTemplate.Requires` is in the SDK; `FeatDefinition.Requires` moved there.
> - Tests: `PluginRulesContentTests` (11).
> - The resolved-data snapshot is identical before and after (2,755 templates).
> - Unit suite: 2084 total, 0 failed, 2 skipped (both skipped in source).
> - Also fixed here: the race, class, creature and progression providers *replaced* core data with any plugin
>   root (and could extract embedded defaults into the plugin folder). Every provider now layers.
> - Not done: the SDK `<Version>` bump for the new public `RulesetTemplate.Requires`. Left for the release.

**Goal:** plugin-added content behaves predictably in every list the builder shows. Plugin
backgrounds and races already show up: each provider loads the core folder first, then every
plugin root (`PluginDataRoots.Additional`), and `lookup kind:'handbook'` reads from the
providers. What's missing is control.

- **0.1 Name collisions are logged.** Today a same-named plugin entry silently replaces the
  core one (`raw[name] = def`, e.g. `BackgroundDefinitionProvider.cs:69`; the same pattern
  is in the Condition, Feat, Item, ResourcePool and Spell providers). Keep "last wins" for
  compatibility, but log a warning naming both sources. Make plugin load order
  deterministic (sorted by plugin id) and log it.
- **0.2 List operations in inheritance.** `inherits: [parent]` works
  (`RulesetTemplateResolver`), but every `Merge` replaces lists. For example
  `RaceDefinition.cs:18`:
  `Traits = child.Traits.Count > 0 ? child.Traits : parent.Traits`.
  So a `moon_elf` that adds one entry must copy all of elf's. Add `<list>+:` (append) and
  `<list>-:` (remove) alongside plain `<list>:` (replace), in a shared helper used by every
  `Merge`.
  - It applies to all list fields: race traits and languages; background skills, tools and
    languages; class pools; feat classes and effects.
  - Plain `<list>:` keeps today's meaning, so existing YAML is unchanged.
- **0.3 `patches:` entries.** A plugin file with `patches: elf` merges into the existing
  `elf` using 0.2's rules instead of replacing it or needing a new name.
  - Patches apply after all base loads, in plugin id order.
  - A patch whose target doesn't exist is skipped with a warning, matching how
    `ResolveAll` skips broken inheritance chains.
- **0.4 Restrict any template to a plugin or mode.** Today `requires: { plugin, mode }` is
  feat-only (`FeatDefinition.cs:41`, checked by `FeatEffectRules.PluginAvailable` at
  `:59`). Move `Requires` to `RulesetTemplate` so backgrounds, races, classes, creatures
  and spells can be restricted too. The handbook and the builder's `options` filter on it.
- **0.5 Docs.** PLUGINS.md "YAML Schema" (`:500`) documents `+:`, `-:`, `patches:`,
  `requires:` and the collision warning.

**Done when:**
- Unit tests cover:
  - append, remove and replace on a race and a background;
  - `patches:` on a core race from a test plugin root;
  - a missing patch target being skipped with a warning;
  - a collision warning being logged;
  - a background restricted to a plugin that isn't loaded being hidden from the handbook.
- Existing ruleset data loads with identical results (snapshot of the resolved handbook
  before and after).
- The server suite is green.

## Phase 1: Server creation API and recipe engine

> **Status: done (2026-10-01, committed in `df4b7d9`).**
> - SDK (`PluginSdk/Rulesets/Creation/CharacterCreation.cs`): `ICharacterCreation`, `IRecipeValidator`,
>   `CreationStep`, `CharacterDraft`, `CreationContext`, the choice shapes. `IRulesetModuleSelector.GetCreation`
>   is a default interface member, so existing selector fakes still compile.
> - Core (`Rulesets/Creation/`): `RecipeCharacterCreation`, `CreationSources`, `RecipeValidators` (8),
>   `DraftCharacterMapper`, `CharacterCreationService` (`ValidateRecipes` runs in the startup build callback).
>   `CreationRecipeProvider` layers `creation/` and `statblocks/`.
> - Tool `character_builder` (`Tools/CharacterBuilderTools.cs`): steps, options, preview, commit. Commit builds
>   its own `WorldBuilderTools`, so it is the world_build path.
> - Data: `dnd5e/creation/{pc,companion}.yaml`, `dnd5e/statblocks/companion.yaml`, `skillChoices` on the 12
>   classes, `cantripsKnown`/`spellsKnown` per level and `preparedSpells` on the 8 caster progressions,
>   `spells` (`SpellRepertoire`) on both extensions. The handbook shows `skillChoices`.
> - Tests: `CharacterCreationTests` (14), `CharacterBuilderToolsTests` (4), plus
>   `ClientOnlyTools_AreServedOnBuild_ButNeverListed`. Unit suite: 2104 total, 0 failed, 2 skipped (both in source).
> - **Changed from the plan:**
>   - Recipes are one file per kind (`creation/pc.yaml`) instead of one `creation.yaml` with `pc:`/`companion:`
>     keys. That makes each recipe an ordinary template, so `patches: pc` and `steps+:` work unchanged.
>     Steps take an `after:` key so a patch can place a step.
>   - `character_builder` is **client-only**: served on `/` and `/build` but kept out of every tools/list
>     and the model's help catalog (`ToolProfiles.ClientOnlyTools`). The model only drafts builder JSON (4.4),
>     and listing it would cost ~1.5k chars of the tool budget for nothing.
>   - `steps`/`options` take the draft (kind and level are on it) rather than separate parameters.
>   - The proficiency step's hint isn't dropped. It now fires only when no `skills` choice is recorded, and it
>     says to record the class skills as `levelUpChoices` (the handbook lists each class's count and list).
>   - No `gear` step: open question 4 defaults to "the DM equips to fit".
> - **Defaults taken for open questions:** 2 (all three ability methods), 4 (no packages yet), 6 (plugin id order).
> - Not done: the SDK `<Version>` bump (new public types). Unity suites not run: Phase 1 touched no client code.

**Goal:** one MCP tool the client drives step by step, generic over systems.

- **1.1 SDK:** add `ICharacterCreation` and `IRecipeValidator` in
  `CampaignVault.PluginSdk/Rulesets/Creation/`. Selector lookup with fallback, as
  described in Architecture.
- **1.2 Core engine** `Rulesets/Creation/RecipeCharacterCreation.cs`:
  - Loads `creation.yaml` and `statblock.yaml` per system through a new
    `CreationRecipeProvider` (an `IRulesetYamlProvider`, so plugin roots and phase 0
    patches apply to recipes too).
  - Resolves `source` → provider (races, classes, backgrounds, classSkills, spells, feats,
    creatures, startingEquipment).
  - Evaluates `countFrom`, `exclude` and `when` path references, and runs the named
    validators.
- **1.3 Tool `character_builder`** (new `Tools/CharacterBuilderTools.cs`, registered in the
  build profile in `Schema/ToolProfiles.cs`). Actions:
  - `steps(kind, level)`
  - `options(step, draft)`
  - `preview(draft)`: builds an in-memory `Character`, runs the bootstrap orchestrator with
    `Trigger = Create` and no `EquipmentAccess`, and returns the derived sheet plus
    `errors[]` and `warnings[]`. No database write.
  - `commit(draft, kind)`: goes through the same path as `WorldBuilderTools.WorldBuild`
    (`:33`, bootstrap at `:360-380`) with `isPc` / `isPartyCompanion` set and
    `LevelUpChoices` recorded. It's idempotent on `draft.id`.
- **1.4 Draft contract**, a single C# record that the client mirrors:
  `{ id?, kind, system, level, name, concept, look, choices: { <stepKey>: value | [values] | {object} } }`.
  Values are keyed by recipe step key, so the contract doesn't change per system.
- **1.5 Core validators:**
  - `abilityScores.standardArray`, `abilityScores.pointBuy` (27-point budget with the
    recipe's cost table), `abilityScores.rollRange`;
  - `pickN.count`; `spells.countForLevel`.
  - Racial bonuses come only from the race step, never from the client.
- **1.6 5e recipe and data gaps 1–2:**
  - Write `RulesetData/dnd5e/creation.yaml` and `statblock.yaml`.
  - Add `skillChoices: { count, from: [...] }` to the 12 class YAMLs. These are
    hand-authored (not generated by `scripts/`); keep the SRD header.
  - Make `Dnd5eDeriveProficiencyStep` apply the recorded
    `LevelUpChoices(level=1, key="skills")` and drop the "patch it in by hand" hint.
  - Add `Spells { Cantrips, Known, Prepared }` to `Dnd5eExtension` and `Pf2eExtension`,
    and per-level `cantripsKnown` / `spellsKnown` / `prepared` counts to caster
    progressions.

**Done when:**
- Unit tests cover `steps` for 5e and Narrative.
- They cover `preview` for a 5e level-1 wizard (HP 6+Con, proficiency 2, spell DC
  8+2+Int).
- They cover over-budget point buy, too many skills and a validator name that doesn't
  exist (a clear startup error).
- A plugin `patches:` on `creation.yaml` adding a step shows up in `steps`.
- `commit` matches `world_build` output for the same input.
- The server suite is green.

## Phase 2: Unity builder dialog, 5e level 1

> **Status: done (2026-10-01, uncommitted).**
> - `Scripts/Model/CharacterBuilder.cs`: the wire DTOs (`CharacterDraft`, `BuilderStep`, `BuilderOption`,
>   `BuilderIssue`, `StatBlockSchema`), `BuilderDependencies` (which steps read which, clearing, the note) and
>   `AbilityDice`. `BuilderState` / `StepOptions` / `AbilityWork` on `VaultAppState` (`StateArea.Builder`).
> - `App/VaultController.Builder.cs` (the controller is now `partial`): `BeginBuilder`, `BuilderSteps/Options/
>   Preview/Commit`, `BuilderChoose`/`Toggle`/`ToggleSpell`, the ability methods, `SetBuilderLevel`, `AskDmAboutStep`.
>   Stale replies are dropped by a draft revision counter.
> - UI: `CharacterBuilderOverlay` (rail + level stepper in `Toolbar`, widget left, live sheet right, Ask the DM in
>   `Dock`; regions redraw only when what they show changed, and never under a focused text field),
>   `UI/Builder/{StepWidgets,PickWidget,AbilityScoresWidget,SpellsWidget,IdentityWidget}.cs`, `Theme/builder.uss`.
>   `SheetView` is the sheet and stat block extracted from `CharacterSheetOverlay`, shared with the preview.
>   Opened from the party panel's + button, and from its empty state while setup is pending.
> - `AI/BuilderAdvisor.cs`: the step prompt (option ids, `SUGGEST:` line), suggestion matching; builder dividers in
>   the onboarding chat read "Building Lyra · Class".
> - Tests: EditMode `CharacterBuilderTests` (14): draft ↔ JSON, step parsing, dependency clearing and its note,
>   dropped hidden-step choices, preview parsing, the unknown-kind card, dice, advisor parsing. PlayMode
>   `BuilderTests`: a human fighter built by button presses (asking the scripted DM on the class step, a class
>   change clearing skills), saved, and read back; photos `Library/VaultSnapshots/builder-*.png`.
>   EditMode 178/178, PlayMode 12/12.
> - **Changed from the plan / defaults:**
>   - Open question 3 (draft persistence): **session-only**. The draft survives closing the dialog, not a restart.
>   - The level stepper allows 1–3 (2.4); 4+ is disabled with a tooltip until phase 8.
>   - 2.3's schema-driven stat block: the identity widget edits schema fields, but the parchment for companions is
>     still `SheetView.StatBlock`; drawing it from the schema lands with phase 5, where companions are built.
>   - After the first commit the draft keeps its id, so SAVE CHANGES updates the same character; BUILD ANOTHER
>     starts fresh. The first PC built becomes the played PC when none is set.
> - Known gaps: class options have no descriptions (the class YAMLs have none). The UI is built in C# like the
>   rest of the client; the move to UXML templates + MVVM binding is Phase 2.5.

**Goal:** the player builds a legal 5e level-1 PC end to end, with a live sheet preview,
using widgets per step kind.

- **2.1 `CharacterBuilderOverlay`** (new `Scripts/UI/CharacterBuilderOverlay.cs`):
  - Step rail in `Toolbar`, step widgets in the body, the live preview on the right, and
    "Ask the DM" in `Dock`.
- **2.2 One widget per step kind** (`Scripts/UI/Builder/*.cs`):
  - `pickOne` / `pickN`: option cards with label, description and tags, plus an
    "N of M" count.
  - `abilityScores`: method tabs (array assignment, point buy with points left, a roll log
    that stays visible); racial bonuses shown beside base scores.
  - `allocate`, `spells`, `feats`: filterable lists.
  - `identity`: fields from the schema.

  An unknown step kind renders a clear "this client doesn't support step '<kind>' yet"
  card instead of failing.
- **2.3 `SheetView`:** extract `RenderSheet` (`CharacterSheetOverlay.cs:96`) and
  `RenderStatBlock` (`:339`) into a shared view. The stat block renders from the
  `statblock.yaml` schema.
- **2.4 Level** is a 1–20 stepper in the header. Above 1, see phase 8; until then, levels
  above 3 are disabled with a tooltip saying why.
- **2.5 "Ask the DM" on each step** uses the onboarding conversation
  (`OnboardingState.BrainstormChat`):
  - Sending adds a divider ("Building Lyra · class"), as `MoveToQuestion` does.
  - The prompt gets the step's option ids, and suggestions must be ids. Invalid ones are
    shown, never silently dropped.
- **2.6 Controller and state:**
  - `VaultController` coroutines `BuilderSteps`, `BuilderOptions`, `BuilderPreview` and
    `BuilderCommit`, following the `PostAnswer` pattern (~`:1071`).
  - A `BuilderState` on `VaultAppState`, so the draft survives redraws (same lesson as
    `BrainstormDraft`).
  - Changing an earlier step clears the steps that depend on it, with a visible note
    ("changing class cleared: skills, spells").

**Done when:**
- EditMode tests cover draft ↔ JSON, dependency clearing and its visible note, and the
  unknown-step-kind fallback.
- A PlayMode test builds a 5e level-1 fighter through the overlay and commits it, with a
  photo of each step (as `TableTests` does).
- EditMode and PlayMode are green.

## Phase 2.5: UI architecture pass ("tie the shoelaces before running")

> **Status:** U.1–U.6 done (uncommitted): Phase 2.5 is complete. The user widened the scope: *all* UI moves to UXML +
> binding, and re-engineering or splitting existing code to get there was welcome.
> - **U.1:** `Scripts/UI/Mvvm/` (`ViewModel` with `Set`/`SetList`/`Watch`, `ItemList.Sync`, `ITemplated`,
>   `Templates`, `Display`), `Scripts/UI/Controls/` (`Repeater`, `ContentPresenter`, `ClassBinding`,
>   `VariantBinding`, `VaultButton`, `VaultField`, `VaultBar`). The `Overlay` shell is `Shell/Modal.uxml` (from U.5),
>   and a page with a `Template` fills its regions and binds to `CreateViewModel()`. `TooltipLayer` shows any
>   element's bindable `tooltip`. `UnityClient/UI_CONVENTIONS.md`. Tests: PlayMode `BindingTests` (the spike: every
>   binding kind in a real panel), EditMode `UiConventionTests` (inline styles with an allowlist and a ratchet for
>   files not converted yet; every template loads).
> - **U.2:** `Templates/Builder/*.uxml`, `BuilderViewModel` and one step view model per kind
>   (`Scripts/UI/Builder/`). The old widgets, region signatures and focus deferral are gone; `CharacterBuilderOverlay`
>   is 40 lines. EditMode `BuilderViewModelTests` (9) pin what is and isn't raised.
> - **U.3:** `Templates/Sheet/*.uxml`, `SheetViewModel` / `StatBlockViewModel` / `SheetPageViewModel`
>   (`Scripts/UI/Sheet/`); `SheetView.cs` is gone. The stat block draws any schema field it has no fixed line for
>   (from `CharacterSheet.Stats`), so phase 5's companion fields need no client work; the builder passes its
>   identity step's schema. EditMode `SheetViewModelTests` (5).
> - **Lessons, now in UI_CONVENTIONS.md:** a two-way field takes state's value only when state changed underneath
>   it (a refresh caused by one field otherwise pushes a stale value into a sibling mid-edit); bound values arrive
>   on the panel's next update, so tests let a frame pass; a bar's first value doesn't animate.
> - **U.4:** `Templates/Table/*.uxml` (party frames, codex tabs, the Quests / Scene / Pack / Journal pages),
>   `PartyViewModel` and `CodexViewModel` with a page view model per tab (`Scripts/UI/Table/`); `PartyViews.cs` is
>   two thin views. New controls: `ClickArea` (a clickable card), `VaultButton plain`. The journal's handoff draft,
>   search and track fields live on page view models that last as long as the table, so switching tabs or a
>   refresh never loses typing. EditMode `TableViewModelTests` (5).
> - Snapshots compared before/after (`builder-*`, `n7-*-sheet-*`, `n7-*-codex-*`): same look.
>   Green after U.4: EditMode 200/200, PlayMode 13/13. After U.5: EditMode 219/219, PlayMode 13/13 (snapshots p6-*, n6-*, n7-settings-advanced compared: same look).
> - **U.5:** `Templates/{World,Settings,Setup,Onboarding,Dialogs}/*.uxml` with view models in `Scripts/UI/{World,Settings,Dialogs}/`:
>   `CampaignsViewModel`, `OnboardingViewModel` (a page per state: start form, choice / yes-no / text question,
>   brainstorm chat with a docked composer, status, done; the foot buttons are a list of `ActionViewModel`),
>   `SettingsViewModel` (a page per tab: `ProviderFormViewModel`, `ServerPage`, `EmbeddedPage`, `FeelPage`,
>   `PluginsPage`, `AdvancedPage`), `SetupViewModel`, `ConfirmViewModel`, `InspectorViewModel`.
>   `SettingsOverlays.cs`, `WorldOverlays.cs` and `ProviderForm.cs` are gone; the overlays are thin classes.
>   New controls: `VaultSwitch`, `VaultCard`, `VaultField.focusRequest`; shared `ChoiceViewModel`, `ActionViewModel`,
>   `TabViewModel`. EditMode `SettingsViewModelTests` and `WorldViewModelTests` (19).
> - **U.6:** the table itself. `Shell.uxml` now binds the top bar (`TopBarViewModel`: context, two status sigils,
>   page buttons, the "Aa" menu with a persistent `TextSizeViewModel`) and the toasts (`ToastsViewModel`); the story
>   log (`StoryLogViewModel`, one view model and one `Templates/Story/*.uxml` per segment kind, tool activity folded
>   into an `ActivityStripViewModel`) and the command bar (`CommandBarViewModel`: gate, quick actions, ACT/STOP,
>   history) are templates cloned into `#Log` and `#Command`. `StoryLogView` and `CommandBar` keep only what a
>   binding can't do (scroll-follow, the keys of a multi-line box). New controls: `LiveRepeater` (an incremental list
>   over an `ObservableCollection`: one insert/remove/swap per change, for the log that trims at the front and the
>   toasts that fade; `Repeater`'s by-position reuse would re-bind everything), `VaultSpinner` (the thinking die).
>   Speaker hues are `cv-speaker--N` classes, roll results `cv-roll--result-*`. `Ui.cs` is down to text helpers,
>   `ToastHost` and `TextSizeControl.Build` are gone, and `UiConventionTests.Legacy` is empty. EditMode
>   `ShellViewModelTests` (13). Green: EditMode 232/232, PlayMode 13/13 (`p4-table`, `p4-busy`, `p7-*` compared:
>   same look).

**Goal:** before phases 4 and 5 add more screens, the client's UI moves to UI Toolkit's intended split: UXML for
structure (editable in UI Builder), USS for every visual decision, view models with runtime data binding for
screens that show changing state, and C# only for behaviour. The builder goes first; it has the most state and
the most hand-written redraw logic, which binding replaces.

Today every page is built in C# through the `Ui` factory and redrawn wholesale on `VaultAppState.Changed`. Styling
is already in USS (`Theme/*.uss`), but there are inline `style.*` lines across `Scripts/UI`, there is no UXML
beyond `Shell.uxml`, and redraw-on-change needs workarounds (the builder's region signatures and "don't redraw
under a focused text field").

**Rules (written down in `UnityClient/UI_CONVENTIONS.md` as part of U.1):**
- **UXML** for any layout that isn't generated from data: overlay shells, the sheet's sections, cards, rows.
- **Custom controls** (`[UxmlElement] partial class OptionCard : VisualElement`) for reusable pieces with
  behaviour: option card, ability row, step rail, check row, gear row, resource row.
- **View models** (`INotifyBindablePropertyChanged`, `[CreateProperty]`) only where state changes while the
  screen is open: builder, character sheet, party frames, codex, onboarding, settings. They read `VaultAppState`,
  raise property changes, and call `VaultController` for commands. The controller stays the only writer of
  state, as now.
- **No view model for tiny widgets** (chip, count label, toast, tooltip): a template or the `Ui` factory is enough.
- **No inline `style.*`** except runtime values with no class equivalent (bar fill width, tooltip position,
  filter show/hide), each on an allowlist.
- Element names stay as test hooks (`opt-<id>`, `builder-commit`, …), so PlayMode tests keep driving real buttons.

**Items:**
- **U.1 Groundwork:**
  - `ViewModel` base (property-change plumbing and a `Bind(VaultAppState, StateArea)` helper that maps state areas
    to property notifications).
  - Template loading (`Resources/VaultUI/Templates/*.uxml`).
  - `UI_CONVENTIONS.md`.
  - An EditMode test that fails on inline `style.*` in `Scripts/UI` outside the allowlist.
  - Spike first: confirm Unity 6.6 runtime binding with two-way `TextField` binding, list binding (`ListView` or
    a repeated template) and binding a `CharacterSheet`, inside the PlayMode harness's render-to-texture panel.
- **U.2 Builder:**
  - `CharacterBuilder.uxml`, plus `OptionCard`, `AbilityRow` and `StepRail` controls and a `BuilderViewModel`.
  - Remove the overlay's signatures and deferral (binding updates only what changed, so typing is never
    interrupted).
  - The widgets stay one per step kind.
- **U.3 Sheet:**
  - `SheetView` becomes `CharacterSheet.uxml` + `StatBlock.uxml` bound to a sheet view model; the sheet overlay
    and the builder preview share it.
  - This is where 2.3's schema-driven stat block lands too: the parchment draws from `StatBlockSchema` fields.
- **U.4 Table:** party frames and codex, bound to the session/PC/companions areas.
- **U.5 Dialogs:** onboarding, campaigns, settings and setup; the `Overlay` shell itself becomes `Modal.uxml`
  (head / toolbar / scroll / dock / foot).
- **U.6 Sweep:** remove the remaining inline styles, so the allowlist test passes for all of `Scripts/UI`.

**Done when:**
- Every screen above has a UXML layout that opens in UI Builder.
- View models are unit-tested in EditMode without a panel (state change → property change → expected values).
- The inline-style test passes.
- PlayMode snapshots match the current look (the `p4`–`p6`, `n6`/`n7` and `builder-*` photos are compared side by
  side before and after).
- EditMode and PlayMode are green.

**Out of scope:** a visual redesign. The look stays; only how it's built changes. U.4–U.6 can ship as separate
commits, but U.1–U.3 must land before phase 4 starts.

## Phase 3: Release pipeline check (bundling)

**Goal:** confirm that a release build compiles and bundles everything, now that the
`MacArchitecture` fix is in.

- **3.1** Run the Release workflow (manual dispatch). Windows, Linux and macOS must all
  compile. If one fails, capture its `error CS…` lines; `gh` isn't installed locally, so
  read them in the Actions UI.
- **3.2** Check each zip for:
  - `StreamingAssets/CampaignVault/system-prompt*.md` and `skills/` (from
    `DmContentStager`);
  - `plugin-skills/`;
  - `Server/<rid>/`, including `Plugins/*/RulesetData` (from `embed-server.sh` and
    `ServerEmbedBuildProcessor`);
  - `models/embedding/model.onnx`: a real file, not a git-lfs pointer.
- **3.3** Optional: open and re-save `ProjectSettings/DynamicsManager.asset` to clear the
  PhysicsManager version warning.

**Done when:** all three release artifacts build, and the contents check passes on one of
them by unzipping it and listing the paths above.

**Status (2026-10-01): local half done, CI half waiting on Michael.**
- 3.2 on a local macOS build (`embed-server.sh osx-arm64`, then `BuildStandaloneOSX` with
  `-vaultPrestagedServer`): `Build StandaloneOSX result=Succeeded errors=0` (1.0 GB). The player has
  `system-prompt.md`, `system-prompt.narrative.md`, `skills/`, `Server/osx-arm64/` with
  `Plugins/{MedievalWeapons,NecromancersGrimoire,ShadowAndSteel,TrailAndHearth}/RulesetData`, and a real
  90 MB `models/embedding/model.onnx` (not an LFS pointer).
- `plugin-skills/` is absent because no plugin ships a `SKILL.md` yet (`plugins/MedievalWeapons/skills`
  is empty). `DmContentStager` only creates the folder when there is something to copy, so this is
  expected, not a bug.
- 3.1 not done: it needs the Release workflow dispatched from GitHub (no `gh` here, and it publishes a
  release), and the Phase 2/2.5 work is uncommitted, so CI would not build it. 3.3 skipped (optional).

## Phase 4: Onboarding integration (the "party" step)

**Goal:** the three PC questions become one party step that opens the builder.

- **4.1 Server:** replace `PcCreation`, `PcRoster` and `StartingLevel`
  (`OnboardingQuestionCatalog.cs:199` and the entries after it) with one `Party` question.
  - New `OnboardingAnswerType.Party`, whose answer is
    `{ mode, level, characterIds[], companionIds[] }`. `mode` is one of
    `build-now | dm-drafts | build-at-table`.
  - `ValidateAnswer` checks the ids exist and are `isPc` (no ids needed for
    `build-at-table`).
  - `PartyComposition` stays. `SoloCompanions` folds into the party step's companion
    section.
- **4.2 Brief** (`OnboardingBrief.cs:22-24`, `:52-57`, `:86-100`): if the PCs are already
  built, list their ids and say "already built, don't `world_build` them; place them at
  the opening location". `build-at-table` keeps today's wording.
- **4.3 Client:** party cards (name, class line, level, EDIT) with ADD CHARACTER, DM DRAFTS
  THE PARTY and WE'LL BUILD AT THE TABLE.
  - Replaces the roster helper (`WorldOverlays.cs:347-363`) and `AboutTheParty` (`:514`).
  - `RosterParser` stays for the handoff (`VaultController.cs:424-425`).
- **4.4 DM drafts:** the model returns builder JSON with option ids only, following
  `OnboardingBrainstorm.FinalizeInstruction`. Each draft goes through `preview`; invalid
  picks are flagged on their step and never silently fixed. The player reviews each draft
  in the builder, then commits.
- **4.5 Migration:** onboarding states that already answered `pc_creation` keep their
  answers. `GetNextQuestion` skips `Party` for them, and the brief uses the old wording.

**Done when:**
- `Onboarding_EveryPath_AsksAboutTheWorldAndPlayerCharacters`
  (`MultiCampaignIntegrationTests.cs` ~`:447`) covers the three party modes.
- Brief tests assert that pre-built PCs aren't rebuilt.
- `VaultSmokeScenario.AnswerFor` and `TableTests` answer `Party` with `build-at-table`.
- All suites are green.

**Status (2026-10-01): 4.1, 4.2, 4.3 and 4.5 done; 4.4 (DM drafts) waits on open question 5.**
- Server: one `party` question (`OnboardingAnswerType.Party`, JSON `OnboardingPartyAnswer`), ids checked
  against the campaign's characters; legacy `pc_creation` states finish on the old questions and wording;
  the brief lists built ids and says "Do NOT world_build". `start_session` keeps handing back the brief
  until a PC stands somewhere (a built party alone isn't a seeded world). The builder plays the system chosen
  in onboarding when no config exists yet.
- Client: `PartyPageViewModel` (cards + EDIT, level 1..3, ADD / USE THIS PARTY / DM DRAFTS / BUILD AT THE
  TABLE), the builder opens over the setup for the campaign being set up (`BuilderState.ForOnboarding`).
  Roster helper removed.
- Known gaps, by decision: solo companions are gone from onboarding until Phase 5 (`companionIds` takes built
  ones only); starting level is capped at `MaxBuilderLevel` (3) in the client; characters built but not yet
  submitted are not recovered if the app restarts mid-step (they stay in the campaign as PCs); DM DRAFTS
  currently submits `dm-drafts` with no ids, so the DM invents the party from the brief (the old
  `dm-pregenerates` behaviour) until 4.4.
- PF2e/Narrative: the builder has no recipe for them yet (Phase 6); Narrative's party step offers only the
  table and DM routes.

## Phase 5: Companions (simplified stat block)

**Goal:** a companion is drafted by the DM, built from a template, or built from scratch,
and is stored as a stat-block creature.

- **5.1** Companion recipe = one `identity` step over `statblock.yaml`:
  - 5e: name, kind, CR, HP (→ `StatBlockHp`), AC, speed, the six abilities, skills,
    `attacks[] { name, bonus, damage }`, traits, stance.
  - PF2e: name, kind, creature level, HP, AC, speed, modifiers, skills, attacks, traits,
    stance.

  Saved with `isPartyCompanion = true`.
- **5.2 Sources:**
  - "From a template": `lookup kind:'creatures'` around the party level; the template is
    copied into the draft and can then be edited.
  - "The DM drafts one".
  - "From scratch".
- **5.3 Power check** (the `companion.power` validator): warn, don't block, when the
  companion is above the party. Suggested bands: 5e CR ≤ party level / 2; PF2e creature
  level ≤ party level − 2. See the first open question.
- **5.4 Data:** add companion archetypes (mastiff, riding horse, warhorse, hawk, guard,
  acolyte, scout, thug) under `RulesetData/*/creatures` with a `companion: true` tag.
  They're SRD / ORC sourced, so keep the source headers.

**Done when:**
- Server tests cover a template commit (HP from `StatBlockHp`, not a formula) and the power
  warning.
- A client test covers editing a template.
- The companion shows in party frames and the codex ally list.
- Green.

## Phase 6: PF2e

- **6.1 Data gap 4:**
  - Extend `scripts/generate_pf2e_feats.py` to emit `category`, `ancestries` and `skill`,
    then regenerate.
  - Add `heritages[]` to `pf2e/ancestries/*.yaml`.
  - Add progressions for bard, druid, ranger and witch.
- **6.2** Write `RulesetData/pf2e/creation.yaml`:
  `ancestry → heritage → background → class → boosts (allocate) → feats (by category) → spells? → gear → identity`.
- **6.3 Validators:**
  - `pf2e.boosts`: no two boosts to the same score from one source; 4 free boosts.
  - `pf2e.featEligibility`: level, class, ancestry and category. `prerequisite` stays
    free text, shown as a note rather than enforced (981 feats have one).

**Done when:** a PF2e level-1 fighter and wizard build and commit, the preview matches the
bootstrap output, and the suites are green.

## Phase 7: Narrative

`RulesetData/narrative/creation.yaml` with `identity` only: name, concept, three
descriptors, look, and optional drives and fears (`Character.Wants` / `Fears`). No level and
no stats, consistent with the Narrative branch that skips `starting_level`.

**Done when:** a Narrative campaign's party step builds and commits a PC with no stat
fields.

## Phase 8: Levels above 1

- For a target level L > 1, the recipe adds a `levelChoices` step group from
  `GetPendingChoices` for each level (subclass, ASI or feat, fighting style, spells
  gained).
- **"The DM fills the rest":** the model proposes ids, which are validated and shown for
  review. Nothing is chosen without the player seeing it; invalid or skipped picks stay
  flagged.
- HP per level uses the campaign's `HpMode`, shown in the preview.

**Done when:** a 5e level-5 wizard (subclass at 2, ASI at 4, spells through level 3) builds
by both the DM-fills path and the manual path, and both previews match the bootstrap output.

---

## Out of scope / deferred

- **Mechanical plugin traits.** Race and ancestry traits stay as text, stamped into
  `DistinctiveFeatures` by `RaceTraitStamper`. Making them do something mechanically is
  deferred.
  - When it's revisited, don't reuse the word or the field: `SystemExtension.Traits` is the
    published plugin key→value fact store (`IPluginTraitsUpgrader`, the PLUGINS.md key
    convention at `:258`).
  - Phase 0's list operations apply to the existing text `traits:` lists only.
- SWADE (its folders are empty).
- Multiclassing at creation (`ClassLevels` supports it).
- Portraits; importing sheets from other tools.
- Making the in-game chat's history trimming visible
  (`OpenAiChatDriver.TrimToBudget`, 48k). It still drops turns silently; that's a separate
  ticket.

## Open questions for Michael

1. **Companion power bands:** are the defaults in 5.3 right? Warn or block (planned: warn)?
2. **Ability-score methods:** all three everywhere, or a per-campaign setting (e.g. "no
   rolling")?
3. **Draft persistence:** keep an uncommitted builder draft across app restarts (local
   file), or only for the session?
4. **Starting equipment (data gap 3):** author the SRD packages now, or ship phase 2 with
   "the DM equips to fit"?
5. **DM drafts:** one model call per PC, or one call for the whole party?
6. **Plugin load order (0.1):** sort by plugin id, or allow an explicit `priority` in
   `plugin.json`?

## Gates

Each phase ships on its own:
- A green server unit suite (run the test executable directly, per
  `running-tests-locally`).
- Green Unity EditMode and PlayMode runs (the Unity binary directly, per
  `unity-tests-direct-binary`).
- One build at a time (CLAUDE.md).
- Any remaining or known failures reported before calling a phase done.
