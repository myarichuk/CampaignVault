import { describe, expect, it } from "vitest";
import { PendingReseed } from "../src/reseedAfterCompaction.js";

describe("PendingReseed", () => {
  it("forces a full reseed on the first take_turn after a compaction, once", () => {
    const pending = new PendingReseed();
    pending.mark("s1");

    const args: Record<string, unknown> = { request: { changes: [] }, campaignName: "c" };
    expect(pending.applyTo("campaign-vault_take_turn", "s1", args)).toBe(true);
    expect((args.request as Record<string, unknown>).forceFullReseed).toBe(true);

    const next: Record<string, unknown> = { request: {} };
    expect(pending.applyTo("campaign-vault_take_turn", "s1", next)).toBe(false);
    expect((next.request as Record<string, unknown>).forceFullReseed).toBeUndefined();
  });

  it("leaves other tools and other sessions alone", () => {
    const pending = new PendingReseed();
    pending.mark("s1");

    expect(pending.applyTo("campaign-vault_get_entity", "s1", { id: "chars/x" })).toBe(false);
    expect(pending.applyTo("campaign-vault_take_turn", "s2", { request: {} })).toBe(false);
    expect(pending.has("s1")).toBe(true);
  });

  it("handles a request passed as a JSON string", () => {
    const pending = new PendingReseed();
    pending.mark("s1");

    const args: Record<string, unknown> = { request: JSON.stringify({ includeParty: true }) };
    expect(pending.applyTo("take_turn", "s1", args)).toBe(true);
    expect(JSON.parse(args.request as string)).toEqual({ includeParty: true, forceFullReseed: true });
  });
});
