using Xunit;

namespace CampaignVault.Tests;

[CollectionDefinition("RavenDB")]
public sealed class RavenDbTestCollection : ICollectionFixture<RavenDbTestEnvironment>
{
}
