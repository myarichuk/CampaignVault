using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.ViewModels;

var settings = new SettingsViewModel();
var workspace = new WorkspaceViewModel();
var state = new CampaignVault.Authoring.Services.CampaignStateService(workspace.DbService);
workspace.SetStateService(state);

var tempDir = Path.Combine(Path.GetTempPath(), "test_campaign_UI_test");
Directory.CreateDirectory(tempDir);
Directory.CreateDirectory(Path.Combine(tempDir, "characters"));
File.WriteAllText(Path.Combine(tempDir, "characters", "test.md"), "---\n$type: character\nid: characters/test\nname: Test\n---\nHello");

workspace.LoadDirectory(tempDir);

Task.Delay(2000).Wait();

Console.WriteLine("Categories count: " + workspace.Categories.Count);
foreach (var c in workspace.Categories) {
    Console.WriteLine("Category: " + c.Title + " - Children: " + c.Children.Count);
    foreach (var child in c.Children) {
        Console.WriteLine("  - " + child.Title);
    }
}
