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

- **Mechanics gaps found in the 2026-10 code review (see MECHANICS_REMEDIATION_PLAN.md).** The fixes below are built
  (the plan's phases 2 to 9); each bullet lists what is still not done.
  - Spell bookkeeping (built, M-60 to M-62): a leveled cast is refused unless its slot spend is in the same commit and the spell
    is on the caster's cantrip, prepared or known list; a 5e concentration spell marks its caster as concentrating. The campaign
    option `spellBookkeeping=warn` relaxes the refusals. The action parameters `ritual`, `innate`, `focus` and `item` skip them. Not
    done: the engine doesn't spend the slot itself, a slot spent in an earlier commit isn't seen, PF2e sustained spells and
    homebrew spells get no automatic concentration, and the multiclass slot table is untouched.
  - A grapple ends when the grappler is incapacitated, but not when the two move out of reach (no distance model to
    check it against). Some conditions stay prose because they name a relation or an action-economy rule (charmed,
    deafened, blinded in PF2e, slowed, stunned in PF2e). Cards and the static speed view still show the unconditioned
    speed (M-26 follow-up).
  - PF2e dying (built, M-52): a PC at 0 HP gets dying 1 (2 from a critical hit) plus wounded; damage while dying raises it; the
    recovery check is the `death_save` commit; dying 4 (less doomed) kills; healing ends dying and adds wounded. Not built: Hero
    Points (they spend to avoid dying), the Treat Wounds action that clears wounded (it ends only on a long rest at full HP;
    a long rest heals now, M-70), recovery checks that need more than a flat roll, and the Unity client's display of the PF2e
    counters. A PC already at 0 HP before the engine began flagging them gets the Unconscious status at their next HP change.
  - Multiclassing (5e): `level_up` with a `classGained` the character lacks adds the class at level 1 once the SRD's 13 in
    the key abilities is met (the DM can override, recorded). The new class's hit die and spellcasting ability come with it, and the
    proficiency step now runs after the class is added. Not done or not checked: the multiclass spell-slot table (slot
    sizing is untouched by this work), a new class's level-1 spell and cantrip picks (the level-up menu offers no slots for
    them), plugin classes outside the twelve SRD names (no hit die), and Pathfinder, which multiclasses through archetype
    feats (a `classGained` the character lacks is refused). Legacy level-up records with no class name match every class
    of a multiclass character.
  - Range (built, M-80): `range` is the normal maximum band, `longRange` lets a farther target be hit with disadvantage, `minRange`
    refuses a closer one, and a 5e ranged weapon attack with a living hostile (the other side of the PC/NPC line) recorded within
    Touch has disadvantage. An area spell checks its origin point against `range` and the targets against `aoeRadius` from the origin.
    Everything passes when no position is recorded. Reach weapons are Close; PF2e long range is -2 per distance band beyond
    `range` (a band stands in for a range increment). Not done: a hostile found only among the characters the action loaded, and
    the melee auto-crit against a paralyzed target also fires from a reach weapon at 10 ft (bands can't tell 5 from 10).

- **Scripted effects (not built, on purpose).** The effect vocabulary now covers numeric bonuses, advantage,
  extra damage dice, critical range, resistance, flat damage reduction, evasion, and initiative, speed and passive bonuses. A sandboxed script (Jint: no host access, a time and memory cap)
  could express the rest. We hold off because a script is code: it can't be validated, previewed or tagged the way
  data can, and a DM-written one would run model-written code. Plugin authors who need more already have C#
  (`IRollModifierProvider`, recipe validators). If scripts come, they'd be plugin-only files that return the same
  effect records the vocabulary uses, never something the DM homebrews.

## Done since the first draft

- **Code review fixes (2026-10-15, REVIEW_REMEDIATION_PLAN.md Phases 1–2).** Data: the SRD spell-slot table for every
  caster level, pact magic at warlock 2, sorcery points to 20, Channel Divinity per class, Wild Shape 2, Rage, Lay on
  Hands, Arcane Recovery and Indomitable pools; petrified resists everything; unconscious no longer hides prone; reach
  weapons are Close and ranged weapons carry `longRange`; spells carry their casting kind (`castKind`), PF2e `actionCost`,
  `attackType` (touch = melee), `ritual`, PF2e `saveType`/`basicSave`; PF2e spells no longer claim concentration or a
  verbal component. Code: stabilizing resets the death-save tally (and `death_save` `stabilize: true` does it without a
  roll); a spell's data sets its action economy; PF2e untrained attacks add no level, thrown weapons add Strength and
  propulsive half; rider dice double on a critical; PF2e shields count only while raised (`Raise a Shield`); heavy armor's
  Strength minimum slows, `stealthDisadvantage` is the `noisy_armor` condition, PF2e check penalties and the Strength
  threshold apply, versatile weapons use the larger die with `twoHanded`; PF2e grapple degrees, the grappler's Athletics
  Escape DC and MAP; the 5e defender may use Acrobatics; a long rest needs 8 hours and 1 HP; a paired slot must hold the
  spell; range disadvantage goes through the roll pipeline; ranged-in-melee ignores an incapacitated hostile and covers
  thrown weapons; PF2e Sneak Attack needs an agile, finesse or ranged weapon; a missing PF2e save counts as trained;
  Legendary spellcasting at 19; going without food lasts 3 + Constitution days.

- **Mechanics review fixes (2026-10).** The engine owns what it can compute and the prompts say so: long-rest healing,
  hit dice and exhaustion, 0 HP (Unconscious, Prone, 5e death saves, PF2e dying and wounded), temporary HP, spell slot
  pairing, spell repertoire and concentration, range bands, and the reaction reset. The Unity party frame shows a downed
  PC's death-save tally and still draws the HP bar when temp HP is appended to the hit points. The DM prompts, the combat
  skills and the help manual no longer ask the model to hand-apply these.

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

- **Still open from the 2026-10 code review.** PF2e conditions beyond their listed modifiers (drained's HP loss,
  stupefied's flat check to cast, frightened and sickened decrementing at turn end, enfeebled on every Strength roll);
  a grapple's size limit (no size on the sheet); PF2e feat resistance stays "halve" unless the feat uses the flat
  `damageReduction` kind; shield `hardness` and Shield Block; a 5e spell with a long casting time is refused in combat
  but ritual casting isn't modelled outside it; a caster level above 20 or a multiclass slot table; 5e subraces and
  dragonborn resistance.
- **Subclass features that are rule text only.** The 12 SRD subclasses' features are data (by level, with their own
  choices), shown on the sheet and to the DM. The engine applies the ones the effect vocabulary covers (fighting
  styles, Improved Critical, Divine Strike, Colossus Slayer, Draconic Resilience's hit points and armor class,
  Unarmored Defense); the rest (Cutting Words, Sculpt Spells, Rage's resistances…) are adjudicated by the DM from
  the text, because they need state the vocabulary doesn't have (a rage, a reaction). A feature can grant a pool
  (`pools:`), sized from the character (`maxFrom`) with a die that grows by level; Bardic Inspiration uses that.
- **The Land druid's bonus cantrip** isn't counted by the spells step. Granted spells are all added as prepared.
- **Homebrew PF2e feats in the builder.** A campaign's own feat has no category, ancestry or class list in its document,
  so the PF2e feat steps (which filter by those) don't offer it; 5e's improvement feats and spell lists do.
- **PF2e and the new effect kinds.** Advantage, extra damage dice and critical range apply to 5e attacks; PF2e rolls take
  the numeric kinds, resistance and flat damage reduction (damage goes through the same resolver as 5e's).

- **PF2e cleric deity's skill.** It's a free skill pick until there are deities.
- **PF2e druid's Voice of Nature** feat choice.
- **5e Expertise** (bard 3 and 10, rogue 1 and 6), ranger favored enemy and natural explorer.
- **PF2e** Perception ranks, Lore skills, prerequisites written only as prose, and class proficiency increases by
  level.
- **Level-up details.** A 5e feat taken instead of an improvement has its prerequisites checked, and its own choices
  (a half-feat's ability, skills, spells) are slots once it is picked (PF2e level-ups don't pick feats in the menu yet).
  A feat taken at creation outside an improvement (a race's bonus feat) doesn't ask its choices, and a race's innate
  spells are listed but their once-a-day use is the DM's. A level-up pick
  records the class that gave it, at that class's level, so a multiclass character's subclass or style picks stay with
  their class (records from before this name no class and still match by key alone). A deity, patron or lineage the character took at creation
  is recorded and narrows later level-up choices the way it did in the builder (a patron's pact boon at level 3); the
  shipped cleric's domains are only asked at level 1, so nothing there changes.
