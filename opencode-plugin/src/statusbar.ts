// Field names grounded in CampaignVault's DTOs:
//   SceneView.Location.Name              (src/CampaignVault/Models/Location.cs)
//   SceneView.Climate.EffectiveZone       (SceneClimateSummary, src/CampaignVault/Models/V4Views.cs)
//   WorldStateView.Time / CampaignTime    (Epoch/Year/Month/Day/Hour, src/CampaignVault/Models/CampaignTime.cs)
//   PartyMemberView.Character.{CurrentAppearance,VisualTags} (src/CampaignVault/Models/Character.cs)
//   SceneView.PresentNPCs[].{Name,CurrentAppearance,CurrentActivity} (NpcPresenceSummary)

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

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function formatTime(time: Record<string, unknown> | undefined): string {
  if (!time) return "unknown time";
  const epoch = pick(time, "epoch", "Epoch");
  const year = pick(time, "year", "Year");
  const month = pick(time, "month", "Month");
  const day = pick(time, "day", "Day");
  const hour = pick(time, "hour", "Hour");
  const parts = [year, month, day].filter((p) => p !== undefined).join("-");
  const hourText = typeof hour === "number" ? `${String(hour).padStart(2, "0")}:00` : "";
  return [parts, hourText, epoch].filter(Boolean).join(" ");
}

/**
 * Builds the 3-line STATUS BAR block described in recommended-system-prompt.md:
 *   SCENE | {location} · {zone} | {time}
 *   YOU   | {appearance}; tags: {tags}
 *   NEAR  | {positions/engagements}
 * Returns null when there isn't enough scene data to render anything meaningful
 * (e.g. a tool result with no scene/location payload at all).
 */
export function formatStatusBar(payload: unknown): string | null {
  const root = asRecord(payload);
  if (!root) return null;

  const scene = asRecord(pick(root, "scene", "Scene")) ?? root;
  const location = asRecord(pick(scene, "location", "Location"));
  const climate = asRecord(pick(scene, "climate", "Climate"));
  const worldState = asRecord(pick(root, "worldState", "WorldState"));
  const time = asRecord(pick(worldState, "time", "Time")) ?? asRecord(pick(root, "time", "Time"));

  if (!location) return null;

  const locationName = pick(location, "name", "Name") ?? "unknown location";
  const zone = pick(climate, "effectiveZone", "EffectiveZone");
  const sceneLine = `SCENE | ${locationName}${zone ? ` · ${zone}` : ""} | ${formatTime(time)}`;

  const party = asArray(pick(root, "party", "Party"));
  const you = party
    .map(asRecord)
    .filter((p): p is Record<string, unknown> => p !== undefined && Boolean(pick(p, "isPc", "IsPc")))
    .map((p) => {
      const character = asRecord(pick(p, "character", "Character")) ?? p;
      const appearance = pick(character, "currentAppearance", "CurrentAppearance") ?? "no notable appearance";
      const tags = asArray(pick(character, "visualTags", "VisualTags")).join(", ");
      const name = pick(character, "name", "Name") ?? "you";
      return `${name}: ${appearance}${tags ? `; tags: ${tags}` : ""}`;
    })
    .join(" / ");
  const youLine = `YOU   | ${you || "no PC data in this response"}`;

  const presentNpcs = asArray(pick(scene, "presentNPCs", "PresentNPCs", "presentNpcs"));
  const near = presentNpcs
    .map(asRecord)
    .filter((n): n is Record<string, unknown> => n !== undefined)
    .map((n) => {
      const name = pick(n, "name", "Name") ?? "someone";
      const activity = pick(n, "currentActivity", "CurrentActivity");
      return activity ? `${name} (${activity})` : String(name);
    })
    .join(", ");
  const nearLine = `NEAR  | ${near || "no one else present"}`;

  return [sceneLine, youLine, nearLine].join("\n");
}
