// Covers the ENGINE IS AUTHORITATIVE rule of recommended-system-prompt*.md: the database is the truth, not the
// model's memory. After a session.compacted gap, re-inject the CAMPAIGN line and the SESSIONS rule for it
// (forceFullReseed on the next take_turn; start_session is never called mid-play).

export interface CampaignInfo {
  slug: string;
  ruleset?: string;
  rosterIds: string[];
}

/** Extracts the CampaignInfo this hook needs from a start_session response payload. */
export function extractCampaignInfo(payload: unknown): CampaignInfo | null {
  if (!payload || typeof payload !== "object") return null;
  const root = payload as Record<string, unknown>;

  const campaign = pick(root, "campaign", "Campaign");
  const campaignObj = campaign && typeof campaign === "object" ? (campaign as Record<string, unknown>) : undefined;
  const inner = campaignObj ? pick(campaignObj, "campaign", "Campaign") : undefined;
  const innerObj = inner && typeof inner === "object" ? (inner as Record<string, unknown>) : campaignObj;

  // start_session v2 sends a flat campaign posture ({ slug, system, ... }); older payloads nested the
  // raw campaign doc ({ campaign: { name, system } }).
  const slug = pick(innerObj, "slug", "Slug", "name", "Name");
  if (typeof slug !== "string" || slug.length === 0) return null;

  const ruleset = pick(innerObj, "System", "system", "ActiveSystem", "activeSystem");
  const party = pick(root, "party", "Party");
  const rosterIds = Array.isArray(party)
    ? party.map(partyMemberId).filter((id): id is string => typeof id === "string")
    : [];

  return {
    slug,
    ruleset: typeof ruleset === "string" ? ruleset : undefined,
    rosterIds,
  };
}

/** Builds the text re-injected into the session after a compaction. */
export function buildReinjectionText(info: CampaignInfo): string {
  const rulesetLine = info.ruleset ? ` ruleset="${info.ruleset}"` : "";
  const rosterLine = info.rosterIds.length > 0 ? ` roster=[${info.rosterIds.join(", ")}]` : "";
  return [
    `CAMPAIGN: campaignName="${info.slug}"${rulesetLine}${rosterLine}`,
    "Context was compacted. Persisted state is ground truth, not your memory: send forceFullReseed:true " +
      "on your next take_turn (memoriesOnlyCharacterId or fullDetailCharacterId for an NPC you are about to voice), " +
      "and never call start_session mid-play.",
  ].join("\n");
}

function partyMemberId(p: unknown): string | undefined {
  if (!p || typeof p !== "object") return undefined;
  const rec = p as Record<string, unknown>;
  const direct = pick(rec, "id", "Id");
  if (typeof direct === "string" && direct.length > 0) return direct;
  const character = pick(rec, "character", "Character");
  if (character && typeof character === "object") {
    const nested = pick(character as Record<string, unknown>, "id", "Id");
    if (typeof nested === "string" && nested.length > 0) return nested;
  }
  return undefined;
}

function pick(obj: Record<string, unknown> | undefined, ...names: string[]): unknown {
  if (!obj) return undefined;
  for (const name of names) {
    if (obj[name] !== undefined) return obj[name];
  }
  const lower = Object.fromEntries(Object.entries(obj).map(([k, v]) => [k.toLowerCase(), v]));
  for (const name of names) {
    const v = lower[name.toLowerCase()];
    if (v !== undefined) return v;
  }
  return undefined;
}

/** Session-scoped cache: sessionID -> last known CampaignInfo, populated on start_session. */
export class CampaignInfoCache {
  private readonly bySessionId = new Map<string, CampaignInfo>();

  set(sessionId: string, info: CampaignInfo): void {
    this.bySessionId.set(sessionId, info);
  }

  get(sessionId: string): CampaignInfo | undefined {
    return this.bySessionId.get(sessionId);
  }
}
