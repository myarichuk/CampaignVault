# Commercial Licensing

CampaignVault's source code is available under a dual-licensing model:

1. **Free/Non-Commercial**: PolyForm Noncommercial 1.0.0 (see LICENSE file)
2. **Commercial**: Proprietary license (not included in this repository)

## What is "Commercial Use"?

Commercial use includes (but is not limited to):
- Using CampaignVault as part of a product or service you sell
- Running CampaignVault as a SaaS offering
- Integrating CampaignVault into closed-source internal business tools
- Any use where the primary intent is to generate revenue or business value

**Non-commercial uses** (e.g., personal projects, open-source projects, academic research, non-profit work) are free under the PolyForm Noncommercial license.

## Obtaining a Commercial License

To use CampaignVault commercially, you must obtain a separate commercial license agreement.

**Contact**: michael.yarichuk@gmail.com

Please include in your inquiry:
- Your organization name
- Intended use case (SaaS, product, internal tool, etc.)
- Expected scale (users, deployments, revenue if applicable)
- Timeline

**Pricing**: No fixed pricing is currently in place. Commercial license terms are negotiated on a case-by-case basis and may include:
- Perpetual or term-based licenses
- Usage restrictions or usage tiers
- Support, updates, and maintenance arrangements

## Important: RavenDB License Requirement

Regardless of which CampaignVault license you obtain, **you must also obtain a RavenDB license key** to lawfully deploy CampaignVault.

- **Free Option**: RavenDB Community License (free, requires annual key renewal)
  - Covers single-node deployments up to 3 cores and 6GB RAM
  - Sufficient for most MCP server deployments
  - Request at: https://ravendb.net/license/request/community

- **Paid Options**: RavenDB Professional or Enterprise licenses for larger/multi-node deployments
  - Learn more at: https://ravendb.net/pricing

**Why this matters**: Without a RavenDB license key, CampaignVault falls back to AGPLv3, which is incompatible with any commercial/proprietary use. A RavenDB license key moves you to RavenDB's proprietary EULA, allowing your containing application (CampaignVault) to be licensed however you choose (including commercial).

## License Compatibility

Your CampaignVault commercial license is **independent** of your RavenDB license. You'll need:
- A **CampaignVault commercial license** to legally use CampaignVault for business purposes
- A **RavenDB license** (free Community tier is fine for small deployments) to lawfully run RavenDB.Embedded

Both are required for legal commercial operation.

---

**Questions?** Contact michael.yarichuk@gmail.com.
