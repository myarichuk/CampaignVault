using System.ComponentModel;
using Avalonia.Controls;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Views;

public partial class EditorView : UserControl
{
    private bool _isUpdatingText;
    private MainWindowViewModel? _subscribedViewModel;
    private AvaloniaEdit.TextEditor? _editor;

    public EditorView()
    {
        InitializeComponent();

        _editor = this.FindControl<AvaloniaEdit.TextEditor>("Editor");
        if (_editor != null)
        {
            _editor.TextChanged += OnEditorTextChanged;
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void OnEditorTextChanged(object? sender, System.EventArgs e)
    {
        if (_isUpdatingText || _editor == null) return;
        if (DataContext is MainWindowViewModel viewModel)
        {
            _isUpdatingText = true;
            viewModel.EditorText = _editor.Text;
            _isUpdatingText = false;
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Unsubscribe from previous ViewModel to prevent memory leak
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Sync editor with current VM text on first bind
            if (_editor != null && _editor.Text != viewModel.EditorText)
            {
                _isUpdatingText = true;
                _editor.Text = viewModel.EditorText ?? string.Empty;
                _isUpdatingText = false;
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.EditorText)) return;
        if (_isUpdatingText || _editor == null) return;

        var viewModel = _subscribedViewModel;
        if (viewModel == null) return;

        if (_editor.Text != viewModel.EditorText)
        {
            _isUpdatingText = true;
            _editor.Text = viewModel.EditorText ?? string.Empty;
            _isUpdatingText = false;
        }
    }
}