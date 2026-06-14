using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;

namespace CampaignVault.Authoring.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly YamlDotNet.Serialization.ISerializer _yamlSerializer =
        new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();
    public WorkspaceViewModel Workspace { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public GenerationViewModel Generation { get; }
    public SyncViewModel Sync { get; }
    public HubViewModel Hub { get; }
    public CampaignStateService CampaignState { get; }

    [ObservableProperty]
    private AppStateService _applicationState = new();

    public Dock.Model.Core.IFactory Factory { get; }
    public Dock.Model.Controls.IRootDock? Layout { get; }

    private readonly WorkspaceParser _parser = new();
    private readonly CampaignHistoryService _historyService = new();
    private IStorageProvider? _storageProvider;

    [ObservableProperty]
    private string _editorText = string.Empty;

    [ObservableProperty]
    private string _workspaceStatusMessage = "Open a campaign folder to begin.";

    [ObservableProperty]
    private Character? _parsedCharacter;

    [ObservableProperty]
    private string _previewNotes = string.Empty;

    [ObservableProperty]
    private string _entityTypeDisplay = "Character";

    [ObservableProperty]
    private bool _isCharacter = true;

    [ObservableProperty]
    private string _badge1Label = string.Empty;

    [ObservableProperty]
    private string _badge1Value = string.Empty;

    [ObservableProperty]
    private string _badge2Label = string.Empty;

    [ObservableProperty]
    private string _badge2Value = string.Empty;

    public MainWindowViewModel()
    {
        Sync = new SyncViewModel(Settings, Workspace);
        Generation = new GenerationViewModel(Settings);
        Hub = new HubViewModel(this);
        CampaignState = new CampaignStateService(Workspace.DbService);
        CampaignState.SetClientFactory(() => Sync.CreateClient());

        Workspace.SetStateService(CampaignState);

        // Subscribe to selection changes
        Workspace.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedNode) && Workspace.SelectedNode is EntityNodeViewModel entityNode)
            {
                var absolutePath = Path.Combine(Workspace.CurrentDirectory, entityNode.Entity.RelativePath ?? string.Empty);
                LoadFileContent(absolutePath);
                OnEditorTextChanged(EditorText);
            }
        };

        Factory = new AuthoringDockFactory(this);
        Layout = Factory.CreateLayout();
        if (Layout != null)
        {
            Factory.InitLayout(Layout);
        }

#if DEBUG
        // For dev testing, auto-load a dummy path or local path if it exists
        var testPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestCampaign");
        if (!Directory.Exists(testPath)) Directory.CreateDirectory(testPath);
        // LoadCampaign(testPath); // Skip auto-load to show Hub by default
#endif
    }

    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    [RelayCommand]
    private async Task OpenCampaignFolderAsync()
    {
        if (_storageProvider == null)
        {
            WorkspaceStatusMessage = "Folder picker is not available yet. Restart the app.";
            return;
        }

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Campaign Workspace Folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null)
        {
            return;
        }

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            WorkspaceStatusMessage = "Could not resolve the selected folder path.";
            return;
        }

        LoadCampaign(path);
    }

    // Command to open a campaign and switch state
    [RelayCommand]
    public void LoadCampaign(string path)
    {
        _historyService.Add(path);
        Hub.LoadRecentCampaigns();
        Workspace.LoadDirectory(path);
        WorkspaceStatusMessage = $"Workspace: {path}";
        Sync.StatusMessage = "Workspace loaded. Connect gRPC sync to compare with CampaignVault.";
        ApplicationState.CurrentState = CampaignVault.Authoring.Services.AppState.Editor;
    }

    public void ReloadActiveFileContent()
    {
        if (Workspace.SelectedNode is EntityNodeViewModel entityNode)
        {
            var absolutePath = Path.Combine(Workspace.CurrentDirectory, entityNode.Entity.RelativePath ?? string.Empty);
            LoadFileContent(absolutePath);
        }
    }

    private void LoadFileContent(string path)
    {
        if (File.Exists(path))
        {
            EditorText = File.ReadAllText(path);
        }
    }

    [RelayCommand]
    private async Task SaveActiveFileAsync()
    {
        if (Workspace.SelectedNode is EntityNodeViewModel entityNode)
        {
            var absolutePath = Path.Combine(Workspace.CurrentDirectory, entityNode.Entity.RelativePath ?? string.Empty);
            await File.WriteAllTextAsync(absolutePath, EditorText);
            Sync.StatusMessage = $"Saved {entityNode.Title} locally at {DateTime.Now:HH:mm:ss}";
        }
    }

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private int _formCurrentHp;

    [ObservableProperty]
    private int _formMaxHp;

    [ObservableProperty]
    private float _formWillpower;

    [ObservableProperty]
    private float _formStress;

    private bool _isUpdatingFromForm;

    partial void OnEditorTextChanged(string value)
    {
        if (_isUpdatingFromForm) return;

        try
        {
            if (string.IsNullOrEmpty(value))
            {
                ParsedCharacter = null;
                PreviewNotes = string.Empty;
                EntityTypeDisplay = "Unknown";
                IsCharacter = false;
                Badge1Label = string.Empty;
                Badge1Value = string.Empty;
                Badge2Label = string.Empty;
                Badge2Value = string.Empty;
                return;
            }

            var type = (Workspace.SelectedNode as EntityNodeViewModel)?.Entity.EntityType?.ToLower() ?? "character";

            if (type == "location")
            {
                var loc = _parser.ParseLocation(value);
                ParsedCharacter = new Character
                {
                    Id = loc.Id,
                    Name = loc.Name,
                    Notes = loc.Description
                };
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
                ParsedCharacter = new Character
                {
                    Id = quest.Id,
                    Name = quest.Title,
                    Notes = quest.DmNotes
                };
                PreviewNotes = quest.DmNotes ?? string.Empty;
                EntityTypeDisplay = "Quest";
                IsCharacter = false;
                Badge1Label = "State:";
                Badge1Value = quest.OverallState.ToString();
                Badge2Label = "Urgency:";
                Badge2Value = quest.Urgency.ToString();
            }
            else
            {
                var character = _parser.ParseCharacter(value);
                ParsedCharacter = character;
                PreviewNotes = character.Notes ?? string.Empty;
                EntityTypeDisplay = "Character";
                IsCharacter = true;
                Badge1Label = string.Empty;
                Badge1Value = string.Empty;
                Badge2Label = string.Empty;
                Badge2Value = string.Empty;

                // Set backing fields directly (not properties) to avoid triggering
                // SyncFormToEditor() → EditorText change → re-entrant loop.
                // The backing field assignment + manual OnPropertyChanged mirrors what
                // the source-generated setter does but skips the partial OnXxxChanged callback.
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
        catch
        {
            // Fail gracefully while user is typing incomplete frontmatter
            ParsedCharacter = null;
            PreviewNotes = value;
            IsCharacter = false;
            Badge1Label = string.Empty;
            Badge1Value = string.Empty;
            Badge2Label = string.Empty;
            Badge2Value = string.Empty;
        }
    }

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
        ParsedCharacter.Notes = null; // Notes are serialized outside the frontmatter

        var yaml = _yamlSerializer.Serialize(ParsedCharacter);
        EditorText = $"---\n{yaml}---\n\n{PreviewNotes}".ReplaceLineEndings("\n");

        OnPropertyChanged(nameof(ParsedCharacter));
        _isUpdatingFromForm = false;
    }
}
