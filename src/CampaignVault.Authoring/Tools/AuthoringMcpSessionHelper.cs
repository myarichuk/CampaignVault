using System;
using System.IO;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Grpc;

namespace CampaignVault.Authoring.Tools;

internal static class AuthoringMcpSessionHelper
{
    public const string NoVaultError =
        "No campaign vault is open. Open or create a vault in the authoring app first.";

    public static CampaignVaultSession? TestSessionOverride { get; set; }

    public static CampaignVaultSession? TryGetOpenSession(out AuthoringToolResult? errorResult)
    {
        errorResult = null;
        if (TestSessionOverride != null) return TestSessionOverride;

        var session = (App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState)?.Session;
        if (session is not { IsOpen: true })
        {
            errorResult = new AuthoringToolResult(success: false, error: NoVaultError);
            return null;
        }

        return session;
    }

    public static void EnsureSyncConfigured(CampaignVaultSession session)
    {
        var mainVm = App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState;
        if (mainVm != null)
        {
            mainVm.Sync.ConfigureSessionSync();
            return;
        }

        if (session.IsVaultSyncConfigured)
            return;

        var settings = new SettingsService().LoadSettings();
        var authoringSettings = new CampaignAuthoringSettings
        {
            GrpcHost = settings.GrpcHost,
            GrpcPort = settings.GrpcPort,
            GrpcToken = settings.GrpcToken
        };

        session.ConfigureVaultSync(
            () => VaultGrpcClientFactory.CreateClient(
                settings.GrpcHost,
                settings.GrpcPort,
                string.IsNullOrWhiteSpace(settings.GrpcToken) ? null : settings.GrpcToken),
            authoringSettings);
    }

    public static string ResolveEntityRelativePath(CampaignVaultSession session, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Path is required.", nameof(filePath));

        var vaultPath = session.VaultPath!;
        string relative;

        if (Path.IsPathRooted(filePath))
        {
            var full = Path.GetFullPath(filePath);
            var vaultFull = Path.GetFullPath(vaultPath);
            if (!full.StartsWith(vaultFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, vaultFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultException($"Path is outside the open vault: '{filePath}'.");
            }

            relative = Path.GetRelativePath(vaultFull, full).Replace('\\', '/');
        }
        else
        {
            relative = filePath.Replace('\\', '/').TrimStart('/');
        }

        if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative += ".md";

        if (!VaultPaths.IsEntityRelativePath(relative))
            throw new VaultException($"Not a campaign entity path: '{relative}'.");

        return relative;
    }

    public static void RefreshUiIfAvailable() =>
        (App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState)?.RefreshAll();
}