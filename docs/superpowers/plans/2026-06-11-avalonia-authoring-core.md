# CampaignVault Authoring App - Phase 1 (Local Workspace Editor) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the foundational Avalonia Desktop App that can read, parse, and edit local Campaign Markdown/YAML files, producing a standalone local workspace editor.

**Architecture:** A new Avalonia project (`CampaignVault.Authoring`) added to the solution, featuring a `WorkspaceParser` that splits YAML frontmatter from Markdown body, mapping to the shared C# `Character` model. The UI uses MVVM to bind a sidebar directory tree and a side-by-side text editor.

**Tech Stack:** C#, Avalonia UI v11, CommunityToolkit.Mvvm, YamlDotNet, NUnit

---

### Task 1: Scaffold Avalonia Project & Add Dependencies

**Files:**
- Create: `src/CampaignVault.Authoring/CampaignVault.Authoring.csproj`
- Modify: `CampaignVault.slnx`

- [x] **Step 1: Create the Avalonia project via dotnet CLI**

```powershell
dotnet new avalonia.mvvm -n CampaignVault.Authoring -o src/CampaignVault.Authoring
```

- [x] **Step 2: Add project references to the new project**

```powershell
dotnet add src/CampaignVault.Authoring/CampaignVault.Authoring.csproj reference src/CampaignVault/CampaignVault.csproj
```

- [x] **Step 3: Add NuGet packages for parsing**

```powershell
dotnet add src/CampaignVault.Authoring/CampaignVault.Authoring.csproj package YamlDotNet
```

- [x] **Step 4: Add the new project to the SLNX solution file**
*(Note: As `dotnet sln` does not natively support `.slnx` files well in all SDKs, we will manually append it or use the standard XML snippet).*

Modify `CampaignVault.slnx` to include the project under the "src" folder:
```xml
  <Folder Name="/src/">
    <Project Path="src\CampaignVault\CampaignVault.csproj" />
    <Project Path="src\CampaignVault.Authoring\CampaignVault.Authoring.csproj" />
  </Folder>
```

- [x] **Step 5: Verify build passes**

Run: `dotnet build src/CampaignVault.Authoring/CampaignVault.Authoring.csproj`
Expected: PASS

- [x] **Step 6: Commit**

```powershell
git add src/CampaignVault.Authoring CampaignVault.slnx
git commit -m "chore: scaffold CampaignVault.Authoring Avalonia project with dependencies"
```

---

### Task 2: Implement `WorkspaceParser` Core Logic

**Files:**
- Create: `src/CampaignVault.Authoring/Services/WorkspaceParser.cs`
- Create: `tests/CampaignVault.Tests/Authoring/WorkspaceParserTests.cs`

- [x] **Step 1: Write the failing test for parsing a Character markdown file**

Create `tests/CampaignVault.Tests/Authoring/WorkspaceParserTests.cs`:
```csharp
using NUnit.Framework;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;

namespace CampaignVault.Tests.Authoring;

[TestFixture]
public class WorkspaceParserTests
{
    [Test]
    public void ParseCharacter_ValidMarkdown_ExtractsYamlAndBody()
    {
        var markdown = @"---
$type: ""character""
id: ""test_char""
name: ""Test Character""
currentHp: 10
maxHp: 20
---
# Test Body
This is a note.";

        var parser = new WorkspaceParser();
        var character = parser.ParseCharacter(markdown);

        Assert.That(character, Is.Not.Null);
        Assert.That(character.Id, Is.EqualTo("test_char"));
        Assert.That(character.Name, Is.EqualTo("Test Character"));
        Assert.That(character.CurrentHp, Is.EqualTo(10));
        Assert.That(character.MaxHp, Is.EqualTo(20));
        Assert.That(character.Notes, Does.Contain("This is a note."));
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CampaignVault.Tests --filter WorkspaceParserTests`
Expected: FAIL (Does not compile because WorkspaceParser doesn't exist).

- [x] **Step 3: Implement `WorkspaceParser` minimal logic**

Create `src/CampaignVault.Authoring/Services/WorkspaceParser.cs`:
```csharp
using System.Text.RegularExpressions;
using CampaignVault.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Authoring.Services;

public class WorkspaceParser
{
    private readonly IDeserializer _yamlDeserializer;

    public WorkspaceParser()
    {
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public Character ParseCharacter(string fileContent)
    {
        var match = Regex.Match(fileContent, @"^---\s*(.*?)\s*---\s*(.*)", RegexOptions.Singleline);
        if (!match.Success)
            throw new ArgumentException("Invalid frontmatter format.");

        var yamlBlock = match.Groups[1].Value;
        var markdownBody = match.Groups[2].Value.Trim();

        var character = _yamlDeserializer.Deserialize<Character>(yamlBlock);
        character.Notes = markdownBody;

        return character;
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CampaignVault.Tests --filter WorkspaceParserTests`
Expected: PASS

- [x] **Step 5: Commit**

```powershell
git add src/CampaignVault.Authoring/Services/WorkspaceParser.cs tests/CampaignVault.Tests/Authoring/WorkspaceParserTests.cs
git commit -m "feat: add WorkspaceParser for YAML frontmatter to Character mapping"
```

---

### Task 3: Implement `WorkspaceViewModel` for File Tree

**Files:**
- Create: `src/CampaignVault.Authoring/ViewModels/WorkspaceViewModel.cs`

- [x] **Step 1: Write the ViewModel implementation**

Create `src/CampaignVault.Authoring/ViewModels/WorkspaceViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<FileNodeViewModel> _files = new();

    [ObservableProperty]
    private FileNodeViewModel? _selectedFile;

    public void LoadDirectory(string path)
    {
        Files.Clear();
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*.md", SearchOption.AllDirectories))
        {
            Files.Add(new FileNodeViewModel { 
                FilePath = file, 
                FileName = Path.GetFileName(file) 
            });
        }
    }
}

public partial class FileNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;
}
```

- [x] **Step 2: Commit**

```powershell
git add src/CampaignVault.Authoring/ViewModels/WorkspaceViewModel.cs
git commit -m "feat: add WorkspaceViewModel for file tree binding"
```

---

### Task 4: UI Assembly (`MainWindow.axaml` and `MainWindowViewModel`)

**Files:**
- Modify: `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CampaignVault.Authoring/Views/MainWindow.axaml`

- [x] **Step 1: Wire up the ViewModels**

Modify `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System;

namespace CampaignVault.Authoring.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public WorkspaceViewModel Workspace { get; } = new();

    [ObservableProperty]
    private string _editorText = string.Empty;

    public MainWindowViewModel()
    {
        // Subscribe to selection changes
        Workspace.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedFile) && Workspace.SelectedFile != null)
            {
                LoadFileContent(Workspace.SelectedFile.FilePath);
            }
        };

        // For dev testing, auto-load a dummy path or local path if it exists
        var testPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestCampaign");
        if (!Directory.Exists(testPath)) Directory.CreateDirectory(testPath);
        Workspace.LoadDirectory(testPath);
    }

    private void LoadFileContent(string path)
    {
        if (File.Exists(path))
        {
            EditorText = File.ReadAllText(path);
        }
    }
}
```

- [x] **Step 2: Define the UI Layout**

Modify `src/CampaignVault.Authoring/Views/MainWindow.axaml` to replace the default content with a grid:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:CampaignVault.Authoring.ViewModels"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d" d:DesignWidth="800" d:DesignHeight="450"
        x:Class="CampaignVault.Authoring.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Icon="/Assets/avalonia-logo.ico"
        Title="CampaignVault Authoring App">

    <Grid ColumnDefinitions="250, *">
        <!-- Sidebar -->
        <Border Grid.Column="0" BorderBrush="Gray" BorderThickness="0,0,1,0" Padding="10">
            <ListBox ItemsSource="{Binding Workspace.Files}" 
                     SelectedItem="{Binding Workspace.SelectedFile}">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding FileName}" />
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </Border>

        <!-- Editor -->
        <TextBox Grid.Column="1" 
                 Text="{Binding EditorText, Mode=TwoWay}"
                 AcceptsReturn="True"
                 TextWrapping="Wrap"
                 FontFamily="Consolas"
                 Padding="20" />
    </Grid>

</Window>
```

- [x] **Step 3: Verify the build passes**

Run: `dotnet build src/CampaignVault.Authoring/CampaignVault.Authoring.csproj`
Expected: PASS

- [x] **Step 4: Commit**

```powershell
git add src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs src/CampaignVault.Authoring/Views/MainWindow.axaml
git commit -m "feat: assemble main window layout with sidebar and text editor"
```
