using Raven.Client.Documents;
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

        var documentStore = EmbeddedServer.Instance.GetDocumentStore("CampaignVault");
        
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
