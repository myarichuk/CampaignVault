using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Authoring.Vault;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class SourceControlViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session;
    private readonly SourceControlViewModel _sourceControl;

    public SourceControlViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "VaultSourceCtl_" + Guid.NewGuid().ToString("N"));
        _session = new CampaignVaultSession();
        _sourceControl = new SourceControlViewModel();
    }

    public void Dispose()
    {
        _session.Dispose();
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task RefreshStatus_ReportsDirty_WhenWorkingTreeHasChanges()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        _sourceControl.Bind(_session);
        _sourceControl.RefreshStatus();
        Assert.False(_sourceControl.IsDirty);

        var entityPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
        await File.WriteAllTextAsync(entityPath, "---\nid: characters/grog\nname: Grog\n---\n\nUpdated notes.");
        _sourceControl.RefreshStatus();
        Assert.True(_sourceControl.IsDirty);
        Assert.NotEmpty(_sourceControl.ChangedPaths);
    }

    [Fact]
    public async Task CommitAsync_ClearsDirtyState()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var entityPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
        await File.WriteAllTextAsync(entityPath, "---\nid: characters/grog\nname: Grog\n---\n\nNotes.");

        _sourceControl.Bind(_session);
        _sourceControl.CommitMessage = "Add Grog";
        await _sourceControl.CommitCommand.ExecuteAsync(null);

        _sourceControl.RefreshStatus();
        Assert.False(_sourceControl.IsDirty);
    }
}