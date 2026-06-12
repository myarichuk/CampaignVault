using System.IO;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class SettingsServiceTests
{
    [Fact]
    public void LoadSettings_NoFile_ReturnsDefaults()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            var service = new SettingsService(tempFile);
            var settings = service.LoadSettings();

            Assert.NotNull(settings);
            Assert.Equal(8080, settings.McpPort);
            Assert.Equal("None", settings.LlmProvider);
            Assert.Equal(50051, settings.GrpcPort);
            Assert.Equal(5275, settings.VaultMcpPort);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadSettings_PartialJson_AppliesDefaultsForMissingFields()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
                {
                  "McpPort": 8080,
                  "LlmProvider": "None"
                }
                """);

            var service = new SettingsService(tempFile);
            var settings = service.LoadSettings();

            Assert.Equal(8080, settings.McpPort);
            Assert.Equal(50051, settings.GrpcPort);
            Assert.Equal("localhost", settings.GrpcHost);
            Assert.Equal(5275, settings.VaultMcpPort);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SaveAndLoadSettings_SavesCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            var service = new SettingsService(tempFile);

            var originalSettings = new CampaignAuthoringSettings
            {
                McpPort = 9090,
                LlmProvider = "Gemini",
                LlmApiKey = "test-api-key",
                LlmEndpoint = "https://test-endpoint.com",
                LlmModel = "gemini-1.5-pro"
            };

            service.SaveSettings(originalSettings);

            var loadedSettings = service.LoadSettings();
            Assert.NotNull(loadedSettings);
            Assert.Equal(9090, loadedSettings.McpPort);
            Assert.Equal("Gemini", loadedSettings.LlmProvider);
            Assert.Equal("test-api-key", loadedSettings.LlmApiKey);
            Assert.Equal("https://test-endpoint.com", loadedSettings.LlmEndpoint);
            Assert.Equal("gemini-1.5-pro", loadedSettings.LlmModel);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
