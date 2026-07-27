// Enforces Rule 6 of recommended-system-prompt.md at the tool layer: "never invent a roll yourself,
// mentally or via any script/tool." This targets bash/script-execution tool calls that look like an
// attempt to fake a die roll (RNG calls, literal dice notation, shell dice utilities) instead of using
// CampaignVault's own resolution flow (take_turn). Heuristic by nature — expect to tune after real
// false positives/negatives are observed in manual testing.

const DICE_NOTATION = /\b\d{1,3}d\d{1,3}\b/i; // e.g. 2d6, d20
const RNG_KEYWORDS = /\b(random|randint|randrange|rand\(|Math\.random|shuf|\/dev\/urandom|getrandom)\b/i;

export const SHELL_LIKE_TOOLS = new Set(["bash", "shell", "exec", "run_command", "code_execution"]);

export interface DiceGuardResult {
  blocked: boolean;
  reason?: string;
}

export function checkDiceRollAttempt(toolName: string, command: string | undefined): DiceGuardResult {
  if (!command || !SHELL_LIKE_TOOLS.has(toolName)) {
    return { blocked: false };
  }

  if (DICE_NOTATION.test(command) || RNG_KEYWORDS.test(command)) {
    return {
      blocked: true,
      reason:
        "Blocked: this looks like an attempt to roll dice via a script/shell command. " +
        "CampaignVault resolves all rolls server-side through take_turn — submit the action there " +
        "instead of generating a random number yourself.",
    };
  }

  return { blocked: false };
}
