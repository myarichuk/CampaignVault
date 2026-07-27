import { describe, expect, it } from "vitest";
import { checkPressure, tryParseToolOutput, PressureSeverity } from "../src/pressure.js";

function item(severity: number, text = "pressure text", groupingKey = "g1") {
  return { Severity: severity, EntityId: "npc/1", Text: text, GroupingKey: groupingKey };
}

describe("checkPressure", () => {
  it("finds no engine warnings and no cap hit on an empty payload", () => {
    const result = checkPressure({ worldPressureItems: [] });
    expect(result.items).toHaveLength(0);
    expect(result.engineWarnings).toHaveLength(0);
    expect(result.capHit).toBe(false);
  });

  it("detects an EngineWarning severity item", () => {
    const result = checkPressure({
      worldPressureItems: [item(PressureSeverity.Suggestion), item(PressureSeverity.EngineWarning, "fix this now")],
    });
    expect(result.engineWarnings).toHaveLength(1);
    expect(result.engineWarnings[0].text).toBe("fix this now");
  });

  it("hits the escalation cap at 5 unresolved items by default", () => {
    const items = Array.from({ length: 5 }, () => item(PressureSeverity.Simulation));
    const result = checkPressure({ worldPressureItems: items });
    expect(result.capHit).toBe(true);
  });

  it("does not hit the cap below the threshold", () => {
    const items = Array.from({ length: 4 }, () => item(PressureSeverity.Simulation));
    const result = checkPressure({ worldPressureItems: items });
    expect(result.capHit).toBe(false);
  });

  it("respects a custom escalation cap", () => {
    const items = Array.from({ length: 3 }, () => item(PressureSeverity.Simulation));
    const result = checkPressure({ worldPressureItems: items }, 3);
    expect(result.capHit).toBe(true);
  });

  it("finds worldPressureItems nested under a scene/worldState wrapper", () => {
    const result = checkPressure({
      scene: { worldPressureItems: [item(PressureSeverity.EngineWarning)] },
    });
    expect(result.engineWarnings).toHaveLength(1);
  });

  it("is case-insensitive on field names (camelCase vs PascalCase)", () => {
    const result = checkPressure({
      worldPressureItems: [{ severity: PressureSeverity.EngineWarning, entityId: "x", text: "hi", groupingKey: "g" }],
    });
    expect(result.engineWarnings).toHaveLength(1);
  });

  it("ignores malformed items without throwing", () => {
    const result = checkPressure({ worldPressureItems: [{ foo: "bar" }, null, 5] });
    expect(result.items).toHaveLength(0);
  });
});

describe("tryParseToolOutput", () => {
  it("parses valid JSON", () => {
    expect(tryParseToolOutput('{"a":1}')).toEqual({ a: 1 });
  });

  it("returns null on invalid JSON instead of throwing", () => {
    expect(tryParseToolOutput("not json")).toBeNull();
  });
});
