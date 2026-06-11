using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CampaignVault.Authoring.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<FileNodeViewModel> _files = new();

    [ObservableProperty]
    private FileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    public void LoadDirectory(string path)
    {
        CurrentDirectory = path;
        Files.Clear();
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.md", SearchOption.AllDirectories))
        {
            Files.Add(new FileNodeViewModel { 
                FilePath = file, 
                FileName = Path.GetFileName(file) 
            });
        }
    }
}

public partial class FileNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;
}
