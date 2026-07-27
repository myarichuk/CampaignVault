import { describe, expect, it } from "vitest";
import { checkDiceRollAttempt } from "../src/diceGuard.js";

describe("checkDiceRollAttempt", () => {
  it("ignores non-shell tools entirely", () => {
    expect(checkDiceRollAttempt("take_turn", "roll 2d6").blocked).toBe(false);
  });

  it("ignores shell commands with no command text", () => {
    expect(checkDiceRollAttempt("bash", undefined).blocked).toBe(false);
  });

  it("blocks literal dice notation in a bash command", () => {
    const result = checkDiceRollAttempt("bash", "echo 'rolling 2d6 for damage'");
    expect(result.blocked).toBe(true);
    expect(result.reason).toMatch(/take_turn/);
  });

  it("blocks python -c invoking random", () => {
    const result = checkDiceRollAttempt("bash", "python3 -c \"import random; print(random.randint(1,20))\"");
    expect(result.blocked).toBe(true);
  });

  it("blocks node -e invoking Math.random", () => {
    const result = checkDiceRollAttempt("bash", "node -e \"console.log(Math.floor(Math.random()*20)+1)\"");
    expect(result.blocked).toBe(true);
  });

  it("blocks shuf-based rolls", () => {
    expect(checkDiceRollAttempt("bash", "shuf -i 1-20 -n 1").blocked).toBe(true);
  });

  it("allows ordinary unrelated shell commands", () => {
    expect(checkDiceRollAttempt("bash", "ls -la ./scenes").blocked).toBe(false);
    expect(checkDiceRollAttempt("bash", "cat notes.md").blocked).toBe(false);
  });
});
