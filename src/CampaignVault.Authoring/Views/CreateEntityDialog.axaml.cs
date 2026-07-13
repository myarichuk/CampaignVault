using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Views;

public partial class CreateEntityDialog : Window
{
    // Name the user auto-filled so we can replace it when they switch type
    private string? _autoFilledName;

    public CreateEntityDialog()
    {
        InitializeComponent();
    }

    private void TypeListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var type = SelectedType();
        CreateButton.IsEnabled = type != null;

        if (type == null) return;

        var defaultName = GetDefaultName(type);
        if (string.IsNullOrEmpty(NameTextBox.Text) || NameTextBox.Text == _autoFilledName)
        {
            NameTextBox.Text = defaultName;
            _autoFilledName = defaultName;
        }
    }

    private void NameTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CreateButton.IsEnabled)
            Submit();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Create_Click(object? sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        var type = SelectedType();
        if (type == null) return;

        var name = string.IsNullOrWhiteSpace(NameTextBox.Text)
            ? GetDefaultName(type)
            : NameTextBox.Text.Trim();

        Close(new CreateEntityRequest(type, name));
    }

    private string? SelectedType() =>
        (TypeListBox.SelectedItem as ListBoxItem)?.Tag?.ToString();

    private static string GetDefaultName(string type) => type switch
    {
        "customcreature" => "New Creature",
        "plotthread" => "New Plot Thread",
        "" => "New Entity",
        _ => $"New {char.ToUpper(type[0])}{type[1..]}"
    };
}
