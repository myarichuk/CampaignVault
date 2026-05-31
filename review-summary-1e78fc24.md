# Review Summary

- **Mode**: branch
- **Target**: master (16 commits ahead of origin/master, merge-base 533db87b9915ad31da80d723d7521d4cad9075d1)
- **Files reviewed**: 25 (README.md, CampaignVault.csproj, CampaignRepository.cs, ChangeHandlers/*, Models/Character.cs CombatEncounter.cs *Extension.cs V4Views.cs, Program.cs, Rulesets/*, Tools/CampaignTools.cs, and 7 test files)
- **Diff stats**: 25 files changed, 1480 insertions(+), 20 deletions(-)
- **Issue counts**: 7 bugs, 5 suggestions, 3 nits

## Top issues

[bug] src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:103 -- Unsafe int.Parse on action.Parameters (no TryParse/validation; mirrored across all resolvers). LLM input can crash commit.
[bug] src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:29 -- Silent fallback to default stats when character SystemStats type mismatches active ruleset (cross-ruleset actions "work" with zeroed values).
[bug] src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:114 -- Unchecked outcomes[0]/[1] batch indexing in ResolveAttack (and contested).
[bug] src/CampaignVault/Tools/CampaignTools.cs:436 -- StartCombat/NextTurn accept empty or invalid combatantIds lists with no validation or guards, allowing broken combat states.
[bug] src/CampaignVault/Tools/CampaignTools.cs:467 -- No dead/incapacitated filtering; turns and attacks allowed on HP<=0 characters; no auto-removal or skip logic.

See the full review at: C:\Users\myarichuk\source\repos\CampaignVault\review-1e78fc24.md
