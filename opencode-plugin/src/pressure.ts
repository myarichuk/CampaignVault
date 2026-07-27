// Mirrors CampaignVault's PressureSeverity enum (src/CampaignVault/Models/WorldPressureItem.cs)
// and the escalation cap in src/CampaignVault/Data/PressureManager.cs (default MaxPressuresPerResponse = 5).
export const PressureSeverity = {
  Suggestion: 0,
  Simulation: 1,
  NarrativePrompt: 2,
  EngineWarning: 3,
} as const;

export const DEFAULT_ESCALATION_CAP = 5;

export interface WorldPressureItem {
  severity: number;
  entityId: string;
  text: string;
  groupingKey: string;
}

export interface PressureCheckResult {
  items: WorldPressureItem[];
  engineWarnings: WorldPressureItem[];
  capHit: boolean;
}

/** Case-insensitive pickup of a field regardless of camelCase/PascalCase JSON naming. */
function pick(obj: Record<string, unknown>, ...names: string[]): unknown {
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

function normalizeItem(raw: unknown): WorldPressureItem | null {
  if (!raw || typeof raw !== "object") return null;
  const obj = raw as Record<string, unknown>;
  const severity = pick(obj, "severity", "Severity");
  const entityId = pick(obj, "entityId", "EntityId");
  const text = pick(obj, "text", "Text");
  const groupingKey = pick(obj, "groupingKey", "GroupingKey");
  if (typeof severity !== "number" || typeof text !== "string") return null;
  return {
    severity,
    entityId: typeof entityId === "string" ? entityId : "",
    text,
    groupingKey: typeof groupingKey === "string" ? groupingKey : "",
  };
}

/**
 * Finds the WorldPressureItems array wherever it lives in a parsed ToolResult<T> payload
 * (top-level `worldPressureItems`, or nested under `worldState.worldPressureItems`/`scene.worldPressureItems`).
 */
function findPressureArray(payload: unknown): unknown[] {
  if (!payload || typeof payload !== "object") return [];
  const obj = payload as Record<string, unknown>;
  const direct = pick(obj, "worldPressureItems", "WorldPressureItems");
  if (Array.isArray(direct)) return direct;

  for (const key of Object.keys(obj)) {
    const value = obj[key];
    if (value && typeof value === "object" && !Array.isArray(value)) {
      const nested = findPressureArray(value);
      if (nested.length > 0) return nested;
    }
  }
  return [];
}

export function checkPressure(payload: unknown, escalationCap = DEFAULT_ESCALATION_CAP): PressureCheckResult {
  const items = findPressureArray(payload)
    .map(normalizeItem)
    .filter((item): item is WorldPressureItem => item !== null);

  const engineWarnings = items.filter((item) => item.severity === PressureSeverity.EngineWarning);
  const capHit = items.length >= escalationCap;

  return { items, engineWarnings, capHit };
}

/** Safely parses a tool's raw output text as JSON; returns null on failure rather than throwing. */
export function tryParseToolOutput(rawOutput: string): unknown {
  try {
    return JSON.parse(rawOutput);
  } catch {
    return null;
  }
}
