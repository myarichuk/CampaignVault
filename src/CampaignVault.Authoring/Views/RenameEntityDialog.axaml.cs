using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CampaignVault.Authoring.Views;

public partial class RenameEntityDialog : Window
{
    public string? NewName { get; private set; }

    public RenameEntityDialog()
    {
        InitializeComponent();
    }

    public RenameEntityDialog(string currentName) : this()
    {
        NameTextBox.Text = currentName;
        NameTextBox.SelectAll();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Rename_Click(object? sender, RoutedEventArgs e) => Submit();

    private void NameTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Submit();
    }

    private void Submit()
    {
        var name = NameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        NewName = name;
        Close(true);
    }
}
