// After opencode compacts a session, the model has lost the NPC cards, location descriptions and
// context lines the server delivered once per session (TAKE_TURN_PLAN.md T6). The server tracks that
// delivery in a ledger it clears on any full reseed, so the next take_turn after a compaction must ask
// for one: forceFullReseed=true resends everything the model now lacks.

function isTakeTurnTool(tool: string): boolean {
  return tool === "take_turn" || tool.endsWith("_take_turn") || tool.endsWith(".take_turn");
}

export class PendingReseed {
  private readonly sessions = new Set<string>();

  mark(sessionID: string): void {
    this.sessions.add(sessionID);
  }

  has(sessionID: string): boolean {
    return this.sessions.has(sessionID);
  }

  /**
   * If this is the first take_turn since a compaction of this session, sets request.forceFullReseed on
   * its args (in place) and clears the flag. Returns true when it did.
   */
  applyTo(tool: string, sessionID: string, args: Record<string, unknown> | undefined): boolean {
    if (!args || !isTakeTurnTool(tool) || !this.sessions.has(sessionID)) return false;

    const request = args.request;
    if (typeof request === "string") {
      try {
        const parsed = JSON.parse(request) as Record<string, unknown>;
        parsed.forceFullReseed = true;
        args.request = JSON.stringify(parsed);
      } catch {
        return false;
      }
    } else if (request && typeof request === "object") {
      (request as Record<string, unknown>).forceFullReseed = true;
    } else {
      args.request = { forceFullReseed: true };
    }

    this.sessions.delete(sessionID);
    return true;
  }
}
