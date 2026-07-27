using Raven.Client.Documents;
using Raven.Client.Documents.Operations;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Strips stale "$type" discriminators that reference compiler-generated anonymous types
/// (&lt;&gt;f__AnonymousTypeN) out of Event.Details.
///
/// BACKGROUND: Event.Details is a persisted Dictionary&lt;string, object&gt;. Values that used to be
/// boxed anonymous types (see MutationTools.ExtractEventDetails, now fixed to use named records)
/// were tagged by Newtonsoft with a "$type" referencing the anonymous type's compiler-generated
/// name. That name's numeric suffix is not stable across rebuilds, so a document written by one
/// build can reference an anonymous type index that no longer exists (or means something else) in
/// a later build, and typed reads of the Event collection (e.g. session.Query&lt;Event&gt;()) throw.
///
/// This must run before any typed query against the Event collection, at the raw-JSON level via a
/// server-side patch, since loading the documents as typed entities is exactly what fails.
/// Idempotent: documents without the anonymous-type marker are left untouched.
/// </summary>
public class RepairAnonymousTypeDiscriminators
{
    private readonly IDocumentStore _documentStore;

    public RepairAnonymousTypeDiscriminators(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    // Server-side JS patch: recursively walks the Details object, dropping any "$type" key whose
    // value names a compiler-generated anonymous type, and collapsing Newtonsoft's array-wrapper
    // shape ({ $type, $values }) back down to a plain array once its $type is stripped.
    private const string PatchScript = """
        function stripAnonymousTypes(value) {
            if (value === null || typeof value !== 'object') {
                return value;
            }
            if (Array.isArray(value)) {
                for (var i = 0; i < value.length; i++) {
                    value[i] = stripAnonymousTypes(value[i]);
                }
                return value;
            }
            var type = value['$type'];
            var isAnonymous = typeof type === 'string' && type.indexOf('<>f__AnonymousType') !== -1;
            if (isAnonymous && Object.prototype.hasOwnProperty.call(value, '$values')) {
                return stripAnonymousTypes(value['$values']);
            }
            var result = {};
            for (var key in value) {
                if (key === '$type' && isAnonymous) {
                    continue;
                }
                result[key] = stripAnonymousTypes(value[key]);
            }
            return result;
        }
        if (this.Details) {
            this.Details = stripAnonymousTypes(this.Details);
        }
        """;

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var rql = $"from Events update {{ {PatchScript} }}";
        var operation = await _documentStore.Operations.SendAsync(new PatchByQueryOperation(rql), token: ct);
        await operation.WaitForCompletionAsync(ct);
    }
}
