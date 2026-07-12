using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Grpc;
using ModelContextProtocol.Server;

namespace CampaignVault.Authoring.Tools;

public record AuthoringToolResult(
    bool success,
    string? error = null,
    object? files = null,
    string? content = null,
    string? path = null,
    object? summary = null);

[McpServerToolType]
public class AuthoringMcpTools
{
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Returns the open vault's current state: path, git HEAD/synced commit SHAs, working-tree dirty status, and sync summary. Use this for a single orientation call before deciding what sync action to take next.")]
    public Task<AuthoringToolResult> GetVaultStatus()
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return Task.FromResult(error!);

        var gitStatus = session.GetGitStatus();
        var summary = session.GetSyncSummary();

        var payload = new Dictionary<string, object>
        {
            { "vaultPath", session.VaultPath ?? "" },
            { "headCommitSha", session.HeadCommitSha ?? "" },
            { "syncedCommitSha", session.SyncedCommitSha ?? "" },
            { "isDirty", gitStatus.IsDirty },
            { "modifiedPaths", gitStatus.ModifiedPaths ?? [] },
            { "addedPaths", gitStatus.AddedPaths ?? [] },
            { "removedPaths", gitStatus.RemovedPaths ?? [] },
            { "untrackedPaths", gitStatus.UntrackedPaths ?? [] },
            { "sync", BuildSyncSummaryPayload(summary) }
        };

        return Task.FromResult(new AuthoringToolResult(success: true, summary: payload));
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Lists all campaign entities in the open vault (characters, locations, quests, factions, lore, rumors, events).")]
    public Task<AuthoringToolResult> ListWorkspaceEntities()
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return Task.FromResult(error!);

        var syncPlans = session.GetEntitySyncPlans()
            .ToDictionary(p => p.EntityId, StringComparer.OrdinalIgnoreCase);

        var entities = session.ScanEntities()
            .Select(e =>
            {
                syncPlans.TryGetValue(e.Id, out var plan);
                return new
                {
                    id = e.Id,
                    entityType = e.EntityType,
                    relativePath = e.RelativePath,
                    displayName = VaultEntityDisplay.GetDisplayName(e, session.VaultPath),
                    syncState = (plan?.State ?? VaultSyncState.LocalOnly).ToString(),
                    hasValidFrontmatter = e.HasValidFrontmatter,
                    parseError = e.ParseError
                };
            })
            .ToList();

        return Task.FromResult(new AuthoringToolResult(success: true, files: entities));
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Reads a campaign entity markdown file from the open vault. Accepts a vault-relative path (e.g. characters/grog.md) or absolute path inside the vault.")]
    public async Task<AuthoringToolResult> ReadWorkspaceEntity(
        [Description("Vault-relative or absolute path to the entity markdown file.")]
        string filePath)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            var relativePath = AuthoringMcpSessionHelper.ResolveEntityRelativePath(session, filePath);
            var content = await session.ReadFileAsync(relativePath);
            return new AuthoringToolResult(success: true, content: content, path: relativePath);
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Writes or updates a campaign entity markdown file in the open vault. Accepts a vault-relative path or absolute path inside the vault.")]
    public async Task<AuthoringToolResult> WriteWorkspaceEntity(
        [Description("Vault-relative or absolute path to the entity markdown file.")]
        string filePath,
        [Description("Full file content including YAML frontmatter and markdown body.")]
        string content)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            var yamlIssues = YamlFrontmatterValidator.ValidateDocument(content);
            if (yamlIssues.Count > 0)
            {
                return new AuthoringToolResult(success: false,
                    error: $"YAML frontmatter invalid (line {yamlIssues[0].Line}): {yamlIssues[0].Message}");
            }

            var relativePath = AuthoringMcpSessionHelper.ResolveEntityRelativePath(session, filePath);
            await session.WriteFileAsync(relativePath, content);
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, path: relativePath);
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Deletes a campaign entity markdown file from the open vault. Accepts a vault-relative path or absolute path inside the vault. This removes the local file; commit to persist the deletion in git.")]
    public async Task<AuthoringToolResult> DeleteWorkspaceEntity(
        [Description("Vault-relative or absolute path to the entity markdown file to delete.")]
        string filePath)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            var relativePath = AuthoringMcpSessionHelper.ResolveEntityRelativePath(session, filePath);
            await session.DeleteEntityFileAsync(relativePath);
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, path: relativePath);
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Fetches the remote Campaign Vault snapshot into the local cache (.cv/remote-cache). Does not modify vault files.")]
    public async Task<AuthoringToolResult> FetchVault()
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            AuthoringMcpSessionHelper.EnsureSyncConfigured(session);
            await session.FetchAsync();
            var summary = session.GetSyncSummary();
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, summary: BuildSyncSummaryPayload(summary));
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Pushes local entity changes to Campaign Vault via gRPC. Requires a clean working tree for a full push.")]
    public async Task<AuthoringToolResult> PushToVault(
        [Description("Optional entity IDs to push (e.g. characters/grog). Omit to push all pending entities.")]
        string[]? entityIds = null)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            AuthoringMcpSessionHelper.EnsureSyncConfigured(session);
            await session.PushAsync(entityIds);
            var summary = session.GetSyncSummary();
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, summary: BuildSyncSummaryPayload(summary));
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("Pulls entity changes from the Campaign Vault remote cache into the local vault.")]
    public async Task<AuthoringToolResult> PullFromVault(
        [Description("Optional entity IDs to pull (e.g. characters/grog). Omit to pull all pending entities.")]
        string[]? entityIds = null)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            AuthoringMcpSessionHelper.EnsureSyncConfigured(session);
            await session.PullAsync(entityIds);
            var summary = session.GetSyncSummary();
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, summary: BuildSyncSummaryPayload(summary));
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Commits all pending local changes in the vault's git repository. All modified/added/untracked files are staged and committed. Required before Push (push requires a clean working tree).")]
    public async Task<AuthoringToolResult> CommitVault(
        [Description("Commit message describing the change.")]
        string message)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            await session.CommitAsync(message);
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true,
                summary: new Dictionary<string, object> { { "headCommitSha", session.HeadCommitSha ?? "" } });
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Resolves a conflicted entity by choosing: KeepLocal (push the local version), KeepVault (overwrite local with remote), or Merged (supply your own merged content).")]
    public async Task<AuthoringToolResult> ResolveVaultConflict(
        [Description("Entity id in 'folder/name' form, e.g. 'characters/grog' (no .md extension).")]
        string entityId,
        [Description("Resolution method: KeepLocal, KeepVault, or Merged.")]
        string resolution,
        [Description("Required only when resolution is Merged: the full merged markdown+frontmatter content to write.")]
        string? mergedContent = null)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        if (!Enum.TryParse<ConflictResolution>(resolution, ignoreCase: true, out var parsed))
        {
            return new AuthoringToolResult(success: false,
                error: $"Unknown resolution '{resolution}'. Expected KeepLocal, KeepVault, or Merged.");
        }

        try
        {
            AuthoringMcpSessionHelper.EnsureSyncConfigured(session);
            await session.ResolveConflictAsync(entityId, parsed, mergedContent);
            var summary = session.GetSyncSummary();
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true,
                summary: BuildSyncSummaryPayload(summary),
                path: entityId);
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Lists campaigns on the main Campaign Vault server via gRPC CampaignSync. Uses authoring Settings grpc host/port/token — not the play MCP HTTP port.")]
    public async Task<AuthoringToolResult> ListServerCampaigns()
    {
        try
        {
            var settings = new SettingsService().LoadSettings();
            var client = VaultGrpcClientFactory.CreateClient(
                settings.GrpcHost,
                settings.GrpcPort,
                string.IsNullOrWhiteSpace(settings.GrpcToken) ? null : settings.GrpcToken);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await client.GetCampaignsAsync(
                new EmptyRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cts.Token);
            var campaigns = response.Campaigns
                .Select(c => new { name = c.Name, ruleset = c.Ruleset })
                .ToList();
            return new AuthoringToolResult(success: true, files: campaigns);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false,
                error: $"Failed to list server campaigns via gRPC: {ex.Message}");
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Creates a new campaign entity with a blank template in the open vault (character, location, quest, faction, lore, rumor, event, or item). Returns the relative path and starter YAML+markdown content; edit with WriteWorkspaceEntity, then CommitVault.")]
    public async Task<AuthoringToolResult> CreateWorkspaceEntity(
        [Description("Entity type: character, location, quest, faction, lore, rumor, event, or item.")]
        string entityType,
        [Description("Display name for the new entity.")]
        string name)
    {
        if (AuthoringMcpSessionHelper.TryGetOpenSession(out var error) is not { } session)
            return error!;

        try
        {
            var (relativePath, content) = await session.CreateEntityAsync(entityType, name);
            AuthoringMcpSessionHelper.RefreshUiIfAvailable();
            return new AuthoringToolResult(success: true, path: relativePath, content: content);
        }
        catch (VaultException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
        catch (Exception ex)
        {
            return new AuthoringToolResult(success: false, error: ex.Message);
        }
    }

    private static object BuildSyncSummaryPayload(VaultSyncSummary summary) => new Dictionary<string, object>
    {
        { "syncedCount", summary.SyncedCount },
        { "aheadCount", summary.AheadCount },
        { "behindCount", summary.BehindCount },
        { "conflictCount", summary.ConflictCount },
        { "connection", summary.Connection.State.ToString() },
        { "connectionMessage", summary.Connection.Message ?? "" },
        { "remoteCacheCorrupt", summary.RemoteCacheCorrupt },
        { "lastFetchedAt", summary.LastFetchedAt?.ToString() ?? "" }
    };
}