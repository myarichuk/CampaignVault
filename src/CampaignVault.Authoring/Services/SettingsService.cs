using System;
using System.IO;
using System.Text.Json;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Authoring.Services;

public class SettingsService
{
    private readonly string _filePath;

    public SettingsService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "campaign_authoring_settings.json");
    }

    public CampaignAuthoringSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<CampaignAuthoringSettings>(json);
                return settings ?? new CampaignAuthoringSettings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load settings from {_filePath}: {ex.Message}");
            // Fallback to defaults on error
        }
        return new CampaignAuthoringSettings();
    }

    public void SaveSettings(CampaignAuthoringSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save settings to {_filePath}", ex);
        }
    }
}
