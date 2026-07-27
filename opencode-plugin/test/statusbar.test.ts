import { describe, expect, it } from "vitest";
import { formatStatusBar } from "../src/statusbar.js";

describe("formatStatusBar", () => {
  it("returns null when there's no location data", () => {
    expect(formatStatusBar({})).toBeNull();
    expect(formatStatusBar(null)).toBeNull();
  });

  it("renders SCENE/YOU/NEAR from a start_session-shaped payload", () => {
    const payload = {
      scene: {
        location: { name: "The Sunken Lantern" },
        climate: { effectiveZone: "Dockside District", ambientTemperatureC: 14, timeOfDay: "dusk" },
        presentNPCs: [
          { name: "Old Marra", currentActivity: "tending the bar" },
          { name: "a hooded stranger" },
        ],
      },
      worldState: { time: { epoch: "Current Era", year: 1492, month: 3, day: 12, hour: 19 } },
      party: [
        {
          character: {
            name: "Valen",
            currentAppearance: "road-worn leather armor, a fresh scar over one eye",
            visualTags: ["scarred", "travel-stained"],
          },
          isPc: true,
        },
      ],
    };

    const result = formatStatusBar(payload);
    expect(result).not.toBeNull();
    const lines = result!.split("\n");
    expect(lines[0]).toBe("SCENE | The Sunken Lantern · Dockside District | 1492-3-12 19:00 Current Era");
    expect(lines[1]).toContain("Valen: road-worn leather armor, a fresh scar over one eye");
    expect(lines[1]).toContain("tags: scarred, travel-stained");
    expect(lines[2]).toContain("Old Marra (tending the bar)");
    expect(lines[2]).toContain("a hooded stranger");
  });

  it("falls back gracefully when party/NPC data is missing", () => {
    const payload = { scene: { location: { Name: "Ruined Keep" } } };
    const result = formatStatusBar(payload);
    expect(result).toContain("SCENE | Ruined Keep");
    expect(result).toContain("no PC data in this response");
    expect(result).toContain("no one else present");
  });

  it("handles PascalCase field names from raw C# JSON serialization", () => {
    const payload = {
      Scene: { Location: { Name: "Old Keep" }, PresentNPCs: [] },
      WorldState: { Time: { Year: 1000, Month: 1, Day: 1, Hour: 6 } },
      Party: [],
    };
    const result = formatStatusBar(payload);
    expect(result).toContain("SCENE | Old Keep");
  });
});
