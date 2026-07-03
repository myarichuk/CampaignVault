using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Views;

public partial class EditorView : UserControl
{
    private bool _isUpdatingText;
    private MainWindowViewModel? _subscribedViewModel;
    private AvaloniaEdit.TextEditor? _editor;
    private YamlErrorLineHighlighter? _yamlHighlighter;

    public EditorView()
    {
        InitializeComponent();

        _editor = this.FindControl<AvaloniaEdit.TextEditor>("Editor");
        if (_editor != null)
        {
            _yamlHighlighter = new YamlErrorLineHighlighter();
            _editor.TextArea.TextView.LineTransformers.Add(_yamlHighlighter);
            _editor.TextChanged += OnEditorTextChanged;
            _editor.TextArea.PointerMoved += OnEditorPointerMoved;
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
            UpdateYamlHighlights(viewModel);
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            if (_editor != null && _editor.Text != viewModel.EditorText)
            {
                _isUpdatingText = true;
                _editor.Text = viewModel.EditorText ?? string.Empty;
                _isUpdatingText = false;
            }

            UpdateYamlHighlights(viewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.EditorText))
        {
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

        if (e.PropertyName is nameof(MainWindowViewModel.YamlDiagnostics)
            or nameof(MainWindowViewModel.EditorText)
            && _subscribedViewModel != null)
        {
            UpdateYamlHighlights(_subscribedViewModel);
        }
    }

    private void UpdateYamlHighlights(MainWindowViewModel viewModel)
    {
        if (_yamlHighlighter == null || _editor == null) return;

        _yamlHighlighter.SetDiagnostics(viewModel.YamlDiagnostics);
        _editor.TextArea.TextView.Redraw();
        UpdateYamlTooltip(viewModel, _editor.TextArea.Caret.Line);
    }

    private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_editor == null || _subscribedViewModel == null) return;

        var position = e.GetPosition(_editor.TextArea.TextView);
        var visualLine = _editor.TextArea.TextView.GetVisualLineFromVisualTop(position.Y);
        if (visualLine == null)
        {
            ToolTip.SetTip(_editor, null);
            return;
        }

        UpdateYamlTooltip(_subscribedViewModel, visualLine.FirstDocumentLine.LineNumber);
    }

    private void UpdateYamlTooltip(MainWindowViewModel viewModel, int lineNumber)
    {
        if (_editor == null) return;

        var diagnostic = viewModel.YamlDiagnostics.FirstOrDefault(d => d.Line == lineNumber);
        ToolTip.SetTip(_editor, diagnostic != null ? $"Line {diagnostic.Line}: {diagnostic.Message}" : null);
    }
}