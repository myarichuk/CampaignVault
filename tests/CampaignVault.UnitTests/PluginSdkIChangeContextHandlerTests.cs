using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// PR1 acceptance: a plugin-shaped handler can target <see cref="IChangeContext"/> alone
/// without referencing host <c>ChangeContext</c> or Raven types.
/// </summary>
public class PluginSdkIChangeContextHandlerTests
{
    private sealed class PluginShapedHandler : IWorldChangeHandler
    {
        public bool ShouldHandle(WorldChange change) => change is HpChange;

        public Task<ChangeHandlerResult> ApplyAsync(
            WorldChange change,
            IChangeContext context,
            CancellationToken ct = default)
        {
            if (change is HpChange hp &&
                context.Characters.TryGetValue(hp.CharacterId, out var character))
            {
                character.CurrentHp += hp.Delta;
                context.RecordMessage($"Adjusted HP for {hp.CharacterId} by {hp.Delta}");
                return Task.FromResult(ChangeHandlerResult.Ok);
            }

            return Task.FromResult(ChangeHandlerResult.Failure("missing character"));
        }
    }

    [Fact]
    public async Task Plugin_shaped_handler_implements_against_IChangeContext_alone()
    {
        var handler = new PluginShapedHandler();
        Assert.IsAssignableFrom<IWorldChangeHandler>(handler);

        var context = ChangeContextTestHelper.Create(
            characters: new()
            {
                ["chars/hero"] = new Character { Id = "chars/hero", Name = "Hero", CurrentHp = 10, MaxHp = 20 }
            });

        IChangeContext asInterface = context;
        var result = await handler.ApplyAsync(
            new HpChange { CharacterId = "chars/hero", Delta = -3 },
            asInterface);

        Assert.True(result.Success);
        Assert.Equal(7, context.Characters["chars/hero"].CurrentHp);
    }
}
