using System;
using System.IO;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class CampaignHistoryServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignHistoryService _service;

    public CampaignHistoryServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "CampaignHistory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _service = new CampaignHistoryService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, true);
        }
        catch { }
    }

    [Fact]
    public void Remove_ExcludesPathFromRecentCampaigns()
    {
        var first = Path.Combine(_tempDirectory, "alpha");
        var second = Path.Combine(_tempDirectory, "beta");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        _service.Add(first);
        _service.Add(second);

        _service.Remove(first);

        var history = _service.Load();
        Assert.DoesNotContain(first, history.RecentPaths);
        Assert.Contains(second, history.RecentPaths);
    }
}
