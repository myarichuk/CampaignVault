using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Shared once per "RavenDB" xUnit collection (the whole test run). Prefers a real RavenDB
/// server via Testcontainers, one database per test class, since a separate server process
/// doesn't compete with the xUnit test host for CPU/mmap/GC the way the embedded server does.
/// Falls back to a single shared embedded database for the whole run when Docker isn't available.
/// </summary>
public sealed class RavenDbTestEnvironment : IAsyncLifetime
{
    private const string RavenDbImage = "ravendb/ravendb:7.2-ubuntu-latest";
    private const int RavenDbPort = 8080;

    private static readonly Dictionary<string, string> CoraxSettings = new()
    {
        { "Indexing.Static.SearchEngineType", "Corax" },
        { "Indexing.Auto.SearchEngineType", "Corax" }
    };

    public bool IsRemote { get; private set; }

    private IContainer? _container;
    private string? _serverUrl;
    private IDocumentStore? _sharedFallbackStore;
    private string? _embeddedDataDir;

    public async Task InitializeAsync()
    {
        CleanupOldTestDirectories();

        if (await TryStartContainerAsync().ConfigureAwait(false))
        {
            IsRemote = true;
            return;
        }

        IsRemote = false;
        InitializeEmbeddedFallback();
    }

    private static void CleanupOldTestDirectories()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var testDirs = Directory.EnumerateDirectories(tempPath, "RavenDBTest*", SearchOption.TopDirectoryOnly);
            foreach (var dir in testDirs)
            {
                try
                {
                    Directory.Delete(dir, true);
                    Console.WriteLine($"[RavenDbTestEnvironment] Cleaned up {Path.GetFileName(dir)}");
                }
                catch
                {
                    // Best effort — skip directories still locked by concurrent or incomplete runs
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RavenDbTestEnvironment] Cleanup sweep failed: {ex.Message}");
        }
    }

    public (IDocumentStore Store, bool IsSharedStore) CreateStoreForClass(string uniqueDatabaseName)
    {
        if (!IsRemote)
        {
            return (_sharedFallbackStore!, true);
        }

        var store = new DocumentStore
        {
            Urls = [_serverUrl!],
            Database = uniqueDatabaseName
        };
        ConfigureConventions(store);
        store.Initialize();

        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(uniqueDatabaseName)
        {
            Settings = CoraxSettings
        }));

        IndexCreation.CreateIndexes(typeof(CampaignRepository).Assembly, store);
        WaitForStaticIndexes(store);

        return (store, false);
    }

    private async Task<bool> TryStartContainerAsync()
    {
        try
        {
            var startTask = StartContainerCoreAsync();
            var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);
            if (completed != startTask)
            {
                Console.Error.WriteLine("[RavenDbTestEnvironment] Timed out waiting for RavenDB container to become ready; falling back to embedded RavenDB.");
                return false;
            }

            return await startTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RavenDbTestEnvironment] Could not start RavenDB Docker container ({ex.GetType().Name}: {ex.Message}); falling back to embedded RavenDB.");
            return false;
        }
    }

    private async Task<bool> StartContainerCoreAsync()
    {
        _container = new ContainerBuilder()
            .WithImage(RavenDbImage)
            .WithPortBinding(RavenDbPort, true)
            .WithEnvironment("RAVEN_Setup_Mode", "None")
            .WithEnvironment("RAVEN_Security_UnsecuredAccessAllowed", "PrivateNetwork")
            .WithEnvironment("RAVEN_License_Eula_Accepted", "true")
            .WithEnvironment("RAVEN_Indexing_Static_SearchEngineType", "Corax")
            .WithEnvironment("RAVEN_Indexing_Auto_SearchEngineType", "Corax")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(RavenDbPort))
            .Build();

        await _container.StartAsync().ConfigureAwait(false);

        var mappedPort = _container.GetMappedPublicPort(RavenDbPort);
        _serverUrl = $"http://localhost:{mappedPort}";

        return await WaitForServerReadyAsync(_serverUrl).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForServerReadyAsync(string url)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probeStore = new DocumentStore { Urls = [url] };
                probeStore.Initialize();
                probeStore.Maintenance.Server.Send(new GetBuildNumberOperation());
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine($"[RavenDbTestEnvironment] RavenDB container did not become ready in time: {lastError}");
        return false;
    }

    private void InitializeEmbeddedFallback()
    {
        _embeddedDataDir = Path.Combine(Path.GetTempPath(), $"RavenDBTest_Shared_{Guid.NewGuid()}");
        Directory.CreateDirectory(_embeddedDataDir);
        File.WriteAllText(Path.Combine(_embeddedDataDir, "settings.json"),
            "{\"Indexing.Static.SearchEngineType\": \"Corax\", \"Indexing.Auto.SearchEngineType\": \"Corax\"}");

        Environment.SetEnvironmentVariable("RAVEN_Indexing_Static_SearchEngineType", "Corax");
        Environment.SetEnvironmentVariable("RAVEN_Indexing_Auto_SearchEngineType", "Corax");
        EmbeddedServer.Instance.StartServer(new ServerOptions
        {
            DataDirectory = _embeddedDataDir,
            ServerUrl = "http://127.0.0.1:0",
            CommandLineArgs = ["--Indexing.Static.SearchEngineType=Corax", "--Indexing.Auto.SearchEngineType=Corax"]
        });

        var store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions(new DatabaseRecord("TestDB_Shared")
        {
            Settings = CoraxSettings
        }));
        ConfigureConventions(store);
        store.Initialize();
        IndexCreation.CreateIndexes(typeof(CampaignRepository).Assembly, store);
        WaitForStaticIndexes(store);

        _sharedFallbackStore = store;
    }

    private static void ConfigureConventions(IDocumentStore store)
    {
        store.OnBeforeStore += (_, args) =>
        {
            if (args.Entity == null)
            {
                return;
            }

            var prop = args.Entity.GetType().GetProperty("CampaignName", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || prop.PropertyType != typeof(string))
            {
                return;
            }

            var val = prop.GetValue(args.Entity) as string;
            if (!string.IsNullOrWhiteSpace(val))
            {
                prop.SetValue(args.Entity, val.Trim().ToLowerInvariant());
            }
        };
    }

    private static void WaitForStaticIndexes(IDocumentStore store, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var stats = store.Maintenance.Send(new GetStatisticsOperation());
            if (stats.Indexes.All(i => !i.IsStale && i.State != IndexState.Error))
            {
                return;
            }

            if (stats.Indexes.Any(i => i.State == IndexState.Error))
            {
                var errors = store.Maintenance.Send(new GetIndexErrorsOperation());
                throw new Exception("Index errors: " + string.Join("; ", errors.SelectMany(e => e.Errors).Select(e => e.Error)));
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Static RavenDB indexes did not become non-stale during test environment startup.");
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            try
            {
                await _container.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await _container.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        if (_sharedFallbackStore != null)
        {
            _sharedFallbackStore.Dispose();

            if (_embeddedDataDir != null)
            {
                DeleteDirectoryWithRetry(_embeddedDataDir, retries: 10, delayMs: 200);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path, int retries, int delayMs)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch when (i < retries - 1)
            {
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RavenDbTestEnvironment] Failed to delete {path}: {ex.Message}");
            }
        }
    }
}
