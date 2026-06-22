using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace CampaignVault.Authoring.ViewModels;

public class ToolViewModelWrapper : Tool
{
    public object ViewModel { get; }

    public ToolViewModelWrapper(string id, string title, object viewModel, bool canClose = true)
    {
        Id = id;
        Title = title;
        ViewModel = viewModel;
        CanClose = canClose;
        CanPin = true;
    }
}

public class DocumentViewModelWrapper : Document
{
    public object ViewModel { get; }

    public DocumentViewModelWrapper(string id, string title, object viewModel, bool canClose = false)
    {
        Id = id;
        Title = title;
        ViewModel = viewModel;
        CanClose = canClose;
    }
}

public class AuthoringDockFactory : Factory
{
    private readonly MainWindowViewModel _context;
    private IRootDock? _rootDock;
    private IDocumentDock? _documentDock;
    private IDockable? _explorerTool;

    public AuthoringDockFactory(MainWindowViewModel context)
    {
        _context = context;
    }

    public override IRootDock CreateLayout()
    {
        var explorerTool =
            new ToolViewModelWrapper("Explorer", "Campaign Explorer", _context.Workspace, canClose: false);
        var generatorTool = new ToolViewModelWrapper("Generator", "AI Generator", _context.Generation);
        var syncTool = new ToolViewModelWrapper("Sync", "Sync Diffs", _context.Sync);
        var settingsTool = new ToolViewModelWrapper("Settings", "Settings", _context.Settings);
        var editorDocument = new DocumentViewModelWrapper("Editor", "Workspace Editor", _context, canClose: false);

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = "Documents",
            Proportion = double.NaN,
            IsCollapsable = false,
            CanCloseLastDockable = false,
            ActiveDockable = editorDocument,
            VisibleDockables = CreateList<IDockable>(editorDocument)
        };

        var leftDock = new ToolDock
        {
            Id = "LeftPane",
            Title = "Explorer",
            Proportion = 0.22,
            Alignment = Alignment.Left,
            GripMode = GripMode.Visible,
            ActiveDockable = explorerTool,
            VisibleDockables = CreateList<IDockable>(explorerTool)
        };

        var rightDock = new ToolDock
        {
            Id = "RightPane",
            Title = "Tools",
            Proportion = 0.22,
            Alignment = Alignment.Right,
            GripMode = GripMode.Visible,
            ActiveDockable = generatorTool,
            VisibleDockables = CreateList<IDockable>(generatorTool, settingsTool)
        };

        var bottomDock = new ToolDock
        {
            Id = "BottomPane",
            Title = "Sync",
            Proportion = 0.25,
            Alignment = Alignment.Bottom,
            GripMode = GripMode.Visible,
            ActiveDockable = syncTool,
            VisibleDockables = CreateList<IDockable>(syncTool)
        };

        var centerDock = new ProportionalDock
        {
            Id = "CenterPane",
            Orientation = Orientation.Vertical,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                bottomDock
            )
        };

        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(
                leftDock,
                new ProportionalDockSplitter(),
                centerDock,
                new ProportionalDockSplitter(),
                rightDock
            )
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        _rootDock = rootDock;
        _documentDock = documentDock;
        _explorerTool = explorerTool;

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Explorer"] = () => _context.Workspace,
            ["Generator"] = () => _context.Generation,
            ["Sync"] = () => _context.Sync,
            ["Settings"] = () => _context.Settings,
            ["Editor"] = () => _context
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            ["Root"] = () => _rootDock,
            ["DocumentsPane"] = () => _documentDock,
            ["Explorer"] = () => _explorerTool
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    public override void CloseDockable(IDockable dockable)
    {
        if (dockable is ToolViewModelWrapper { CanClose: false } or DocumentViewModelWrapper { CanClose: false })
            return;

        base.CloseDockable(dockable);
    }
}