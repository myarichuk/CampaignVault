using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
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
            var absolute = Path.Combine(session.VaultPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute))
                File.Delete(absolute);
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

    private static object BuildSyncSummaryPayload(VaultSyncSummary summary) => new
    {
        syncedCount = summary.SyncedCount,
        aheadCount = summary.AheadCount,
        behindCount = summary.BehindCount,
        conflictCount = summary.ConflictCount,
        connection = summary.Connection.State.ToString(),
        connectionMessage = summary.Connection.Message,
        remoteCacheCorrupt = summary.RemoteCacheCorrupt,
        lastFetchedAt = summary.LastFetchedAt
    };
}