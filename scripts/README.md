# Content generator scripts

These regenerate SRD/ORC-licensed `RulesetData/*.yaml` content from the two
official source APIs. They are one-shot fetch-and-overwrite tools, not
enrichment passes: each run pulls the full current dataset from the network
and rewrites the YAML it's responsible for from scratch. Re-running one is
always safe (idempotent — same source data in, same files out) and requires
no local state beyond the repo itself.

Requires network access to both hosts below. Nothing in `CampaignVault.csproj`
depends on running these at build time — they're a content-authoring step you
run manually, then commit the resulting YAML like any other source file.

## Sources

- **`www.dnd5eapi.co`** — dnd5e content. A community-run wrapper over the
  System Reference Document 5.1 by Wizards of the Coast LLC, CC BY 4.0. See
  `LICENSING.md`.
- **`elasticsearch.aonprd.com`** — pf2e content. The official Paizo-run
  Archives of Nethys search endpoint, queried directly (no wrapper). Every
  query in every script below is scoped to
  `primary_source.keyword: ["Player Core", "Player Core 2"]` — the two 2023
  Remaster core rulebooks released under the ORC License. **Never widen that
  filter** to other AoN sourcebooks (Lost Omens, adventure paths, legacy
  pre-Remaster books) without re-checking `LICENSING.md`'s scope rationale;
  dropping it silently pulls in non-ORC content (confirmed live: an unscoped
  `category:spell` query for "Fireball" returns the pre-Remaster Core
  Rulebook version, not the Player Core one — see
  `CONTENT_GAPS_PLAN.md` Step 2 findings for the reproduction).

## `generate_spells.py`

```
python3 scripts/generate_spells.py
```

Regenerates **every** file in `RulesetData/{dnd5e,pf2e}/spells/` — the
directory is fully wiped and rebuilt each run, so nothing there is
hand-authored. dnd5e spells include structured damage/save/heal/AoE fields
(`dnd5eapi.co`'s spell detail JSON has them); pf2e spells don't — AoN's pf2e
spell documents carry damage only as prose text, not a structured field, so
pf2e spell damage validation is out of scope engine-side (see
`Dnd5eRulesetResolver` vs. the lack of an equivalent pf2e validator).

Can run either half only, e.g. from a Python shell: `generate_dnd5e()` or
`generate_pf2e()` — useful if only one source API is reachable.

## `generate_pf2e_feats.py`

```
python3 scripts/generate_pf2e_feats.py
```

Regenerates `RulesetData/pf2e/feats/` (also fully wiped and rebuilt). Reads
each feat's structured `prerequisite` field from AoN rather than parsing free
text — an earlier regex-based approach over prose prerequisites proved
fragile and was removed.

## `generate_items.py`

```
python3 scripts/generate_items.py
```

Regenerates dnd5e weapon/armor items into `RulesetData/dnd5e/items/` from
`dnd5eapi.co`'s `equipment-categories/{weapon,armor}` endpoints (mundane
items only — both endpoints also list magic items such as *Vorpal Sword* or
*Armor +1*, which carry no clean fixed stat block and are filtered out by
checking the entry's `url`, not by name-matching). Currently produces 37
weapons + 13 armor/shields.

Unlike the two generators above, **this one does not wipe the directory
first** — `RulesetData/dnd5e/items/` is a mixed hand-authored/generated
directory (`climbers_kit.yaml` is a Tool, hand-authored, and is never touched
since Tool isn't a weapon/armor category SRD exposes). The script only
writes the specific slugs it generates, so anything outside dnd5e
weapons/armor is left alone automatically. `longsword.yaml` *is* one of the
slugs SRD returns, so it gets overwritten on every run — this replaces its
hand-written flavor description with the same generated factual one every
other item gets, and adds equip metadata (`equipZones`/`equipLayer`/
`twoHanded`) the hand-authored version never had (without it, that item
can't actually be equipped via `item_equip`).

No pf2e items exist yet — pf2e's weapon/armor schema is structurally
different (proficiency-based, dex-cap-by-armor-category, no
versatile-damage concept) and needs its own generator, not an extension of
this one. See `CONTENT_GAPS_PLAN.md` Step 5.
