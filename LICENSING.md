# Licensing

CampaignVault is dual-licensed. This document clarifies the licenses of its components.

The notices the content licenses require are in [NOTICE.md](./NOTICE.md). The root `LICENSE` file carves the game data out of the PolyForm terms: CC-BY-4.0 and ORC both forbid adding restrictions to the licensed material, so the data stays under its own license.

## Code License

CampaignVault's source code is **dual-licensed**:
- **Non-Commercial Use**: Free to use under the **PolyForm Noncommercial 1.0.0** license (see LICENSE file)
  - Permitted for: personal use, open-source projects, education, research, non-profit organizations
  - No commercial use, revenue generation, or business-for-profit use
- **Commercial Use**: Requires a separate commercial license agreement
  - Contact: michael.yarichuk@gmail.com to arrange a commercial license
  - No fixed pricing — terms negotiated case-by-case

See [LICENSE](./LICENSE) for the full non-commercial license terms.

## Game Content Licenses

CampaignVault bundles game reference content from two systems:

### D&D 5e Content
- **License**: Creative Commons Attribution 4.0 (CC-BY-4.0)
- **Source**: D&D 5e System Reference Document (SRD) 5.1, spells and equipment pulled from dnd5eapi.co (an MIT-licensed API wrapper strictly over SRD 5.1 data)
- **Scope**: Core rules including classes, spells, conditions, backgrounds, mechanics, and mundane weapons/armor. Magic items (e.g. *Vorpal Sword*, *Armor +1*) are deliberately excluded — `scripts/generate_items.py` filters them out by `url` prefix, not by name, since they carry no fixed SRD stat block
- **Feats**: SRD 5.1's own inclusion criteria exclude nearly all feats (Wizards' stated criteria: SRD-eligible content must have a 3e SRD equivalent and be vital to how a class/item/monster works). **Grappler is the only feat in the official SRD 5.1** — CampaignVault ships only that one for dnd5e; it does not include PHB feats like Alert, Great Weapon Master, Lucky, Tough, or War Caster, since those are not CC-BY-4.0 licensed.
- **Subclasses and backgrounds**: SRD 5.1 has one subclass per class (Berserker, Lore, Life, Land, Champion, Open Hand, Devotion, Hunter, Thief, Draconic Bloodline, the Fiend, Evocation) and one background (Acolyte), so CampaignVault ships only those. Other subclass options are limited to what the SRD lists too (fighting styles, invocations, metamagic). Anything more comes from a plugin or the DM's homebrew (RULES_GAPS.md).
- **Hand-kept files** (`progressions/`, `races/`, `backgrounds/`, `pools/`, `feats/`): short summaries of SRD 5.1 rules in our own words; nothing outside the SRD.
- **Reference**: https://dndbeyond.com/srd and the dnd5eapi.co SRD 5.1 data (subclass features were checked against its /api/2014/subclasses endpoints)
- **Attribution**: Wizards of the Coast LLC, https://dnd.wizards.com. The CC-BY-4.0 attribution statement is in [NOTICE.md](./NOTICE.md); keep it with any redistribution.
- **Restrictions**: Content is limited to the official SRD — no proprietary sourcebook material, Forgotten Realms setting-specific content, or licensed properties

### Pathfinder 2e Content
- **License**: Open RPG Creative (ORC) License
- **Source**: Pathfinder 2e Remastered core rules and reference content by Paizo Inc., pulled directly from the official Archives of Nethys database (elasticsearch.aonprd.com), filtered to the `Player Core`/`Player Core 2` sourcebooks and common rarity
- **Scope**: Core rules including ancestries, classes, spells, feats, conditions, mechanics, and weapons/armor/shields
- **Reference**: https://paizo.com/orclicense, https://2e.aonprd.com
- **Attribution**: Paizo Inc., https://paizo.com. The ORC Notice (attribution, Licensed Material, Reserved Material) is in [NOTICE.md](./NOTICE.md); keep it with any redistribution.
- **Creatures**: the four stat blocks under `RulesetData/pf2e/creatures/` are original to CampaignVault (they are not copied from Monster Core or any other book) and are offered under the ORC License like the rest of the folder.
- **Feats**: `scripts/generate_pf2e_feats.py` pulls Player Core / Player Core 2 feats and keeps only those a shipped class or ancestry (the files under `pf2e/classes` and `pf2e/ancestries`, plus the versatile heritages) can take; feats for Player Core 2 classes and ancestries that aren't shipped yet are dropped rather than left dangling. Regenerating after adding a class or ancestry file picks its feats up.
- **Restrictions**: Content is limited to Remastered core rules and ORC-licensed material — no Golarion setting-specific content, proprietary Paizo sourcebooks, or licensed properties
- **Why `Player Core`/`Player Core 2` specifically**: these two books are the ones Paizo rewrote during the 2023 Remaster specifically to strip Golarion-specific Product Identity (deity names, nation names, setting lore) out of core mechanics, so the books could be released cleanly under ORC. Restricting the generator's query to these two `primary_source` values (plus `rarity: common`) isn't an arbitrary content filter — it's what keeps this project inside Paizo's own declared-open scope. Pulling from any other AoN-indexed sourcebook (Lost Omens, adventure paths, non-Remastered legacy books) would reintroduce Golarion-specific Product Identity that this scope is designed to exclude.

## Unity Client Assets

The Unity client (`UnityClient/Assets/CampaignVault/UI/`) ships third-party fonts and icons. They're redistributable with attribution; none are modified beyond what's noted.

### Fonts (SIL Open Font License 1.1)
- **Cinzel** by Natanael Gama: display type, headings, nameplates. `UI/Fonts/OFL-Cinzel.txt`
- **EB Garamond** by Georg Duffner and Octavio Pardo: body and story text. `UI/Fonts/OFL-EBGaramond.txt`
- **JetBrains Mono** by JetBrains: dice math and developer text. `UI/Fonts/OFL-JetBrainsMono.txt`
- **Source**: static per-weight builds of the Google Fonts releases (https://github.com/google/fonts, `ofl/`). The `UI/Fonts/SDF/*.asset` files are Unity font assets generated from these TTFs.

### Icons (CC BY 3.0)
- **Source**: game-icons.net (https://game-icons.net, https://github.com/game-icons/icons)
- **License**: Creative Commons Attribution 3.0 Unported, https://creativecommons.org/licenses/by/3.0/
- **Authors**: Lorc, Delapouite, sbed and Skoll. The per-icon list is in `UI/Icons/ATTRIBUTION.md`.
- **Modifications**: the black background square is removed and fills are set to white so the UI can tint them. `add.svg`, `chevron.svg` and everything under `UI/Frames/` are original to this project.

## Important Deployment Note: RavenDB License

CampaignVault embeds RavenDB via the `RavenDB.Embedded` NuGet package. **To legally deploy CampaignVault (especially for commercial use), you must obtain a RavenDB license key.** 

- **Free Option**: RavenDB Community License at https://ravendb.net/license/request/community
  - Sufficient for single-node MCP servers (covers 3 cores, 6GB RAM, 3-node cluster)
  - No cost, just requires annual renewal to stay on the latest major version
- **Licensed Deployments**: Using a RavenDB license key (Community, Professional, or Enterprise) ensures CampaignVault's code is governed by your chosen license (PolyForm Noncommercial or commercial) independent of RavenDB's own licensing.
- **Without a License Key**: RavenDB.Embedded falls back to AGPLv3, which is incompatible with commercial closed-source use. This constraint applies to the entire containing application.

See [COMMERCIAL.md](./COMMERCIAL.md) and the README for deployment setup instructions.

## Summary

| Component | License | Notes |
|-----------|---------|-------|
| CampaignVault Code | PolyForm Noncommercial 1.0.0 / Commercial | Dual-licensed; see LICENSE and COMMERCIAL.md |
| D&D 5e Reference Data | CC-BY-4.0 | SRD 5.1 only; not under PolyForm; notice in NOTICE.md |
| Pathfinder 2e Reference Data | ORC License | Player Core / Player Core 2 only; not under PolyForm; ORC Notice in NOTICE.md |
| RavenDB (embedded) | Proprietary EULA (with Community tier) | Requires license key for deployment; obtain at ravendb.net/license |
| Unity client fonts (Cinzel, EB Garamond, JetBrains Mono) | SIL OFL 1.1 | License texts ship next to the fonts |
| Unity client icons (game-icons.net) | CC BY 3.0 | Attribution in `UnityClient/Assets/CampaignVault/UI/Icons/ATTRIBUTION.md` |

---

**Questions?** Contact michael.yarichuk@gmail.com for licensing inquiries or commercial licensing.
