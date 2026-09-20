# Licensing

CampaignVault is dual-licensed. This document clarifies the licenses of its components.

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
- **Source**: D&D 5e System Reference Document (SRD) 5.1, spells pulled from dnd5eapi.co (an MIT-licensed API wrapper strictly over SRD 5.1 data)
- **Scope**: Core rules including classes, spells, conditions, backgrounds, and mechanics
- **Feats**: SRD 5.1's own inclusion criteria exclude nearly all feats (Wizards' stated criteria: SRD-eligible content must have a 3e SRD equivalent and be vital to how a class/item/monster works). **Grappler is the only feat in the official SRD 5.1** — CampaignVault ships only that one for dnd5e; it does not include PHB feats like Alert, Great Weapon Master, Lucky, Tough, or War Caster, since those are not CC-BY-4.0 licensed.
- **Reference**: https://dndbeyond.com/srd, and the bundled SRD-OGL_V5.1.pdf in this repository
- **Attribution**: Wizards of the Coast LLC, https://dnd.wizards.com
- **Restrictions**: Content is limited to the official SRD — no proprietary sourcebook material, Forgotten Realms setting-specific content, or licensed properties

### Pathfinder 2e Content
- **License**: Open RPG Creative (ORC) License
- **Source**: Pathfinder 2e Remastered core rules and reference content by Paizo Inc., pulled directly from the official Archives of Nethys database (elasticsearch.aonprd.com), filtered to the `Player Core`/`Player Core 2` sourcebooks and common rarity
- **Scope**: Core rules including ancestries, classes, spells, feats, conditions, and mechanics
- **Reference**: https://paizo.com/orclicense, https://2e.aonprd.com
- **Attribution**: Paizo Inc., https://paizo.com
- **Restrictions**: Content is limited to Remastered core rules and ORC-licensed material — no Golarion setting-specific content, proprietary Paizo sourcebooks, or licensed properties

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
| D&D 5e Reference Data | CC-BY-4.0 | SRD 5.1 only; attribution to Wizards of the Coast |
| Pathfinder 2e Reference Data | ORC License | Core rules only; attribution to Paizo Inc. |
| RavenDB (embedded) | Proprietary EULA (with Community tier) | Requires license key for deployment; obtain at ravendb.net/license |

---

**Questions?** Contact michael.yarichuk@gmail.com for licensing inquiries or commercial licensing.
