using Avalonia.Controls;

namespace CampaignVault.Authoring.Views;

public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ViewModels.WorkspaceViewModel vm)
        {
            vm.RequestEntityCreationAsync = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is Window window)
                {
                    var dialog = new CreateEntityDialog();
                    return await dialog.ShowDialog<ViewModels.CreateEntityRequest?>(window);
                }
                return null;
            };
        }
    }
}
