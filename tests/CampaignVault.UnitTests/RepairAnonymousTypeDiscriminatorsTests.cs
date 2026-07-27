using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CampaignVault.Data.Migrations;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Reproduces the startup-killing bug where an Event.Details value tagged with a stale
/// "$type": "&lt;&gt;f__AnonymousTypeN..." (from a build whose anonymous-type numbering no longer
/// matches the current assembly) makes any typed load of that Event throw
/// Newtonsoft.Json.JsonSerializationException. Writes the corrupted shape directly over HTTP so it
/// mirrors exactly what an old build's Newtonsoft serializer would have persisted, independent of
/// the app's current (now-fixed) serialization code.
/// </summary>
[Collection("RavenDB")]
public class RepairAnonymousTypeDiscriminatorsTests
{
    private readonly RavenDbTestEnvironment _environment;

    public RepairAnonymousTypeDiscriminatorsTests(RavenDbTestEnvironment environment)
    {
        _environment = environment;
    }

    [Fact]
    public async Task Repair_FixesLegacyAnonymousTypeDiscriminator_SoEventLoadsCleanly()
    {
        var (store, _) = _environment.CreateStoreForClass($"AnonTypeRepair_{Guid.NewGuid():N}");
        var docId = $"Events/{Guid.NewGuid():N}";

        await PutCorruptedEventDocumentAsync(store, docId);

        // Sanity check: the corrupted shape actually reproduces the crash before repair runs.
        // RavenDB's session wraps the underlying Newtonsoft.Json.JsonSerializationException
        // ("Could not find type '<>f__AnonymousType...'") in an InvalidOperationException.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var session = store.OpenAsyncSession();
            await session.LoadAsync<Event>(docId);
        });

        var repair = new RepairAnonymousTypeDiscriminators(store);
        await repair.ExecuteAsync();

        using (var session = store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Event>(docId);
            Assert.NotNull(loaded);
            Assert.Equal("Test event with corrupted Details", loaded.Summary);
            Assert.True(loaded.Details!.ContainsKey("factsDiscovered"));
        }
    }

    private static async Task PutCorruptedEventDocumentAsync(IDocumentStore store, string docId)
    {
        const string json = """
            {
                "Timestamp": "2026-01-01T00:00:00Z",
                "DayLogged": 1,
                "Category": "Unresolved",
                "Summary": "Test event with corrupted Details",
                "Details": {
                    "factsDiscovered": {
                        "$type": "System.Collections.Generic.List`1[[System.Object, System.Private.CoreLib]], System.Private.CoreLib",
                        "$values": [
                            {
                                "$type": "<>f__AnonymousType99`3[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], CampaignVault",
                                "characterId": "chars/test",
                                "actionType": "Attack",
                                "actionName": "Sword Strike"
                            }
                        ]
                    }
                },
                "Involved": [],
                "@metadata": { "@collection": "Events" }
            }
            """;

        using var client = new HttpClient();
        var url = $"{store.Urls[0]}/databases/{store.Database}/docs?id={Uri.EscapeDataString(docId)}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(url, content);
        response.EnsureSuccessStatusCode();
    }
}
