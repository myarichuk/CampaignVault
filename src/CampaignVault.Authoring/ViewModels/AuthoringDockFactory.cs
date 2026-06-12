using System;
using System.Collections.Generic;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Avalonia.Controls;

namespace CampaignVault.Authoring.ViewModels;

public class ToolViewModelWrapper : Tool
{
    public object ViewModel { get; }

    public ToolViewModelWrapper(string id, string title, object viewModel)
    {
        Id = id;
        Title = title;
        ViewModel = viewModel;
    }
}

public class DocumentViewModelWrapper : Document
{
    public object ViewModel { get; }

    public DocumentViewModelWrapper(string id, string title, object viewModel)
    {
        Id = id;
        Title = title;
        ViewModel = viewModel;
    }
}

public class AuthoringDockFactory : Factory
{
    private readonly MainWindowViewModel _context;

    public AuthoringDockFactory(MainWindowViewModel context)
    {
        _context = context;
    }

    public override IRootDock CreateLayout()
    {
        var explorerTool = new ToolViewModelWrapper("Explorer", "Campaign Explorer", _context.Workspace);
        var generatorTool = new ToolViewModelWrapper("Generator", "AI Generator", _context.Generation);
        var syncTool = new ToolViewModelWrapper("Sync", "Sync Diffs", _context.Sync);
        var settingsTool = new ToolViewModelWrapper("Settings", "Settings", _context.Settings);
        
        var editorDocument = new DocumentViewModelWrapper("Editor", "Workspace Editor", _context);

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = "DocumentsPane",
            Proportion = double.NaN,
            ActiveDockable = editorDocument,
            VisibleDockables = CreateList<IDockable>(editorDocument)
        };

        var leftDock = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            Proportion = 0.25,
            ActiveDockable = explorerTool,
            VisibleDockables = CreateList<IDockable>(explorerTool)
        };

        var rightDock = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.25,
            ActiveDockable = generatorTool,
            VisibleDockables = CreateList<IDockable>(generatorTool, settingsTool)
        };

        var bottomDock = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            Proportion = 0.25,
            ActiveDockable = syncTool,
            VisibleDockables = CreateList<IDockable>(syncTool)
        };

        var layout = new RootDock
        {
            Id = "Root",
            Title = "Root",
            ActiveDockable = documentDock,
            DefaultDockable = documentDock,
            VisibleDockables = CreateList<IDockable>(
                new ProportionalDock
                {
                    Orientation = Orientation.Horizontal,
                    VisibleDockables = CreateList<IDockable>(
                        leftDock,
                        new ProportionalDockSplitter(),
                        new ProportionalDock
                        {
                            Orientation = Orientation.Vertical,
                            VisibleDockables = CreateList<IDockable>(
                                documentDock,
                                new ProportionalDockSplitter(),
                                bottomDock
                            )
                        },
                        new ProportionalDockSplitter(),
                        rightDock
                    )
                }
            )
        };

        return layout;
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

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }
}
