using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Git;
using Xunit;

namespace CampaignVault.Tests.Authoring.Vault;

public sealed class CampaignVaultSessionTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();

    public CampaignVaultSessionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "cv_vault_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _session.Dispose();
        TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public async Task CreateAsync_InitializesVaultLayoutGitAndSyncedRef()
    {
        var metadata = await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e");

        Assert.True(_session.IsOpen);
        Assert.Equal(_tempDirectory, _session.VaultPath);
        Assert.Equal("test-campaign", metadata.CampaignName);
        Assert.Equal("Dnd5e", metadata.Ruleset);
        Assert.Equal(1, metadata.SchemaVersion);

        Assert.True(File.Exists(Path.Combine(_tempDirectory, VaultPaths.MetadataFileName)));
        Assert.True(File.Exists(Path.Combine(_tempDirectory, VaultPaths.GitIgnoreFileName)));
        Assert.True(Directory.Exists(Path.Combine(_tempDirectory, VaultPaths.AppConfigDirectoryName)));
        Assert.True(VaultGitRepository.IsGitRepository(_tempDirectory));

        foreach (var (folder, _) in VaultPaths.EntityFolders)
            Assert.True(Directory.Exists(Path.Combine(_tempDirectory, folder)));

        var gitIgnore = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, VaultPaths.GitIgnoreFileName));
        Assert.Contains(".cv/", gitIgnore);

        Assert.False(string.IsNullOrWhiteSpace(_session.HeadCommitSha));
        Assert.Equal(_session.HeadCommitSha, _session.SyncedCommitSha);
    }

    [Fact]
    public async Task OpenAsync_LoadsExistingVault()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");
        var expectedHead = _session.HeadCommitSha;
        var expectedSynced = _session.SyncedCommitSha;
        await _session.CloseAsync();

        using var reopened = new CampaignVaultSession();
        await reopened.OpenAsync(_tempDirectory);

        Assert.Equal("test-campaign", reopened.Metadata?.CampaignName);
        Assert.Equal(expectedHead, reopened.HeadCommitSha);
        Assert.Equal(expectedSynced, reopened.SyncedCommitSha);
    }

    [Fact]
    public async Task OpenAsync_ThrowsWhenMetadataMissing()
    {
        Directory.CreateDirectory(_tempDirectory);
        VaultBootstrap.WriteLayout(_tempDirectory);

        using var git = new VaultGitRepository();
        git.Init(_tempDirectory, "test");

        var session = new CampaignVaultSession();
        var ex = await Assert.ThrowsAsync<VaultException>(() => session.OpenAsync(_tempDirectory));
        Assert.Contains("campaignName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateMetadataAsync_PersistsDisplayNameAndNarrativeFocus()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e", "Original Name");

        await _session.UpdateMetadataAsync("New Name", ["heist", "intrigue"]);

        Assert.Equal("New Name", _session.Metadata!.DisplayName);
        Assert.Equal(["heist", "intrigue"], _session.Metadata.NarrativeFocus);

        using var reopened = new CampaignVaultSession();
        await reopened.OpenAsync(_tempDirectory);
        Assert.Equal("New Name", reopened.Metadata!.DisplayName);
        Assert.Equal(["heist", "intrigue"], reopened.Metadata.NarrativeFocus);
    }

    [Fact]
    public async Task RenameEntityAsync_MovesFileAndUpdatesId()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var grogPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        await File.WriteAllTextAsync(grogPath, """
            ---
            id: characters/grog
            name: Grog
            ---

            A brave warrior.
            """);

        var newRelativePath = await _session.RenameEntityAsync("characters/grog.md", "Groggan the Bold");

        Assert.Equal("characters/groggan-the-bold.md", newRelativePath);
        Assert.False(File.Exists(grogPath));

        var newContent = await _session.ReadFileAsync(newRelativePath);
        Assert.Contains("id: characters/groggan-the-bold", newContent);
        Assert.Contains("A brave warrior.", newContent);
    }

    [Fact]
    public async Task ScanEntities_FindsMarkdownEntitiesAndHashes()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var grogPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        await File.WriteAllTextAsync(grogPath, """
            ---
            id: characters/grog
            name: Grog
            ---

            A brave warrior.
            """);

        var entities = _session.ScanEntities();

        Assert.Single(entities);
        var grog = entities[0];
        Assert.Equal("characters/grog", grog.Id);
        Assert.Equal("character", grog.EntityType);
        Assert.Equal("characters/grog.md", grog.RelativePath);
        Assert.True(grog.HasValidFrontmatter);
        Assert.False(string.IsNullOrWhiteSpace(grog.ContentHash));
    }

    [Fact]
    public async Task ScanEntities_FlagsMissingFrontmatter()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var badPath = Path.Combine(_tempDirectory, "characters", "broken.md");
        await File.WriteAllTextAsync(badPath, "No frontmatter here.");

        var entity = Assert.Single(_session.ScanEntities());
        Assert.False(entity.HasValidFrontmatter);
        Assert.Contains("frontmatter", entity.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanEntities_SupportsNestedFolders()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var npcDir = Path.Combine(_tempDirectory, "characters", "npcs");
        Directory.CreateDirectory(npcDir);
        await File.WriteAllTextAsync(Path.Combine(npcDir, "grog.md"), """
            ---
            id: characters/npcs/grog
            name: Grog
            ---

            Notes
            """);

        var entity = Assert.Single(_session.ScanEntities());
        Assert.Equal("characters/npcs/grog", entity.Id);
        Assert.Equal("characters/npcs/grog.md", entity.RelativePath);
    }

    [Fact]
    public async Task GetGitStatus_ReportsUntrackedEntityFiles()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "characters", "grog.md"), """
            ---
            id: characters/grog
            name: Grog
            ---

            Notes
            """);

        var status = _session.GetGitStatus();
        Assert.True(status.IsDirty);
        Assert.Contains(status.UntrackedPaths, p => p.Replace('\\', '/') == "characters/grog.md");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}