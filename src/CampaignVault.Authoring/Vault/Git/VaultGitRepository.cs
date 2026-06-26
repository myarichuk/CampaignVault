using System;
using System.Collections.Generic;
using System.Linq;
using LibGit2Sharp;

namespace CampaignVault.Authoring.Vault.Git;

public sealed class VaultGitRepository : IDisposable
{
    private Repository? _repository;

    public string RepositoryPath { get; private set; } = string.Empty;

    public bool IsOpen => _repository != null;

    public static bool IsGitRepository(string vaultPath)
    {
        return Repository.IsValid(vaultPath);
    }

    public void Open(string vaultPath)
    {
        if (!Repository.IsValid(vaultPath))
            throw new VaultException($"No git repository found at '{vaultPath}'.");

        Dispose();
        RepositoryPath = vaultPath;
        _repository = new Repository(vaultPath);
    }

    public void Init(string vaultPath, string initialCommitMessage)
    {
        if (Repository.IsValid(vaultPath))
            throw new VaultException($"A git repository already exists at '{vaultPath}'.");

        Dispose();
        RepositoryPath = vaultPath;
        Repository.Init(vaultPath, isBare: false);

        _repository = new Repository(vaultPath);
        var signature = CreateSignature();

        Commands.Stage(_repository, "*");
        var commit = _repository.Commit(
            initialCommitMessage,
            signature,
            signature,
            new CommitOptions());

        var mainBranch = _repository.Branches[VaultPaths.DefaultBranchName]
                         ?? _repository.CreateBranch(VaultPaths.DefaultBranchName, commit);
        Commands.Checkout(_repository, mainBranch);
        SetSyncedCommit(commit.Sha);
    }

    public string? GetHeadSha()
    {
        EnsureOpen();
        return _repository!.Head.Tip?.Sha;
    }

    public string? GetSyncedCommitSha()
    {
        EnsureOpen();
        return _repository!.Refs[VaultPaths.SyncedRefName]?.TargetIdentifier;
    }

    public void SetSyncedCommit(string commitSha)
    {
        EnsureOpen();
        if (_repository!.Lookup<Commit>(commitSha) is null)
            throw new VaultException($"Commit '{commitSha}' was not found in the repository.");

        var refs = _repository.Refs
                   ?? throw new InvalidOperationException("The git repository has no reference collection.");

        if (refs[VaultPaths.SyncedRefName] is { } existing)
            refs.UpdateTarget(existing, commitSha);
        else
            refs.Add(VaultPaths.SyncedRefName, commitSha);
    }

    public void RequireSyncedRef()
    {
        EnsureOpen();
        if (GetSyncedCommitSha() != null)
            return;

        throw new VaultException(
            "Missing refs/cv/synced publish cursor. Create a new vault or repair the ref manually — " +
            "opening a vault does not auto-initialize the synced ref.");
    }

    public string Commit(string message)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Commit message is required.", nameof(message));

        var signature = CreateSignature();
        Commands.Stage(_repository!, "*");
        var commit = _repository!.Commit(message, signature, signature, new CommitOptions());
        return commit.Sha;
    }

    public GitWorkingTreeStatus GetWorkingTreeStatus()
    {
        EnsureOpen();
        var status = _repository!.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        });

        return new GitWorkingTreeStatus(
            IsDirty: status.IsDirty,
            ModifiedPaths: status.Modified.Select(i => i.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            AddedPaths: status.Added.Select(i => i.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            RemovedPaths: status.Removed.Select(i => i.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            UntrackedPaths: status.Untracked.Select(i => i.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
        );
    }

    public string? TryReadFileAtCommit(string? commitSha, string relativePath)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(commitSha))
            return null;

        var commit = _repository!.Lookup<Commit>(commitSha);
        if (commit == null)
            return null;

        var normalizedPath = relativePath.Replace('\\', '/');
        if (commit.Tree[normalizedPath]?.Target is not Blob blob)
            return null;

        return blob.GetContentText();
    }

    public IReadOnlyList<string> GetEntityPathsAtCommit(string? commitSha)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(commitSha))
            return [];

        var commit = _repository!.Lookup<Commit>(commitSha);
        if (commit == null)
            return [];

        return EnumerateEntityBlobPaths(commit.Tree)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateEntityBlobPaths(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.Target is Tree childTree)
            {
                foreach (var nested in EnumerateEntityBlobPaths(childTree, path))
                    yield return nested;
                continue;
            }

            if (entry.Target is Blob && VaultPaths.IsEntityRelativePath(path))
                yield return path;
        }
    }

    public IReadOnlyList<string> GetChangedEntityPathsBetween(string? fromCommitSha, string? toCommitSha)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(fromCommitSha) || string.IsNullOrWhiteSpace(toCommitSha))
            return [];

        var fromCommit = _repository!.Lookup<Commit>(fromCommitSha);
        var toCommit = _repository.Lookup<Commit>(toCommitSha);
        if (fromCommit == null || toCommit == null)
            return [];

        if (fromCommit.Sha == toCommit.Sha)
            return [];

        var changes = _repository.Diff.Compare<TreeChanges>(fromCommit.Tree, toCommit.Tree);
        return changes
            .Select(c => c.Status == ChangeKind.Deleted ? c.OldPath : c.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p) && VaultPaths.IsEntityRelativePath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        _repository?.Dispose();
        _repository = null;
        RepositoryPath = string.Empty;
    }

    private void EnsureOpen()
    {
        if (_repository == null)
            throw new InvalidOperationException("The git repository is not open.");
    }

    private static Signature CreateSignature()
    {
        return new Signature("CampaignVault Authoring", "authoring@campaignvault.local", DateTimeOffset.UtcNow);
    }
}

public sealed record GitWorkingTreeStatus(
    bool IsDirty,
    IReadOnlyList<string> ModifiedPaths,
    IReadOnlyList<string> AddedPaths,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> UntrackedPaths);