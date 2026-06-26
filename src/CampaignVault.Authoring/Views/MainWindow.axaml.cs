using Avalonia.Controls;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetStorageProvider(StorageProvider);
        }
    }
}