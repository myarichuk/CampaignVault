using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class WorldChangeTypeRegistryTests
{
    [PluginWorldChange("test_verb")]
    private sealed class TestVerbChange : WorldChange
    {
        public string? Note { get; set; }
    }

    private sealed class TestVerbHandler : IWorldChangeHandler
    {
        public bool Saw;
        public bool ShouldHandle(WorldChange change) => change is TestVerbChange;
        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
        {
            Saw = true;
            context.RecordMessage("test_verb ok");
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }

    [Fact]
    public void Register_colliding_discriminator_throws()
    {
        var registry = WorldChangeTypeRegistry.CreateDefault();
        registry.Register("test_collision_unique", typeof(TestVerbChange));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register("test_collision_unique", typeof(HpChange)));
    }

    [Fact]
    public async Task Plugin_type_round_trips_deserialize_dispatch_and_schema()
    {
        // Use the process singleton so CommitChangesParser / CommitSchemaModel see the type.
        // Registration/invalidation is not scoped to this test, so it's unregistered again in
        // `finally` — otherwise "test_verb" would leak into every other test sharing this process
        // (xunit runs test classes in parallel by default) for the rest of the run.
        WorldChangeTypeRegistry.Instance.Register("test_verb", typeof(TestVerbChange));
        CommitSchemaModel.Invalidate();
        try
        {
            var json = """[{"$type":"test_verb","note":"hello"}]""";
            using var doc = JsonDocument.Parse(json);
            Assert.True(CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error), error);
            Assert.NotNull(parsed);
            Assert.Single(parsed!);
            Assert.IsType<TestVerbChange>(parsed[0]);
            Assert.Equal("hello", ((TestVerbChange)parsed[0]).Note);

            var handler = new TestVerbHandler();
            var dispatcher = new WorldChangeDispatcher(
                [handler], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
            var result = await dispatcher.DispatchAsync(
                null!, parsed, "test_campaign",
                () => Task.FromResult(new CampaignTime()),
                () => Task.FromResult(new System.Collections.Generic.Dictionary<string, string>()),
                _ => Task.CompletedTask);
            Assert.True(result.Success);
            Assert.True(handler.Saw);

            Assert.Contains(CommitSchemaModel.Variants, v => v.Discriminator == "test_verb");
        }
        finally
        {
            WorldChangeTypeRegistry.Instance.Unregister("test_verb");
            CommitSchemaModel.Invalidate();
        }
    }

    [Fact]
    public async Task FindHandler_fallback_still_dispatches_registry_registered_plugin_type()
    {
        // Regression: do not require BuildHandlerDictionary changes for plugin types.
        // See the comment in Plugin_type_round_trips_deserialize_dispatch_and_schema for why this
        // must be unregistered again.
        WorldChangeTypeRegistry.Instance.Register("test_verb", typeof(TestVerbChange));
        try
        {
            var handler = new TestVerbHandler();
            var dispatcher = new WorldChangeDispatcher(
                [handler], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

            var result = await dispatcher.DispatchAsync(
                null!, [new TestVerbChange { Note = "x" }], "test_campaign",
                () => Task.FromResult(new CampaignTime()),
                () => Task.FromResult(new System.Collections.Generic.Dictionary<string, string>()),
                _ => Task.CompletedTask);

            Assert.True(result.Success);
            Assert.True(handler.Saw);
            Assert.DoesNotContain(result.Summary, s => s.Contains("Unhandled change type"));
        }
        finally
        {
            WorldChangeTypeRegistry.Instance.Unregister("test_verb");
        }
    }
}
