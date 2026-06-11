using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CampaignVault.Authoring.Services;

namespace CampaignVault.Authoring.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private ObservableCollection<FileNodeViewModel> _files = new();

    [ObservableProperty]
    private FileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    private FileSystemWatcher? _watcher;

    public void LoadDirectory(string path)
    {
        CurrentDirectory = path;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        RefreshFilesList();

        if (Directory.Exists(path))
        {
            _watcher = new FileSystemWatcher(path, "*.md")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnWorkspaceChanged;
            _watcher.Deleted += OnWorkspaceChanged;
            _watcher.Changed += OnWorkspaceChanged;
            _watcher.Renamed += OnWorkspaceChanged;
        }
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        // Must run on Avalonia UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var selectedPath = SelectedFile?.FilePath;

            RefreshFilesList();

            if (!string.IsNullOrEmpty(selectedPath))
            {
                var found = Files.FirstOrDefault(f => f.FilePath == selectedPath);
                if (found != null)
                {
                    SelectedFile = found;
                    WorkspaceService.MainWindowViewModel?.ReloadActiveFileContent();
                }
                else
                {
                    SelectedFile = null;
                }
            }
        });
    }

    private void RefreshFilesList()
    {
        Files.Clear();
        if (!Directory.Exists(CurrentDirectory)) return;

        foreach (var file in Directory.GetFiles(CurrentDirectory, "*.md", SearchOption.AllDirectories))
        {
            Files.Add(new FileNodeViewModel { 
                FilePath = file, 
                FileName = Path.GetFileName(file) 
            });
        }
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
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
