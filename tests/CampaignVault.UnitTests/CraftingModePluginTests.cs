using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.AutofacModules;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class CraftingModePluginTests
{
    [Fact]
    public async Task Crafting_plugin_drop_in_excludes_Sdk_dll_and_round_trips()
    {
        var tempPlugins = Path.Combine(Path.GetTempPath(), "cv-crafting-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dest = PluginTestDrop.InstallCrafting(tempPlugins);
            Assert.False(File.Exists(Path.Combine(dest, "CampaignVault.PluginSdk.dll")));

            var plugins = PluginAssemblyLoader.LoadPluginsFromDirectory(tempPlugins);
            var plugin = Assert.Single(plugins);
            Assert.Equal("com.campaignvault.crafting", plugin.Manifest!.Id);

            WorldChangeTypeRegistry.Instance.RegisterPluginAssemblies([plugin.Assembly]);
            CommitSchemaModel.Invalidate();

            var modeType = plugin.Assembly.GetTypes().First(t => typeof(IInteractionMode).IsAssignableFrom(t) && !t.IsAbstract);
            var mode = (IInteractionMode)Activator.CreateInstance(modeType)!;
            Assert.Equal("crafting", mode.ModeId);
            Assert.Same(typeof(IInteractionMode).Assembly, mode.GetType().GetInterface(nameof(IInteractionMode))!.Assembly);
            Assert.True(mode is IInteractionMode);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IInteractionMode).Assembly));

            Assert.Contains(CommitSchemaModel.Variants, v => v.Discriminator == "crafting_step");

            var json = """[{"$type":"crafting_step","characterId":"chars/smith","stage":"forge"}]""";
            using var doc = JsonDocument.Parse(json);
            Assert.True(CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error), error);
            Assert.IsAssignableFrom<WorldChange>(parsed![0]);
            Assert.Equal("crafting_step", parsed[0].GetType().GetCustomAttribute<PluginWorldChangeAttribute>()!.Discriminator);

            var handlerType = plugin.Assembly.GetTypes().First(t => typeof(IWorldChangeHandler).IsAssignableFrom(t) && !t.IsAbstract);
            var handler = (IWorldChangeHandler)Activator.CreateInstance(handlerType)!;
            var encounter = mode.StateMachine.CreateEncounter("locations/forge", ["chars/smith"]);
            var context = ChangeContextTestHelper.Create(
                characters: new() { ["chars/smith"] = new Character { Id = "chars/smith", Name = "Smith", CurrentHp = 10, MaxHp = 10 } },
                activeMode: encounter,
                campaignName: "test");

            var enterOk = mode.StateMachine.TryConsumeActionSlot(encounter.Participants[0], parsed[0], out _);
            Assert.True(enterOk);
            var stepResult = await handler.ApplyAsync(parsed[0], context);
            Assert.True(stepResult.Success);
            Assert.Equal("forge", encounter.Participants[0].State["stage"]?.ToString());

            encounter.Participants[0].State["stage"] = "complete";
            Assert.True(mode.StateMachine.IsComplete(encounter, out var narrative));
            Assert.Contains("Crafting", narrative ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempPlugins, true);
        }
    }

    [Fact]
    public async Task Dispatcher_crafting_step_only_sees_preloaded_ActiveMode()
    {
        var tempPlugins = Path.Combine(Path.GetTempPath(), "cv-crafting-dispatch-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginTestDrop.InstallCrafting(tempPlugins);
            var plugins = PluginAssemblyLoader.LoadPluginsFromDirectory(tempPlugins);
            var plugin = Assert.Single(plugins);

            var modeType = plugin.Assembly.GetTypes().First(t => typeof(IInteractionMode).IsAssignableFrom(t) && !t.IsAbstract);
            var mode = (IInteractionMode)Activator.CreateInstance(modeType)!;
            var handlerType = plugin.Assembly.GetTypes().First(t => typeof(IWorldChangeHandler).IsAssignableFrom(t) && !t.IsAbstract);
            var handler = (IWorldChangeHandler)Activator.CreateInstance(handlerType)!;
            var stepType = plugin.Assembly.GetTypes().First(t => t.Name == "CraftingStepChange");
            var step = (WorldChange)Activator.CreateInstance(stepType)!;
            stepType.GetProperty("CharacterId")!.SetValue(step, "chars/smith");
            stepType.GetProperty("Stage")!.SetValue(step, "forge");

            var keys = new CampaignDocumentKeys();
            var campaign = "test";
            var config = new CampaignConfig { Id = keys.Config(campaign), EnabledModeIds = ["crafting"] };
            var encounter = mode.StateMachine.CreateEncounter("locations/forge", ["chars/smith"]);
            encounter.Id = keys.ModeCurrent(campaign, "crafting");
            encounter.ModeId = "crafting";
            encounter.IsActive = true;

            var session = Substitute.For<IAsyncDocumentSession>();
            session.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, Character>
                {
                    ["chars/smith"] = new Character { Id = "chars/smith", Name = "Smith", CurrentHp = 10, MaxHp = 10 }
                });
            session.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, Item>());
            session.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, Location>());
            session.LoadAsync<CampaignConfig>(Arg.Any<string>()).Returns(config);
            session.LoadAsync<ModeEncounter>(Arg.Any<string>()).Returns(encounter);

            var dispatcher = new WorldChangeDispatcher(
                [handler],
                keys,
                NullLogger<WorldChangeDispatcher>.Instance);

            var result = await dispatcher.DispatchAsync(
                session,
                [step],
                campaign,
                () => Task.FromResult(new CampaignTime()),
                () => Task.FromResult(new Dictionary<string, string>()),
                _ => Task.CompletedTask);

            Assert.True(result.Success, string.Join("; ", result.Summary));
            Assert.Equal("forge", encounter.Participants[0].State["stage"]?.ToString());
        }
        finally
        {
            Directory.Delete(tempPlugins, true);
        }
    }
}
