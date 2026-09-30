using System.Runtime.CompilerServices;

// Pure-C# seams (parsers, compaction, envelope reading) are internal; the
// EditMode suite pins them without widening the public surface.
[assembly: InternalsVisibleTo("CampaignVault.Client.Tests")]
[assembly: InternalsVisibleTo("CampaignVault.Client.TestSupport")]
[assembly: InternalsVisibleTo("CampaignVault.Client.PlayTests")]
