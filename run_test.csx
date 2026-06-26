using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Authoring.Vault;

var session = new CampaignVaultSession();
var workspace = new WorkspaceViewModel();

var tempDir = Path.Combine(Path.GetTempPath(), "test_campaign_UI_test");
if (Directory.Exists(tempDir))
    Directory.Delete(tempDir, true);

await session.CreateAsync(tempDir, "test-campaign");

var entityPath = Path.Combine(tempDir, "characters", "test.md");
Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
await File.WriteAllTextAsync(entityPath, "---\nid: characters/test\nname: Test\n---\nHello");

workspace.BindSession(session);
workspace.RefreshFilesList();

await Task.Delay(500);

Console.WriteLine("Categories count: " + workspace.Categories.Count);
foreach (var c in workspace.Categories)
{
    Console.WriteLine("Category: " + c.Title + " - Children: " + c.Children.Count);
    foreach (var child in c.Children)
        Console.WriteLine("  - " + child.Title);
}

session.Dispose();
workspace.Dispose();