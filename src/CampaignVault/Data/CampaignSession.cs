using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public class CampaignSession
{
    public IAsyncDocumentSession Session { get; }
    public string EffectiveCampaign { get; }

    public CampaignSession(IAsyncDocumentSession session, string effectiveCampaign)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        EffectiveCampaign = effectiveCampaign ?? throw new ArgumentNullException(nameof(effectiveCampaign));
    }
}
