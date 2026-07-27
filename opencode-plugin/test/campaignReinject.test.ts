import { describe, expect, it } from "vitest";
import { extractCampaignInfo, buildReinjectionText, CampaignInfoCache } from "../src/campaignReinject.js";

describe("extractCampaignInfo", () => {
  it("returns null when there's no campaign data", () => {
    expect(extractCampaignInfo({})).toBeNull();
    expect(extractCampaignInfo(null)).toBeNull();
  });

  it("extracts slug/ruleset/roster from a nested start_session-shaped payload", () => {
    const payload = {
      campaign: {
        campaign: { name: "dragonheist", activeSystem: "Dnd5e" },
        posture: {},
      },
      party: [{ id: "chars/valen" }, { id: "chars/mira" }],
    };
    const info = extractCampaignInfo(payload);
    expect(info).toEqual({ slug: "dragonheist", ruleset: "Dnd5e", rosterIds: ["chars/valen", "chars/mira"] });
  });

  it("handles PascalCase field names", () => {
    const payload = {
      Campaign: { Campaign: { Name: "dragonheist" } },
      Party: [],
    };
    const info = extractCampaignInfo(payload);
    expect(info?.slug).toBe("dragonheist");
    expect(info?.rosterIds).toEqual([]);
  });
});

describe("buildReinjectionText", () => {
  it("renders the CAMPAIGN line and a state-refresh nudge", () => {
    const text = buildReinjectionText({ slug: "dragonheist", ruleset: "Dnd5e", rosterIds: ["chars/valen"] });
    expect(text).toContain('CAMPAIGN: campaignName="dragonheist"');
    expect(text).toContain('ruleset="Dnd5e"');
    expect(text).toContain("roster=[chars/valen]");
    expect(text).toContain("Persisted state is ground truth");
  });

  it("omits ruleset/roster segments when absent", () => {
    const text = buildReinjectionText({ slug: "dragonheist", rosterIds: [] });
    expect(text).toContain('CAMPAIGN: campaignName="dragonheist"');
    expect(text).not.toContain("ruleset=");
    expect(text).not.toContain("roster=");
  });
});

describe("CampaignInfoCache", () => {
  it("stores and retrieves info per sessionId, missing key returns undefined", () => {
    const cache = new CampaignInfoCache();
    expect(cache.get("session-1")).toBeUndefined();
    cache.set("session-1", { slug: "dragonheist", rosterIds: [] });
    expect(cache.get("session-1")).toEqual({ slug: "dragonheist", rosterIds: [] });
    expect(cache.get("session-2")).toBeUndefined();
  });
});
