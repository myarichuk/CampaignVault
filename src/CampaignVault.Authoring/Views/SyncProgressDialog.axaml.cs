using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CampaignVault.Authoring.Views;

public partial class SyncProgressDialog : Window
{
    public SyncProgressDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void UpdateProgress(double progress, string message)
    {
        var progressBar = this.FindControl<ProgressBar>("SyncProgressBar");
        var statusText = this.FindControl<TextBlock>("StatusText");

        if (progressBar != null) progressBar.Value = progress;
        if (statusText != null) statusText.Text = message;
    }
}
