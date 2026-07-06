using System;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;

namespace CampaignVault.Tests;

/// <summary>
/// Shared slug for tests that omit explicit campaignName on the legacy CampaignTools facade.
/// </summary>
internal static class TestCampaignDefaults
{
    public const string Slug = "test-campaign";

    public static async Task EnsureExistsAsync(RavenDBFixture fixture, string slug = Slug)
    {
        var tools = TestCampaignToolsFactory.Create(fixture);
        var created = await tools.CreateCampaign(slug, RulesetSystem.Dnd5e);
        if (!created.Success && created.Error != "AlreadyExists")
        {
            throw new InvalidOperationException($"Failed to ensure test campaign '{slug}': {created.Summary}");
        }
    }

    public static async Task EnsureExistsAsync(CampaignTools tools, string slug = Slug,
        RulesetSystem system = RulesetSystem.Dnd5e)
    {
        var created = await tools.CreateCampaign(slug, system);
        if (!created.Success && created.Error != "AlreadyExists")
        {
            throw new InvalidOperationException($"Failed to ensure test campaign '{slug}': {created.Summary}");
        }
    }
}