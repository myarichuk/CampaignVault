# Rules gaps and house rules

> **If the rules feel thin, that's on purpose.** CampaignVault ships only the free rules: the D&D 5e SRD 5.1 (CC-BY-4.0)
> and Pathfinder 2e Player Core / Player Core 2 (ORC). That's one subclass per 5e class, one 5e background, one 5e feat,
> and no named gods, patrons or setting lore. Everything else is yours to add: ask the DM to homebrew it for your
> campaign, or install a plugin (PLUGINS.md).

Rules content and features left out on purpose, or deferred. Each entry says why and what it would take.

## House rules (ours, not the game's)

- **PF2e cleric without a deity.** The shipped data has no deities, because every deity in Player Core is
  setting content. With no deity plugin, a PF2e cleric picks domains, a divine skill and a healing or harming
  font directly. Pathfinder has no such rule: a PF2e cleric always follows a deity.
  - Built: the cleric's level-1 "Divine calling" asks for two domains and the font (`generate_pf2e_classes.py`
    writes it). A deity from a plugin narrows both choices to its own (PLUGINS.md, "Named powers"). The divine skill
    was already a free pick. Domain focus spells aren't granted yet, so a domain is a recorded choice for now.

## Left out for licensing

- **Aasimar** (5e). They aren't in SRD 5.1 or SRD 5.2, so they're skipped. A plugin or the DM can add them.
- **5e subclasses, backgrounds and feats beyond the SRD.** SRD 5.1 has one subclass per class, the Acolyte
  background, and the Grappler feat. Everything else is Player's Handbook (or later) content: leave it to plugins
  and DM homebrew. Removed on 2026-10-02: 38 subclass options, the Criminal, Folk Hero, Sage and Soldier
  backgrounds, the Battle Master's superiority dice pool, the paladin's Blessed Warrior and the ranger's Close
  Quarters Shooter fighting styles, and two invocation options that weren't invocations. Characters that already
  recorded one keep the record; the builder no longer offers it.
- **Named deities, patrons and fiendish lords.** They're setting content (Golarion, the Forgotten Realms…). The
  shipped data stays generic ("a fey patron"); a plugin can name one.

## Planned

- **Scripted effects (not built, on purpose).** The effect vocabulary now covers numeric bonuses, advantage,
  extra damage dice, critical range and resistance. A sandboxed script (Jint: no host access, a time and memory cap)
  could express the rest. We hold off because a script is code: it can't be validated, previewed or tagged the way
  data can, and a DM-written one would run model-written code. Plugin authors who need more already have C#
  (`IRollModifierProvider`, recipe validators). If scripts come, they'd be plugin-only files that return the same
  effect records the vocabulary uses, never something the DM homebrews.

## Done since the first draft

- **Homebrew feats and spells in the builder.** A campaign's own feats and spells join its builder (tagged homebrew),
  next to its homebrew subclasses, ancestries and powers. Picking a deity, patron or lineage now refreshes the class
  choices it narrows (a PF2e deity's domains) at once; before, the old list stayed on screen until reopened.

- **Level-up menu.** The builder's level choice slots, reused for an existing character. `lookup kind=level_up` lists the
  next level's `slots`; `level_up` takes `picks` (slot id → option ids), checks them (ability scores stop at 20, a feat or
  one or two abilities, a skill that can't rise is refused) and applies them before the level's hit points, so a
  Constitution improvement counts. PF2e attribute boosts (half a boost from +4, in pairs) and skill increases are
  applied in play now, not just recorded. The Unity client shows the menu: a LEVEL UP chip on the party frame and a toast
  when the XP rule says a level is earned (once per level, never a modal over the story), a button on the sheet (always
  available for a player character, since milestone campaigns have no XP rule), and the builder's cards for the choices.
  Models (OpenCode, Grok Web, Claude CLI) get the XP_THRESHOLD pressure and the same `picks`. A level-up with `picks`
  left out still works and warns about the required choices it didn't get.

- **DM homebrew of subclasses, ancestries and named powers.** `world_build` takes `homebrew: [{kind, system, yaml}]`, the
  YAML of a plugin file kept with the campaign. It is the last template layer for that campaign's calls only
  (`HomebrewScope`), so the campaign's builder offers it with the homebrew tag and other campaigns don't see it. The
  shipped `pc` recipes now hold optional `deity`, `patron` and `lineage` steps that appear only when a power fits the class.

- **PF2e versatile heritages** (Aiuvarin, Changeling, Dhampir, Dragonblood, Dromaar, Duskwalker, Nephilim; Player Core
  and Player Core 2), listed under every ancestry by `generate_pf2e_origins.py`. They are text only: the extra traits,
  vision and feat lists aren't applied, so the DM or player tracks the other ancestry's feats by hand. Aasimar and
  Tiefling are Advanced Player's Guide and stay out.
- **Named powers** (`powers/`: deity, patron, lineage). A recipe step lists them by type, and the pick narrows the
  class choice to the options it `offers`. None ship; see PLUGINS.md.
- **Plugin-added class options** (`classOptions/`), **hiding shipped content** (`hidden: true`, `hideOptions+:`), the
  **homebrew tag** in the builder, **subclass spells** (granted spells join the prepared list, patron lists join the
  spells step), and a wider **effect vocabulary** (see PLUGINS.md). Fighting-style bonuses are now only effects: the
  attack math no longer adds Archery and Dueling by itself, and they stack on a stated bonus like a feat's.

## Deferred

- **Subclass features that are rule text only.** The 12 SRD subclasses' features are data (by level, with their own
  choices), shown on the sheet and to the DM. The engine applies the ones the effect vocabulary covers (fighting
  styles, Improved Critical, Divine Strike, Colossus Slayer, Draconic Resilience's hit points and armor class,
  Unarmored Defense); the rest (Cutting Words, Sculpt Spells, Rage's resistances…) are adjudicated by the DM from
  the text, because they need state the vocabulary doesn't have (a rage, a reaction, a pool).
- **The Land druid's bonus cantrip** isn't counted by the spells step. Granted spells are all added as prepared.
- **Homebrew PF2e feats in the builder.** A campaign's own feat has no category, ancestry or class list in its document,
  so the PF2e feat steps (which filter by those) don't offer it; 5e's improvement feats and spell lists do.
- **PF2e and the new effect kinds.** Advantage, extra damage dice, critical range and resistance apply to 5e attacks;
  PF2e rolls take the numeric kinds only.

- **PF2e versatile heritages** (nephilim, changeling, aiuvarin, dromaar; Player Core 2: dhampir, dragonblood,
  duskwalker). They go with any ancestry, so `generate_pf2e_origins.py` skips them today.
- **PF2e cleric deity's skill.** It's a free skill pick until there are deities.
- **PF2e druid's Voice of Nature** feat choice.
- **5e Expertise** (bard 3 and 10, rogue 1 and 6), ranger favored enemy and natural explorer.
- **PF2e** Perception ranks, Lore skills, prerequisites written only as prose, and class proficiency increases by
  level.
- **Level-up details.** A 5e feat taken instead of an improvement has its prerequisites checked (PF2e level-ups don't pick
  feats in the menu yet); a multiclass
  character's level-up records picks at the class's level; deities' narrowing of a cleric's domains isn't applied to a
  level-up (the original choice is kept).
