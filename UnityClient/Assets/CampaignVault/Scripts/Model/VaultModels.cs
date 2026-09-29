using System.Collections.Generic;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// Provenance-tagged transcript segments. The kind decides the visual
    /// treatment: only Tool-sourced segments may render roll chips, so narration
    /// can never spoof a game roll no matter what the model writes.
    /// </summary>
    public enum SegmentKind { Narration, NpcVoice, Roll, System, ToolData, Player }

    public sealed class RollInfo
    {
        public string Label = string.Empty;
        public string Detail = string.Empty;
        public bool Success;
    }

    public sealed class TranscriptSegment
    {
        public SegmentKind Kind;
        public string Speaker = string.Empty;
        public string Text = string.Empty;
        public RollInfo Roll;
    }

    public sealed class VaultTranscript
    {
        public const int MaxSegments = 400;

        private readonly List<TranscriptSegment> _segments = new List<TranscriptSegment>();

        public IReadOnlyList<TranscriptSegment> Segments { get { return _segments; } }

        public void Add(TranscriptSegment segment)
        {
            _segments.Add(segment);
            EnforceCap();
        }

        private void EnforceCap()
        {
            while (_segments.Count > MaxSegments)
            {
                int drop = -1;
                for (int i = 0; i < _segments.Count; i++)
                {
                    if (_segments[i].Kind == SegmentKind.ToolData) { drop = i; break; }
                }
                _segments.RemoveAt(drop >= 0 ? drop : 0);
            }
        }

        public void Clear() { _segments.Clear(); }
    }

    /// <summary>PC character sheet, tolerant best-effort parse of get_entity output.</summary>
    public sealed class PcSheet
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Ruleset = string.Empty;
        public string Level = string.Empty;
        public double CurrentHp;
        public double MaxHp;
        public string ArmorClass = string.Empty;
        public string Location = string.Empty;
        public List<string> Conditions = new List<string>();
        public Dictionary<string, string> Stats = new Dictionary<string, string>();

        public double HpFraction
        {
            get
            {
                if (MaxHp <= 0) { return 1.0; }
                double f = CurrentHp / MaxHp;
                if (f < 0) { return 0; }
                if (f > 1) { return 1; }
                return f;
            }
        }

        public static PcSheet FromEntity(JsonValue entity)
        {
            var sheet = new PcSheet
            {
                Id = entity.GetStringAny(new[] { "id", "characterId", "Id" }, string.Empty),
                Name = entity.GetStringAny(new[] { "name", "Name" }, "Unknown"),
                Ruleset = entity.GetStringAny(new[] { "ruleset", "system", "Ruleset" }, string.Empty),
                ArmorClass = entity.GetStringAny(new[] { "armorClass", "ac", "ArmorClass", "AC" }, string.Empty),
                Location = entity.GetStringAny(new[] { "currentLocationId", "location", "Location" }, string.Empty),
            };
            sheet.CurrentHp = entity.GetNumber("currentHp", entity.GetNumber("CurrentHp", -1));
            sheet.MaxHp = entity.GetNumber("maxHp", entity.GetNumber("MaxHp", -1));
            sheet.Level = entity.GetStringAny(new[] { "level", "Level" }, string.Empty);
            var stats = entity.Get("systemStats");
            if (string.IsNullOrEmpty(sheet.Level)) { sheet.Level = stats.GetStringAny(new[] { "level", "Level" }, string.Empty); }
            if (string.IsNullOrEmpty(sheet.ArmorClass)) { sheet.ArmorClass = stats.GetStringAny(new[] { "armorClass", "ArmorClass" }, string.Empty); }
            foreach (var c in entity.GetArray("conditions"))
            {
                if (c.Kind == JsonKind.String) { sheet.Conditions.Add(c.StringValue); }
            }
            var sysStats = entity.Get("systemStats");
            if (!sysStats.IsNull && sysStats.Kind == JsonKind.Object && sysStats.ObjectValue != null)
            {
                foreach (var kv in sysStats.ObjectValue)
                {
                    if (kv.Value.Kind == JsonKind.String || kv.Value.Kind == JsonKind.Number || kv.Value.Kind == JsonKind.Bool)
                    {
                        sheet.Stats[kv.Key] = PrettyScalar(kv.Value);
                    }
                }
            }
            return sheet;
        }

        private static string PrettyScalar(JsonValue v)
        {
            if (v.Kind == JsonKind.Number) { return v.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (v.Kind == JsonKind.Bool) { return v.BoolValue ? "yes" : "no"; }
            return v.StringValue ?? string.Empty;
        }
    }

    /// <summary>Inventory row: an item name from the start_session party roster.</summary>
    public sealed class InventoryItem
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Quantity = "1";
        public bool Equipped;
        public string Slot = string.Empty;
        public string Rarity = string.Empty;
    }

    /// <summary>Companion / party member row.</summary>
    public sealed class Companion
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Kind = string.Empty;
        public double CurrentHp = -1;
        public double MaxHp = -1;
        public string Stance = string.Empty;
        public string Location = string.Empty;
        public bool IsMinion;

        public static Companion FromEntity(JsonValue entity)
        {
            return new Companion
            {
                Id = entity.GetStringAny(new[] { "id", "characterId", "Id" }, string.Empty),
                Name = entity.GetStringAny(new[] { "name", "Name" }, "Unknown"),
                Kind = entity.GetStringAny(new[] { "kind", "creatureKind", "Kind" }, string.Empty),
                CurrentHp = entity.GetNumber("currentHp", entity.GetNumber("CurrentHp", -1)),
                MaxHp = entity.GetNumber("maxHp", entity.GetNumber("MaxHp", -1)),
                Stance = entity.GetStringAny(new[] { "stance", "Stance" }, string.Empty),
                Location = entity.GetStringAny(new[] { "currentLocationId", "location", "Location" }, string.Empty),
                IsMinion = !entity.Get("minionLink").IsNull || !entity.Get("controlsMinionIds").IsNull,
            };
        }
    }
}
