using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// PR3a canaries: core $type discriminators round-trip through CommitChangesParser options.
/// </summary>
public class WorldChangeSerializationCanaryTests
{
    public static TheoryData<string, Type> CoreDiscriminators
    {
        get
        {
            var data = new TheoryData<string, Type>();
            foreach (var (discriminator, type) in WorldChangeTypeRegistry.CreateDefault().Entries
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                data.Add(discriminator, type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CoreDiscriminators))]
    public void Core_discriminator_round_trips(string discriminator, Type changeType)
    {
        var instance = (WorldChange)Activator.CreateInstance(changeType)!;
        var options = CommitChangesParser.CreateOptions();
        var json = JsonSerializer.Serialize(instance, typeof(WorldChange), options);
        Assert.Contains($"\"$type\":\"{discriminator}\"", json);

        var parsed = JsonSerializer.Deserialize<WorldChange>(json, options);
        Assert.NotNull(parsed);
        Assert.IsType(changeType, parsed);
    }

    [Fact]
    public void Registry_seed_preserves_known_hot_path_discriminators()
    {
        var entries = WorldChangeTypeRegistry.CreateDefault().Entries;
        foreach (var expected in new[]
                 {
                     "hp", "activity", "event", "travel", "rest", "ruleset_action",
                     "item", "status", "status_remove", "character_update", "location_update",
                     "quest_progress", "mode_transition"
                 })
        {
            Assert.True(entries.ContainsKey(expected), $"missing discriminator '{expected}'");
        }
    }

    [Fact]
    public void PluginSdk_has_no_RavenDB_Client_reference()
    {
        var refs = typeof(WorldChange).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refs, a => a.Name is "RavenDB.Client" or "RavenDB.Embedded");
    }
}
