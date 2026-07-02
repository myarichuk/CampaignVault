using System;
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
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasParseError));

                if (Workspace.SelectedNode is EntityNodeViewModel entityNode)
                {
                    try
                    {
                        var content = await Session.ReadFileAsync(entityNode.Entity.RelativePath);
                        EditorText = content;
                        OnEditorTextChanged(EditorText);
                    }
                    catch
                    {
                        EditorText = string.Empty;
                    }
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
        var path = await PickFolderAsync();
        if (path != null)
            await LoadCampaignAsync(path);
    }

    [RelayCommand]
    private async Task BackToHub()
    {
        await Session.CloseAsync();
        Workspace.BindSession(null);
        Sync.Bind(null);
        SourceControl.Bind(null);
        ApplicationState.CurrentState = AppState.Idle;
        WorkspaceStatusMessage = "Returned to Campaign Hub.";
        UpdateStatusBar();
    }

    [RelayCommand]
    public async Task LoadCampaignAsync(string path)
    {
        WorkspaceStatusMessage = $"Opening vault: {path}";
        try
        {
            await Session.OpenAsync(path);
            _historyService.Add(path);
            Hub.LoadRecentCampaigns();

            Sync.ConfigureSessionSync();
            Workspace.BindSession(Session);
            Sync.Bind(Session, RefreshAll);
            SourceControl.Bind(Session, RefreshAll);

            ApplicationState.CurrentState = AppState.Editor;
            WorkspaceStatusMessage = $"Vault: {path}";
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

    public void ReloadActiveFileContent()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode)
            return;

        if (!Session.IsOpen)
            return;

        try
        {
            EditorText = Session.ReadFileAsync(entityNode.Entity.RelativePath).GetAwaiter().GetResult();
        }
        catch
        {
            EditorText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SaveActiveFileAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode || !Session.IsOpen)
            return;

        await Session.WriteFileAsync(entityNode.Entity.RelativePath, EditorText);
        RefreshAll();
        Sync.StatusMessage = $"Saved {entityNode.Title} at {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private async Task DeleteSelectedEntityAsync()
    {
        if (Workspace.SelectedNode is not EntityNodeViewModel entityNode || !Session.IsOpen)
            return;

        var rel = entityNode.Entity.RelativePath;
        try
        {
            await Session.DeleteEntityFileAsync(rel);

            // Clear selection and refresh
            Workspace.SelectedNode = null;
            EditorText = string.Empty;
            RefreshAll();
            Sync.StatusMessage = $"Deleted {rel}. Commit the change to record it.";
        }
        catch (Exception ex)
        {
            WorkspaceStatusMessage = $"Delete failed: {ex.Message}";
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
        if (!EntityCreation.IsSupportedEntityType(type))
            type = "character";

        try
        {
            var (relative, _) = await Session.CreateEntityAsync(type, request.Name);
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

    private bool _isUpdatingFromForm;

    partial void OnEditorTextChanged(string value)
    {
        if (_isUpdatingFromForm) return;

        try
        {
            ParseErrorMessage = string.Empty;

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
            ParseErrorMessage = ex.ToString();
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