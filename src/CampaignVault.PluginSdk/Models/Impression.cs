using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>How hard a beat landed (IMPRESSIONS_PLAN.md I5). Faint is noticed and shrugged off.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpressionTier
{
    Faint,
    Mild,
    Strong,
    Overwhelming,
}

/// <summary>What the observer made of the beat's honesty (IMPRESSIONS_PLAN.md I4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpressionRead
{
    /// <summary>Nothing to see through: a gift, honest praise, a real threat.</summary>
    Sincere,

    /// <summary>A deception (an illusion, a lie, insincere flattery) taken at face value.</summary>
    Believed,

    /// <summary>A deception recognised for what it is: the appeal turns against the one who tried it.</summary>
    SawThrough,
}

/// <summary>One beat as it landed on one observer: kept on the observer, newest first, for habituation and the NPC's own record.</summary>
public class Impression
{
    public string? ActorId { get; set; }

    /// <summary>The tone as it landed (after any override, inversion or misgiving), e.g. delight, fear, insult.</summary>
    public string Tone { get; set; } = null!;

    /// <summary>The tone the actor meant, before the observer's reading; habituation counts by this.</summary>
    public string? IntendedTone { get; set; }

    public ImpressionTier Tier { get; set; }

    public ImpressionRead Read { get; set; }

    /// <summary>True when the observer knows who did it; only then does it move their opinion of the actor (D4).</summary>
    public bool KnowsAuthor { get; set; }

    /// <summary>The beat, short ("minor illusion", "a bouquet of wildflowers").</summary>
    public string? Cause { get; set; }

    /// <summary>The spell DC an examining observer must meet to see through it (an illusion believed); null otherwise.</summary>
    public int? DisbeliefDc { get; set; }

    /// <summary>Campaign hour (TotalDaysElapsed * 24 + Hour) it landed.</summary>
    public double Hour { get; set; }
}
