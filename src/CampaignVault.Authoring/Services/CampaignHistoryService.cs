using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CampaignVault.Authoring.Services;

public class CampaignHistory
{
    public List<string> RecentPaths { get; set; } = new();
}

public class CampaignHistoryService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
        "CampaignVault", 
        "history.json");

    public CampaignHistory Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<CampaignHistory>(json) ?? new CampaignHistory();
            }
        }
        catch
        {
            // Fallback for corrupted or inaccessible file
        }
        return new CampaignHistory();
    }

    public void Add(string path)
    {
        try
        {
            var history = Load();
            
            // Remove if exists to move to top
            history.RecentPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            
            // Insert at top
            history.RecentPaths.Insert(0, path);
            
            // Keep only top 10
            if (history.RecentPaths.Count > 10)
            {
                history.RecentPaths = history.RecentPaths.Take(10).ToList();
            }

            Save(history);
        }
        catch
        {
            // Silently fail if history cannot be saved
        }
    }

    public void Remove(string path)
    {
        try
        {
            var history = Load();
            history.RecentPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            Save(history);
        }
        catch
        {
            // Silently fail if history cannot be saved
        }
    }

    private void Save(CampaignHistory history)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(history);
        File.WriteAllText(_path, json);
    }
}
