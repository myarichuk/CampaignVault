using Raven.Client.Documents.Indexes;
using Raven.Embedded;

namespace CampaignVault.Data;

public static class RavenStartup
{
    public static IDocumentStore Initialize(string dbPath)
    {
        EmbeddedServer.Instance.StartServer(new ServerOptions
        {
            DataDirectory = dbPath,
            ServerUrl = "http://127.0.0.1:0" // Use a random port
        });

        // AdvanceWorld/pressure evaluation are composed of many independent, small per-rule and
        // per-contributor queries (deliberately isolated/pluggable rather than batched into one big
        // query) — the default 30-request session guard is tuned for typical CRUD request handlers,
        // not this fan-out. Raised per RavenDB's own guidance once call-count reduction isn't
        // reasonable without giving up the plugin-style rule/contributor architecture.
        var databaseOptions = new DatabaseOptions("CampaignVault")
        {
            Conventions = new Raven.Client.Documents.Conventions.DocumentConventions { MaxNumberOfRequestsPerSession = 200 },
        };
        var documentStore = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
        
        // Create indexes from assembly
        IndexCreation.CreateIndexes(typeof(RavenStartup).Assembly, documentStore);

        // Universal sanitizing listener on the Raven persistence boundary.
        documentStore.OnBeforeStore += (_, args) =>
        {
            if (args.Entity is not null)
            {
                JsonSanitizer.Sanitize(args.Entity);
            }
        };

        return documentStore;
    }
}
