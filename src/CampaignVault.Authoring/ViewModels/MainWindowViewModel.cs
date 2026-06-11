using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System;

namespace CampaignVault.Authoring.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public WorkspaceViewModel Workspace { get; } = new();

    [ObservableProperty]
    private string _editorText = string.Empty;

    public MainWindowViewModel()
    {
        // Subscribe to selection changes
        Workspace.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedFile) && Workspace.SelectedFile != null)
            {
                LoadFileContent(Workspace.SelectedFile.FilePath);
            }
        };

        // For dev testing, auto-load a dummy path or local path if it exists
        var testPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestCampaign");
        if (!Directory.Exists(testPath)) Directory.CreateDirectory(testPath);
        Workspace.LoadDirectory(testPath);
    }

    private void LoadFileContent(string path)
    {
        if (File.Exists(path))
        {
            EditorText = File.ReadAllText(path);
        }
    }
}
