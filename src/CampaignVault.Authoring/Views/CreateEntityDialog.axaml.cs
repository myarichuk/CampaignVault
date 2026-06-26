using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CampaignVault.Authoring.Views;

public partial class CreateEntityDialog : Window
{
    public string? SelectedEntityType { get; private set; }

    public CreateEntityDialog()
    {
        InitializeComponent();
        EntityTypeComboBox.SelectedIndex = 0;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (EntityTypeComboBox.SelectedItem is ComboBoxItem item)
        {
            SelectedEntityType = item.Content?.ToString()?.ToLowerInvariant();
        }
        Close(SelectedEntityType);
    }
}
