using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CampaignVault.Authoring.Views;

public partial class ConfirmationDialog : Window
{
    public string DialogTitle { get; set; } = "Confirm Action";
    public string Message { get; set; } = string.Empty;
    public string ConfirmLabel { get; set; } = "Confirm";
    public bool IsDestructive { get; set; } = false;

    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        TitleBlock.Text = DialogTitle;
        MessageBlock.Text = Message;
        ConfirmButton.Content = ConfirmLabel;

        if (IsDestructive)
            ConfirmButton.Background = Avalonia.Media.Brush.Parse("#DC2626");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel = "Confirm", bool isDestructive = true)
    {
        var dialog = new ConfirmationDialog
        {
            DialogTitle = title,
            Message = message,
            ConfirmLabel = confirmLabel,
            IsDestructive = isDestructive
        };

        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
