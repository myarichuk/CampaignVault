using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CampaignVault.Authoring.Views;

public partial class CreateCampaignDialog : Window
{
    public string? CampaignName { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Ruleset { get; private set; }
    public List<string>? NarrativeFocus { get; private set; }

    public CreateCampaignDialog()
    {
        InitializeComponent();
    }

    private void CampaignNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCreateButtonState();
    }

    private void DisplayNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCreateButtonState();
    }

    private void RulesetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCreateButtonState();
    }

    private void UpdateCreateButtonState()
    {
        var hasName = !string.IsNullOrWhiteSpace(CampaignNameTextBox.Text);
        var hasDisplay = !string.IsNullOrWhiteSpace(DisplayNameTextBox.Text);
        var hasRuleset = RulesetListBox.SelectedIndex >= 0;

        CreateButton!.IsEnabled = hasName && hasDisplay && hasRuleset;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        CampaignName = CampaignNameTextBox.Text?.Trim();
        DisplayName = DisplayNameTextBox.Text?.Trim();

        if (RulesetListBox.SelectedItem is ListBoxItem item)
            Ruleset = item.Tag?.ToString();

        var focusText = NarrativeFocusTextBox.Text ?? string.Empty;
        NarrativeFocus = focusText
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        Close(true);
    }
}
