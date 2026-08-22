using Raven.Client.Documents.Conventions;
using Raven.Client.Json.Serialization.NewtonsoftJson;

namespace CampaignVault.Data;

/// <summary>
/// Single place that wires <see cref="SystemExtensionNewtonsoftConverter"/> into a RavenDB
/// DocumentConventions' Newtonsoft serializer. Shared by production startup (RavenStartup) and the
/// test RavenDB environment (RavenDbTestEnvironment) so tests exercise the exact same serialization
/// behavior as the running server — a converter registered only in production code would leave the
/// bug it fixes invisible to the test suite, same as before this fix existed.
/// </summary>
public static class RavenSerializationConventions
{
    public static void Configure(DocumentConventions conventions)
    {
        conventions.Serialization = new NewtonsoftJsonSerializationConventions
        {
            CustomizeJsonSerializer = serializer => serializer.Converters.Add(new SystemExtensionNewtonsoftConverter()),
            CustomizeJsonDeserializer = serializer => serializer.Converters.Add(new SystemExtensionNewtonsoftConverter()),
        };
    }
}
