using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Session 1 regression: Mage Armor (ruleset_action) + a restated 'status' on the caster rolled back the
/// whole batch. The same effect arriving from the engine and from the LLM in one batch now collapses to
/// one copy (the engine's), while deliberate same-origin stacking still stacks.
/// </summary>
public class StatusOriginCollapseTests
{
    private static (ChangeContext Ctx, Character Caster) Setup()
    {
        var caster = new Character { Id = "chars/maeve", Name = "Maeve", MaxHp = 10, CurrentHp = 10 };
        var ctx = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [caster.Id] = caster },
            campaignName: "test-campaign");
        return (ctx, caster);
    }

    private static StatusEffect EngineMageArmor() => new()
    {
        Name = "Mage Armor",
        Category = "Buff",
        StatModifiers = new Dictionary<string, float> { ["ArmorClass"] = 3f }
    };

    private static async Task ApplyAsync(ChangeContext ctx, StatusChange change, bool fromEngine)
    {
        var handler = RulesetDataTestHelper.CreateStatusChangeHandler();
        ctx.AutoApplyDepth = fromEngine ? 1 : 0;
        var result = await handler.ApplyAsync(change, ctx);
        ctx.AutoApplyDepth = 0;
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task EngineThenLlm_SameEffect_KeepsOneEngineCopy()
    {
        var (ctx, caster) = Setup();

        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Effect = EngineMageArmor() }, fromEngine: true);
        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Status = "mage armor" }, fromEngine: false);

        var effect = Assert.Single(caster.SystemStats!.StatusEffects);
        Assert.Equal(3f, effect.StatModifiers["ArmorClass"]);
    }

    [Fact]
    public async Task LlmThenEngine_SameEffect_EngineCopyReplacesLlmCopy()
    {
        var (ctx, caster) = Setup();

        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Status = "Mage Armor" }, fromEngine: false);
        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Effect = EngineMageArmor() }, fromEngine: true);

        var effect = Assert.Single(caster.SystemStats!.StatusEffects);
        Assert.Equal(3f, effect.StatModifiers["ArmorClass"]);
    }

    [Fact]
    public async Task SameOrigin_SameName_StillStacks()
    {
        var (ctx, caster) = Setup();

        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Effect = new StatusEffect { Name = "Bleeding", Category = "Injury" } }, fromEngine: false);
        await ApplyAsync(ctx, new StatusChange { CharacterId = caster.Id, Effect = new StatusEffect { Name = "Bleeding", Category = "Injury" } }, fromEngine: false);

        Assert.Equal(2, caster.SystemStats!.StatusEffects.Count(e => e.Name == "Bleeding"));
    }

    [Fact]
    public void Guard_RulesetActionPlusStatusOnCaster_IsNotRejected()
    {
        var changes = new WorldChange[]
        {
            new RulesetAction { CharacterId = "chars/maeve", ActionName = "Mage Armor", ActionType = RulesetActionType.Spell, TargetIds = ["chars/maeve"] },
            new StatusChange { CharacterId = "chars/maeve", Status = "Mage Armor" }
        };

        Assert.Null(SideEffectDuplicationGuard.FindConflict(changes));
    }
}
