using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class SourceControlViewModel : ObservableObject
{
    private CampaignVaultSession? _session;
    private Action? _refreshExplorer;

    [ObservableProperty] private string _commitMessage = string.Empty;

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private string _headCommitShort = "—";

    [ObservableProperty] private string _statusMessage = "Open a vault to view git status.";

    [ObservableProperty] private ObservableCollection<string> _changedPaths = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _isBusy;

    public bool CanCommit => IsDirty && !IsBusy;

    public void Bind(CampaignVaultSession? session, Action? refreshExplorer = null)
    {
        _session = session;
        _refreshExplorer = refreshExplorer;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        if (_session is not { IsOpen: true })
        {
            IsDirty = false;
            HeadCommitShort = "—";
            StatusMessage = "No vault open.";
            ChangedPaths.Clear();
            return;
        }

        var gitStatus = _session.GetGitStatus();
        IsDirty = gitStatus.IsDirty;
        HeadCommitShort = ShortSha(_session.HeadCommitSha);

        ChangedPaths.Clear();
        foreach (var path in gitStatus.ModifiedPaths
                     .Concat(gitStatus.AddedPaths)
                     .Concat(gitStatus.RemovedPaths)
                     .Concat(gitStatus.UntrackedPaths)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ChangedPaths.Add(path);
        }

        StatusMessage = gitStatus.IsDirty
            ? $"{ChangedPaths.Count} uncommitted change(s)."
            : "Working tree clean.";
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (IsBusy) return;

        if (_session is not { IsOpen: true })
        {
            StatusMessage = "No vault open.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "Enter a commit message.";
            return;
        }

        IsBusy = true;
        try
        {
            await _session.CommitAsync(CommitMessage.Trim());
            CommitMessage = string.Empty;
            RefreshStatus();
            _refreshExplorer?.Invoke();
            StatusMessage = $"Committed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Commit failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "—" : sha.Length > 7 ? sha[..7] : sha;
}