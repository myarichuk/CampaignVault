using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class CampaignRepository
{
    private readonly IDocumentStore _store;
    private readonly WorldSimulator _simulator = new();

    public CampaignRepository(IDocumentStore store)
    {
        _store = store;
    }

    public IAsyncDocumentSession OpenSession()
    {
        var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        return session;
    }

    public async Task<CommitResult> CommitChangesAsync(IAsyncDocumentSession session, WorldChange[] changes)
    {
        var summary = new List<string>();
        foreach (var change in changes)
        {
            switch (change)
            {
                case HpChange hp:
                    session.Advanced.Increment<Character, int>(hp.CharacterId, x => x.CurrentHp, hp.Delta);
                    summary.Add($"HP adjusted for {hp.CharacterId} by {hp.Delta}");
                    break;

                case ItemTransfer item:
                    var doc = await session.LoadAsync<Item>(item.ItemId);
                    if (doc != null)
                    {
                        doc.HolderId = item.ToHolderId;
                        doc.LastUpdated = DateTime.UtcNow;
                        summary.Add($"Item {item.ItemId} moved to {item.ToHolderId}");
                    }
                    break;

                case StatusChange status:
                    session.Advanced.Patch<Character, string>(status.CharacterId, x => x.Status, x => x.Add(status.Status));
                    summary.Add($"Status '{status.Status}' added to {status.CharacterId}");
                    break;

                case EventOccurred ev:
                    var currentTime = await GetTimeAsync(session);
                    var e = new Event { Id = "events/" + Guid.NewGuid(), Summary = ev.Summary, Type = ev.Type, Involved = ev.Involved ?? [], DayLogged = currentTime.TotalDaysElapsed };
                    await LogEventAsync(session, e);
                    summary.Add($"Event logged: {ev.Summary}");
                    break;

                case RumorEvolves rumor:
                    session.Advanced.Patch<Rumor, RumorState>(rumor.RumorId, x => x.State, rumor.NewState);
                    if (rumor.NewText != null) session.Advanced.Patch<Rumor, string>(rumor.RumorId, x => x.CurrentText, rumor.NewText);
                    var rtime = await GetTimeAsync(session);
                    session.Advanced.Patch<Rumor, int>(rumor.RumorId, x => x.LastStateChangeDay, rtime.TotalDaysElapsed);
                    summary.Add($"Rumor {rumor.RumorId} evolved to {rumor.NewState}");
                    break;

                case RelationshipChange rel:
                    var source = await session.LoadAsync<Character>(rel.SourceId);
                    if (source != null)
                    {
                        source.Mind ??= new NpcMind();
                        source.Mind.Relationships ??= new Dictionary<string, int>();
                        if (source.Mind.Relationships.TryGetValue(rel.TargetId, out var currentVal))
                            source.Mind.Relationships[rel.TargetId] = currentVal + rel.Delta;
                        else
                            source.Mind.Relationships[rel.TargetId] = rel.Delta;
                        summary.Add($"Relationship from {rel.SourceId} to {rel.TargetId} shifted by {rel.Delta} ({rel.Reason})");
                    }
                    break;

                // FIX: Handle NeedChange (LLM can now actually change hunger etc.)
                case NeedChange needChange:
                    var needChar = await session.LoadAsync<Character>(needChange.CharacterId);
                    if (needChar?.Mind != null)
                    {
                        if (!needChar.Mind.Needs.ContainsKey(needChange.Need))
                            needChar.Mind.Needs[needChange.Need] = 0f;
                        needChar.Mind.Needs[needChange.Need] = Math.Clamp(needChar.Mind.Needs[needChange.Need] + needChange.Delta, 0f, 100f);
                        summary.Add($"Need '{needChange.Need}' adjusted for {needChange.CharacterId} by {needChange.Delta}");
                    }
                    break;

                // FIX: Handle AttributeChange (willpower, temperature, morale)
                case AttributeChange attr:
                    var attrChar = await session.LoadAsync<Character>(attr.CharacterId);
                    if (attrChar?.Mind != null)
                    {
                        switch (attr.Attribute.ToLowerInvariant())
                        {
                            case "willpower": attrChar.Mind.Willpower = Math.Clamp(attr.Value, 0f, 100f); break;
                            case "temperature": attrChar.Mind.Temperature = attr.Value; break;
                            case "morale": attrChar.Mind.Morale = Math.Clamp(attr.Value, 0f, 100f); break;
                        }
                        summary.Add($"Attribute '{attr.Attribute}' set for {attr.CharacterId}");
                    }
                    break;

                default:
                    summary.Add($"WARNING: Unhandled change type");
                    break;
            }
        }
        return new CommitResult { ChangesProcessed = changes.Length, Summary = summary };
    }

    // Other methods (GetScene, GetCharacter with simulation logic, etc.) are on the branch or will be added in next commit.
}