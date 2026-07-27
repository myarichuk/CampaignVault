// Covers Rule 5 of recommended-system-prompt.md: "Persisted state is ground truth, not your memory —
// trust the latest scene/NPC query over recollection, especially after any gap or summarization."
// After a session.compacted/session.idle gap, re-inject the CAMPAIGN line and a nudge to re-verify
// state via start_session/get_entity rather than trusting recollection.

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

  const slug = pick(innerObj, "name", "Name");
  if (typeof slug !== "string" || slug.length === 0) return null;

  const ruleset = pick(innerObj, "activeSystem", "ActiveSystem");
  const party = pick(root, "party", "Party");
  const rosterIds = Array.isArray(party)
    ? party
        .map((p) => (p && typeof p === "object" ? pick(p as Record<string, unknown>, "id", "Id") : undefined))
        .filter((id): id is string => typeof id === "string")
    : [];

  return {
    slug,
    ruleset: typeof ruleset === "string" ? ruleset : undefined,
    rosterIds,
  };
}

/** Builds the text re-injected into the session after a compaction/idle gap. */
export function buildReinjectionText(info: CampaignInfo): string {
  const rulesetLine = info.ruleset ? ` ruleset="${info.ruleset}"` : "";
  const rosterLine = info.rosterIds.length > 0 ? ` roster=[${info.rosterIds.join(", ")}]` : "";
  return [
    `CAMPAIGN: campaignName="${info.slug}"${rulesetLine}${rosterLine}`,
    "Context gap detected (compaction or idle). Persisted state is ground truth, not your memory: " +
      "re-verify current scene/NPC/party state via get_entity or start_session before narrating, " +
      "rather than trusting what you recall from earlier in the conversation.",
  ].join("\n");
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
