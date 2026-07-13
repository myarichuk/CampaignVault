using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CampaignVault.Authoring.Views;

public partial class EditCampaignMetadataDialog : Window
{
    public string? DisplayName { get; private set; }
    public List<string> NarrativeFocus { get; private set; } = [];

    public EditCampaignMetadataDialog()
    {
        InitializeComponent();
    }

    public EditCampaignMetadataDialog(string? displayName, IEnumerable<string>? narrativeFocus) : this()
    {
        DisplayNameTextBox.Text = displayName;
        NarrativeFocusTextBox.Text = narrativeFocus != null ? string.Join(", ", narrativeFocus) : string.Empty;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        DisplayName = DisplayNameTextBox.Text?.Trim();

        var focusText = NarrativeFocusTextBox.Text ?? string.Empty;
        NarrativeFocus = focusText
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        Close(true);
    }
}
