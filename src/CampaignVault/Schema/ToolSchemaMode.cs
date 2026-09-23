namespace CampaignVault.Schema;

/// <summary>
/// How much of the take_turn input schema is advertised in tools/list. Bound from
/// <c>CampaignVault:ToolSchemaMode</c> (env: <c>CampaignVault__ToolSchemaMode</c>).
/// </summary>
public enum ToolSchemaMode
{
    /// <summary>Constant, tiny schema: request envelope plus "consult skills / get_commit_schema" for verbs. Default.</summary>
    Stub,

    /// <summary>Tiered schema with a $defs entry per WorldChange $type (~7k tokens).</summary>
    Full
}
