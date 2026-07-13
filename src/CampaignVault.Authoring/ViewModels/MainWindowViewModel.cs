using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IWorkspaceState
{
    private static readonly YamlDotNet.Serialization.ISerializer _yamlSerializer =
        new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

    public CampaignVaultSession Session { get; } = new();

    public WorkspaceViewModel Workspace { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public GenerationViewModel Generation { get; }
    public SyncViewModel Sync { get; }
    public SourceControlViewModel SourceControl { get; } = new();
    public HubViewModel Hub { get; }

    [ObservableProperty] private AppStateService _applicationState = new();

    public Dock.Model.Core.IFactory Factory { get; }
    public Dock.Model.Controls.IRootDock? Layout { get; }

    public McpServerService? McpServerService { get; set; }

    private readonly WorkspaceParser _parser = new();
    private readonly CampaignVault.Authoring.Vault.Canonical.EntityCanonicalizer _canonicalizer = new();
    private readonly CampaignHistoryService _historyService = new();
    private IStorageProvider? _storageProvider;

    [ObservableProperty] private string _editorText = string.Empty;

    private string _lastLoadedEditorText = string.Empty;
    private ExplorerNodeViewModel? _lastSelectedNode;
    private bool _suppressSelectionGuard;

    public bool IsEditorDirty => EditorText != _lastLoadedEditorText;

    private string CurrentEntityTitle =>
        Workspace.SelectedNode is EntityNodeViewModel entityNode
            ? entityNode.Title
            : "current entity";

    [ObservableProperty] private string _workspaceStatusMessage = "Open a campaign vault to begin.";

    [ObservableProperty] private Character? _parsedCharacter;

    [ObservableProperty] private string _previewNotes = string.Empty;

    [ObservableProperty] private string _entityTypeDisplay = "Character";

    [ObservableProperty] private bool _isCharacter = true;

    [ObservableProperty] private string _badge1Label = string.Empty;

    [ObservableProperty] private string _badge1Value = string.Empty;

    [ObservableProperty] private string _badge2Label = string.Empty;

    [ObservableProperty] private string _badge2Value = string.Empty;

    [ObservableProperty] private string _statusBarGit = "—";

    [ObservableProperty] private string _statusBarSync = "Vault: not open";

    [ObservableProperty] private string _statusBarConnection = "offline";

    [ObservableProperty] private bool _showStatusBanner;

    [ObservableProperty] private string _statusBannerMessage = string.Empty;

    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => Workspace.SelectedNode is EntityNodeViewModel;

    public bool HasParseError => Workspace.SelectedNode is EntityNodeViewModel && ParsedCharacter == null &&
                                 !string.IsNullOrEmpty(EditorText);

    public MainWindowViewModel()
    {
        Sync = new SyncViewModel(Settings);
        Generation = new GenerationViewModel(Settings);
        Hub = new HubViewModel(this);

        Workspace.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedNode))
            {
                // Reentrancy guard: if suppress flag is set, this is a confirmed change or revert
                if (_suppressSelectionGuard)
                {
                    _suppressSelectionGuard = false;
                    // Fall through to normal load logic below
                }
                else if (IsEditorDirty && _lastSelectedNode != null)
                {
                    // User has unsaved changes and is trying to switch to a different entity
                    var newNode = Workspace.SelectedNode;

                    // Revert selection to trigger this handler again with suppress flag set
                    _suppressSelectionGuard = true;
                    Workspace.SelectedNode = _lastSelectedNode;

                    // Show confirmation dialog
                    var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                        ? desktop.MainWindow
                        : null;

                    if (owner != null && newNode is EntityNodeViewModel newEntity)
                    {
                        var confirmed = await Views.ConfirmationDialog.ShowAsync(
                            owner,
                            "Unsaved Changes",
                            $"Leave '{_lastSelectedNode.Title}' without saving changes to '{newEntity.Title}'?",
                            "Leave");

                        if (confirmed)
                        {
                            // User confirmed: switch to new node
                            _suppressSelectionGuard = true;
                            Workspace.SelectedNode = newNode;
                        }
                        // If cancelled, selection is already reverted; do nothing
                    }
                    return;
                }

                // Normal load path
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasParseError));

                if (Workspace.SelectedNode is EntityNodeViewModel entityNode)
                {
                    try
                    {
                        var content = await Session.ReadFileAsync(entityNode.Entity.RelativePath);
                        EditorText = content;
                        _lastLoadedEditorText = content;
                        OnEditorTextChanged(EditorText);
                        _lastSelectedNode = entityNode;
                    }
                    catch
                    {
                        EditorText = string.Empty;
                        _lastLoadedEditorText = string.Empty;
                        _lastSelectedNode = entityNode;
                    }
                }
                else
                {
                    _lastSelectedNode = Workspace.SelectedNode;
                }
            }
        };

        Factory = new AuthoringDockFactory(this);
        Layout = Factory.CreateLayout();
        if (Layout != null)
            Factory.InitLayout(Layout);
    }

    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;

    public async Task<string?> PickFolderAsync()
    {
        if (_storageProvider == null)
        {
            WorkspaceStatusMessage = "Folder picker is not available yet. Restart the app.";
            return null;
        }

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Campaign Vault Folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null) return null;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            WorkspaceStatusMessage = "Could not resolve the selected folder path.";
            return null;
        }

        return path;
    }

    [RelayCommand]
    private async Task OpenCampaignFolderAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var path = await PickFolderAsync();
            if (path != null)
                await LoadCampaignAsync(path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackToHub()
    {
        if (IsEditorDirty)
        {
            var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (owner != null)
            {
                var confirmed = await Views.ConfirmationDialog.ShowAsync(
                    owner,
                    "Unsaved Changes",
                    $"You have unsaved changes to '{CurrentEntityTitle}'.\n\nLeave without saving?",
                    "Leave",
                    isDestructive: true);

                if (!confirmed)
                    return;
            }
        }

        await Session.CloseAsync();
        Workspace.BindSession(null);
        Sync.Bind(null);
        SourceControl.Bind(null);
        ApplicationState.CurrentState = AppState.Idle;
        WorkspaceStatusMessage = "Returned to Campaign Hub.";
        UpdateStatusBar();
    }

    [RelayCommand]
    private async Task EditCampaignMetadataAsync()
    {
        if (!Session.IsOpen || Session.Metadata == null) return;

        var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (owner == null) return;

        var dialog = new Views.EditCampaignMetadataDialog(Session.Metadata.DisplayName, Session.Metadata.NarrativeFocus);
        var result = await dialog.ShowDialog<bool?>(owner);
        if (result != true) return;

        await Session.UpdateMetadataAsync(dialog.DisplayName, dialog.NarrativeFocus);
        WorkspaceStatusMessage = $"Updated campaign metadata for '{dialog.DisplayName ?? Session.Metadata.CampaignName}'.";
    }

    [RelayCommand]
    private void Exit()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        owner?.Close();
    }

    [RelayCommand]
    public async Task LoadCampaignAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            WorkspaceStatusMessage = $"Opening vault: {path}";
            await Session.OpenAsync(path);
            _historyService.Add(path);
            Hub.LoadRecentCampaigns();

            EnterEditorMode(path);
            Sync.StatusMessage = "Vault open. Fetch to compare with Campaign Vault.";
            RefreshAll();
        }
        catch (VaultException ex)
        {
            WorkspaceStatusMessage = ex.Message;
            ShowStatusBanner = true;
            StatusBannerMessage = ex.Message;
        }
        catch (Exception ex)
        {
            WorkspaceStatusMessage = $"Failed to open vault: {ex.Message}";
            ShowStatusBanner = true;
            StatusBannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void EnterEditorMode(string? vaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(vaultPath))
            WorkspaceStatusMessage = $"Vault: {vaultPath}";

        Sync.ConfigureSessionSync();
        Workspace.BindSession(Session);
        Sync.Bind(Session, RefreshAll);
        SourceControl.Bind(Session, RefreshAll);
        ApplicationState.CurrentState = AppState.Editor;
    }

    public void RefreshAll()
    {
        Workspace.RefreshFilesList();
        SourceControl.RefreshStatus();
        Sync.UpdateSummary();
        UpdateStatusBar();
    }

    public void UpdateStatusBar()
    {
        if (!Session.IsOpen)
        {
            StatusBarGit = "—";
            StatusBarSync = "Vault: not open";
            StatusBarConnection = "offline";
            return;
        }

        var head = Session.HeadCommitSha;
        StatusBarGit = head != null && head.Length > 7 ? head[..7] : head ?? "—";

        var summary = Session.GetSyncSummary();
        StatusBarSync = $"{summary.AheadCount} ahead · {summary.BehindCount} behind · {summary.ConflictCount} conflicts";

        StatusBarConnection = summary.Connection.State switch
        {
            VaultConnectionState.Online => "online",
            VaultConnectionState.Offline => "offline",
            VaultConnectionState.Error => "error",
            _ => "unknown"
        };

        // Only banner for hard errors / corrupt cache. Plain "offline" is valid for local authoring.
        if (summary.RemoteCacheCorrupt || summary.Connection.State == VaultConnectionState.Error)
        {
            ShowStatusBanner = true;
            StatusBannerMessage = summary.RemoteCacheCorrupt
                ? "Remote cache is corrupt. Fetch again from the Vault Sync pane."
                : summary.Connection.Message ?? $"Vault {StatusBarConnection}";
        }
        else
        {
            ShowStatusBanner = false;
            StatusBannerMessage = string.Empty;
        }
    }

    public async Task ReloadActiveFileContentAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode)
            return;

        if (!Session.IsOpen)
            return;

        if (IsEditorDirty)
        {
            WorkspaceStatusMessage = $"{entityNode.Title} changed on disk. Save or discard your edits to reload it.";
            return;
        }

        try
        {
            var content = await Session.ReadFileAsync(entityNode.Entity.RelativePath);
            EditorText = content;
            _lastLoadedEditorText = content;
        }
        catch
        {
            EditorText = string.Empty;
            _lastLoadedEditorText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SaveActiveFileAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode || !Session.IsOpen)
            return;

        if (YamlDiagnostics.Count > 0)
        {
            WorkspaceStatusMessage =
                $"Cannot save: YAML error on line {YamlDiagnostics[0].Line}: {YamlDiagnostics[0].Message}";
            ShowStatusBanner = true;
            StatusBannerMessage = WorkspaceStatusMessage;
            return;
        }

        await Session.WriteFileAsync(entityNode.Entity.RelativePath, EditorText);
        _lastLoadedEditorText = EditorText;
        RefreshAll();
        Sync.StatusMessage = $"Saved {entityNode.Title} at {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private async Task DeleteSelectedEntityAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode || !Session.IsOpen)
            return;

        var rel = entityNode.Entity.RelativePath;
        var title = entityNode.Title;

        var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (owner != null)
        {
            var confirmed = await Views.ConfirmationDialog.ShowAsync(
                owner,
                "Delete Entity",
                $"Delete '{title}'? This removes the local file.\nYou can still recover it via git history if committed.",
                "Delete",
                isDestructive: true);

            if (!confirmed)
                return;
        }

        try
        {
            await Session.DeleteEntityFileAsync(rel);

            // Clear selection and refresh
            Workspace.SelectedNode = null;
            EditorText = string.Empty;
            _lastLoadedEditorText = string.Empty;
            RefreshAll();
            Sync.StatusMessage = $"Deleted {rel}. Commit the change to record it.";
        }
        catch (Exception ex)
        {
            WorkspaceStatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RenameSelectedEntityAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode || !Session.IsOpen)
            return;

        var owner = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (owner == null) return;

        var dialog = new Views.RenameEntityDialog(entityNode.Title);
        var result = await dialog.ShowDialog<bool?>(owner);
        if (result != true || string.IsNullOrWhiteSpace(dialog.NewName))
            return;

        try
        {
            var newRelativePath = await Session.RenameEntityAsync(entityNode.Entity.RelativePath, dialog.NewName);
            RefreshAll();

            var found = FindEntityNodeByPath(Workspace.Categories, newRelativePath);
            if (found != null)
                Workspace.SelectedNode = found;

            WorkspaceStatusMessage = $"Renamed to {newRelativePath}. Commit the change to record it.";
        }
        catch (Exception ex)
        {
            WorkspaceStatusMessage = $"Rename failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateNewEntityAsync(CreateEntityRequest? request)
    {
        if (request == null || !Session.IsOpen)
        {
            if (!Session.IsOpen)
                WorkspaceStatusMessage = "Open a vault before creating entities.";
            return;
        }

        var type = request.EntityType.ToLowerInvariant();

        try
        {
            var (relative, _) = await Session.CreateEntityAsync(type, request.Name, request.TargetFolder);
            RefreshAll();

            var found = FindEntityNodeByPath(Workspace.Categories, relative);
            if (found != null)
                Workspace.SelectedNode = found;

            WorkspaceStatusMessage = $"Created {relative}. Edit and Save (or Commit).";
            Sync.StatusMessage = $"New {type} ready for editing.";
        }
        catch (Exception ex)
        {
            WorkspaceStatusMessage = $"Failed to create: {ex.Message}";
        }
    }

    private static EntityNodeViewModel? FindEntityNodeByPath(System.Collections.Generic.IEnumerable<ExplorerNodeViewModel> nodes, string relativePath)
    {
        foreach (var node in nodes)
        {
            if (node is EntityNodeViewModel en && string.Equals(en.Entity.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return en;
            var nested = FindEntityNodeByPath(node.Children, relativePath);
            if (nested != null) return nested;
        }
        return null;
    }

    [ObservableProperty] private string _formName = string.Empty;

    [ObservableProperty] private int _formCurrentHp;

    [ObservableProperty] private int _formMaxHp;

    [ObservableProperty] private float _formWillpower;

    [ObservableProperty] private float _formStress;

    [ObservableProperty] private string _parseErrorMessage = string.Empty;

    [ObservableProperty] private IReadOnlyList<YamlDiagnostic> _yamlDiagnostics = Array.Empty<YamlDiagnostic>();

    public string PrimaryYamlError => YamlDiagnostics.Count > 0 ? YamlDiagnostics[0].Message : string.Empty;

    public bool HasYamlDiagnostics => YamlDiagnostics.Count > 0;

    private bool _isUpdatingFromForm;

    partial void OnYamlDiagnosticsChanged(IReadOnlyList<YamlDiagnostic> value)
    {
        OnPropertyChanged(nameof(PrimaryYamlError));
        OnPropertyChanged(nameof(HasYamlDiagnostics));
    }

    partial void OnEditorTextChanged(string value)
    {
        if (_isUpdatingFromForm) return;

        YamlDiagnostics = YamlFrontmatterValidator.ValidateDocument(value);

        try
        {
            ParseErrorMessage = YamlDiagnostics.Count > 0 ? YamlDiagnostics[0].Message : string.Empty;

            if (YamlDiagnostics.Count > 0)
            {
                ParsedCharacter = null;
                PreviewNotes = value;
                IsCharacter = false;
                Badge1Label = Badge1Value = Badge2Label = Badge2Value = string.Empty;
                OnPropertyChanged(nameof(HasParseError));
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                ParsedCharacter = null;
                PreviewNotes = string.Empty;
                EntityTypeDisplay = "Unknown";
                IsCharacter = false;
                Badge1Label = Badge1Value = Badge2Label = Badge2Value = string.Empty;
                OnPropertyChanged(nameof(HasParseError));
                return;
            }

            var type = (Workspace.SelectedNode as EntityNodeViewModel)?.Entity.EntityType?.ToLower() ?? "character";

            if (type == "location")
            {
                var loc = _parser.ParseLocation(value);
                ParsedCharacter = new Character { Id = loc.Id, Name = loc.Name, Notes = loc.Description };
                PreviewNotes = loc.Description ?? string.Empty;
                EntityTypeDisplay = "Location";
                IsCharacter = false;
                Badge1Label = "Type:";
                Badge1Value = loc.Type.ToString();
                Badge2Label = "Danger:";
                Badge2Value = loc.DangerModifier.ToString();
            }
            else if (type == "quest")
            {
                var quest = _parser.ParseQuest(value);
                ParsedCharacter = new Character { Id = quest.Id, Name = quest.Title, Notes = quest.DmNotes };
                PreviewNotes = quest.DmNotes ?? string.Empty;
                EntityTypeDisplay = "Quest";
                IsCharacter = false;
                Badge1Label = "State:";
                Badge1Value = quest.OverallState.ToString();
                Badge2Label = "Urgency:";
                Badge2Value = quest.Urgency.ToString();
            }
            else if (type == "faction")
            {
                var f = _parser.ParseFaction(value);
                ParsedCharacter = new Character { Id = f.Id, Name = f.Name, Notes = f.Description };
                PreviewNotes = f.Description ?? string.Empty;
                EntityTypeDisplay = "Faction";
                IsCharacter = false;
                Badge1Label = "Influence:";
                Badge1Value = f.InfluenceLevel.ToString();
                Badge2Label = Badge2Value = string.Empty;
            }
            else if (type == "lore")
            {
                var l = _parser.ParseLore(value);
                ParsedCharacter = new Character { Id = l.Id, Name = l.Title, Notes = l.Content };
                PreviewNotes = l.Content ?? string.Empty;
                EntityTypeDisplay = "Lore";
                IsCharacter = false;
                Badge1Label = "Category:";
                Badge1Value = l.Category ?? "";
                Badge2Label = Badge2Value = string.Empty;
            }
            else if (type == "rumor")
            {
                var r = _parser.ParseRumor(value);
                ParsedCharacter = new Character { Id = r.Id, Name = r.Subject, Notes = r.CurrentText };
                PreviewNotes = r.CurrentText ?? string.Empty;
                EntityTypeDisplay = "Rumor";
                IsCharacter = false;
                Badge1Label = "State:";
                Badge1Value = r.State.ToString();
                Badge2Label = "Truth:";
                Badge2Value = r.TruthValue.ToString();
            }
            else if (type == "event")
            {
                var e = _parser.ParseEvent(value);
                ParsedCharacter = new Character { Id = e.Id, Name = e.Category.ToString(), Notes = e.Summary };
                PreviewNotes = e.Summary ?? string.Empty;
                EntityTypeDisplay = "Event";
                IsCharacter = false;
                Badge1Label = "Day:";
                Badge1Value = e.DayLogged.ToString();
                Badge2Label = Badge2Value = string.Empty;
            }
            else if (type == "item")
            {
                var it = _parser.ParseItem(value);
                ParsedCharacter = new Character { Id = it.Id, Name = it.Name, Notes = it.Description };
                PreviewNotes = it.Description ?? string.Empty;
                EntityTypeDisplay = "Item";
                IsCharacter = false;
                Badge1Label = "Category:";
                Badge1Value = it.CoreCategory.ToString();
                Badge2Label = "Holder:";
                Badge2Value = it.HolderId ?? "";
            }
            else if (type == "customcreature")
            {
                var cc = _parser.ParseCustomCreature(value);
                ParsedCharacter = new Character { Id = cc.Id, Name = cc.Name, Notes = cc.Description };
                PreviewNotes = cc.Description ?? string.Empty;
                EntityTypeDisplay = "Creature";
                IsCharacter = false;
                Badge1Label = "System:";
                Badge1Value = cc.System.ToString();
                Badge2Label = "CR:";
                Badge2Value = cc.ChallengeRating ?? "";
            }
            else if (type == "plotthread")
            {
                var pt = _parser.ParsePlotThread(value);
                ParsedCharacter = new Character { Id = pt.Id, Name = pt.Title, Notes = pt.DmNotes };
                PreviewNotes = pt.DmNotes ?? string.Empty;
                EntityTypeDisplay = "Plot Thread";
                IsCharacter = false;
                Badge1Label = "State:";
                Badge1Value = pt.State.ToString();
                Badge2Label = "Tension:";
                Badge2Value = pt.TensionLevel.ToString();
            }
            else
            {
                // character or unknown -> character form + preview
                var character = _parser.ParseCharacter(value);
                ParsedCharacter = character;
                PreviewNotes = character.Notes ?? string.Empty;
                EntityTypeDisplay = "Character";
                IsCharacter = true;
                Badge1Label = Badge1Value = Badge2Label = Badge2Value = string.Empty;

                _formName = character.Name ?? string.Empty;
                OnPropertyChanged(nameof(FormName));
                _formCurrentHp = character.CurrentHp;
                OnPropertyChanged(nameof(FormCurrentHp));
                _formMaxHp = character.MaxHp;
                OnPropertyChanged(nameof(FormMaxHp));
                if (character.SystemStats != null)
                {
                    _formWillpower = character.SystemStats.Willpower;
                    OnPropertyChanged(nameof(FormWillpower));
                    _formStress = character.SystemStats.Stress;
                    OnPropertyChanged(nameof(FormStress));
                }
            }
        }
        catch (Exception ex)
        {
            ParsedCharacter = null;
            PreviewNotes = value;
            IsCharacter = false;
            Badge1Label = Badge1Value = Badge2Label = Badge2Value = string.Empty;
            ParseErrorMessage = ex.ToFriendlyMessage("Could not parse entity content");
        }
        finally
        {
            OnPropertyChanged(nameof(HasParseError));
        }
    }

    partial void OnParsedCharacterChanged(Character? value) => OnPropertyChanged(nameof(HasParseError));

    partial void OnFormNameChanged(string value) => SyncFormToEditor();
    partial void OnFormCurrentHpChanged(int value) => SyncFormToEditor();
    partial void OnFormMaxHpChanged(int value) => SyncFormToEditor();
    partial void OnFormWillpowerChanged(float value) => SyncFormToEditor();
    partial void OnFormStressChanged(float value) => SyncFormToEditor();

    private void SyncFormToEditor()
    {
        if (ParsedCharacter == null || !IsCharacter) return;
        if (_isUpdatingFromForm) return;

        _isUpdatingFromForm = true;

        ParsedCharacter.Name = FormName;
        ParsedCharacter.CurrentHp = FormCurrentHp;
        ParsedCharacter.MaxHp = FormMaxHp;
        if (ParsedCharacter.SystemStats == null) ParsedCharacter.SystemStats = new SystemExtension();
        ParsedCharacter.SystemStats.Willpower = FormWillpower;
        ParsedCharacter.SystemStats.Stress = FormStress;
        ParsedCharacter.Notes = null;

        var yaml = _yamlSerializer.Serialize(ParsedCharacter);
        EditorText = $"---\n{yaml}---\n\n{PreviewNotes}".ReplaceLineEndings("\n");

        OnPropertyChanged(nameof(ParsedCharacter));
        _isUpdatingFromForm = false;
    }
}