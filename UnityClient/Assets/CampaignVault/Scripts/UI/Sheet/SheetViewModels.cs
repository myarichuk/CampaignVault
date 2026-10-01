using System.Collections.Generic;
using System.Globalization;
using Unity.Properties;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Sheet
{
    /// <summary>
    /// A character as the sheet shows it. These are snapshots: a new sheet from the server is a new view model, and
    /// the presenter rebinds the same elements to it (the template only changes between sheet and stat block).
    /// Every string is display-safe.
    /// </summary>
    public static class SheetViewModels
    {
        /// <summary>The full sheet for a player character, the parchment stat block for anyone else.</summary>
        public static ViewModel For(CharacterSheet sheet, bool statBlock, StatBlockSchema schema = null)
        {
            if (sheet == null) { return null; }
            return statBlock ? (ViewModel)new StatBlockViewModel(sheet, schema) : new SheetViewModel(sheet);
        }

        internal static string Whole(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        internal static string Where(CharacterSheet s)
        {
            var parts = new List<string>();
            if (s.Location.Length > 0) { parts.Add(Ui.PrettyId(s.Location)); }
            if (s.Activity.Length > 0) { parts.Add(s.Activity); }
            return string.Join(" — ", parts.ToArray());
        }
    }

    /// <summary>A condition or mood chip; long text is cut, the hover keeps it whole.</summary>
    public sealed class BadgeViewModel : ViewModel
    {
        public BadgeViewModel(string text, string tone, string icon)
        {
            string full = Net.TextSanitizer.Clean(text ?? string.Empty, 0);
            string shown = full.Length > 32 ? full.Substring(0, 32).TrimEnd() + "…" : full;
            Text = DisplayText.Plain(shown.ToUpperInvariant());
            Tooltip = shown.Length != full.Length ? full : string.Empty;
            Tone = tone;
            Icon = icon ?? string.Empty;
        }

        [CreateProperty] public string Text { get; private set; }
        [CreateProperty] public string Tooltip { get; private set; }
        /// <summary>cv-chip--&lt;tone&gt;: blood, gold.</summary>
        [CreateProperty] public string Tone { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
    }

    /// <summary>One box of the vitals strip (armor, speed, initiative…).</summary>
    public sealed class VitalViewModel : ViewModel, ITemplated
    {
        public VitalViewModel(string caption, string value, string sub, string tone)
        {
            Caption = caption;
            Value = DisplayText.Plain(value);
            Sub = DisplayText.Plain(sub ?? string.Empty);
            Tone = tone ?? string.Empty;
        }

        public string Template { get { return "Sheet/Vital"; } }
        [CreateProperty] public string Caption { get; private set; }
        [CreateProperty] public string Value { get; private set; }
        [CreateProperty] public string Sub { get; private set; }
        /// <summary>cv-vital--&lt;tone&gt;: ac, spell.</summary>
        [CreateProperty] public string Tone { get; private set; }
    }

    /// <summary>The hit-point box: numbers and a bar.</summary>
    public sealed class HpVitalViewModel : ViewModel, ITemplated
    {
        public HpVitalViewModel(CharacterSheet s)
        {
            Current = SheetViewModels.Whole(s.CurrentHp);
            Max = "/ " + SheetViewModels.Whole(s.MaxHp);
            Fraction = (float)s.HpFraction;
        }

        public string Template { get { return "Sheet/HpVital"; } }
        [CreateProperty] public string Current { get; private set; }
        [CreateProperty] public string Max { get; private set; }
        [CreateProperty] public float Fraction { get; private set; }
    }

    /// <summary>An ability medallion: STR, +3, 16.</summary>
    public sealed class MedallionViewModel : ViewModel
    {
        public MedallionViewModel(CharacterSheet.Ability a)
        {
            Short = DisplayText.Plain(a.Short);
            Mod = CharacterSheet.Signed(a.Mod);
            Score = a.Score >= 0 ? a.Score.ToString(CultureInfo.InvariantCulture) : string.Empty;
            // No score (PF2e modifiers only): the full name sits where the score would.
            Full = a.Score >= 0 ? string.Empty : DisplayText.Plain(a.Name);
            Tooltip = a.Name;
        }

        [CreateProperty] public string Short { get; private set; }
        [CreateProperty] public string Mod { get; private set; }
        [CreateProperty] public string Score { get; private set; }
        [CreateProperty] public string Full { get; private set; }
        [CreateProperty] public string Tooltip { get; private set; }
    }

    /// <summary>A save or skill: a proficiency pip (5e) or rank letter (PF2e), the name, the modifier.</summary>
    public sealed class CheckViewModel : ViewModel
    {
        public CheckViewModel(CharacterSheet.Check c, bool ranked)
        {
            Ranked = ranked;
            RankLetter = ranked ? CharacterSheet.RankLetter(c.Rank) : string.Empty;
            RankVariant = ranked ? c.Rank.ToString(CultureInfo.InvariantCulture) : string.Empty;
            Proficient = c.Rank > 0;
            Tooltip = ranked ? CharacterSheet.RankName(c.Rank) : c.Rank > 0 ? "Proficient" : string.Empty;
            Name = DisplayText.Plain(c.Name);
            // Saves named after an ability ("Strength") don't repeat it as "STR".
            string shortOf = c.Name.Length >= 3 ? c.Name.Substring(0, 3).ToUpperInvariant() : c.Name;
            Ability = c.Ability.Length > 0 && c.Ability != shortOf ? DisplayText.Plain(c.Ability) : string.Empty;
            Mod = CharacterSheet.Signed(c.Mod);
        }

        [CreateProperty] public bool Ranked { get; private set; }
        [CreateProperty] public string RankLetter { get; private set; }
        /// <summary>cv-check--rank&lt;n&gt;; empty for 5e.</summary>
        [CreateProperty] public string RankVariant { get; private set; }
        [CreateProperty] public bool Proficient { get; private set; }
        [CreateProperty] public string Tooltip { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Ability { get; private set; }
        [CreateProperty] public string Mod { get; private set; }
    }

    public sealed class PipViewModel : ViewModel
    {
        public PipViewModel(bool full) { Full = full; }
        [CreateProperty] public bool Full { get; private set; }
    }

    /// <summary>A resource (spell slots, rage…): pips up to eight, and the count.</summary>
    public sealed class ResourceViewModel : ViewModel
    {
        public ResourceViewModel(CharacterSheet.Resource r)
        {
            Name = DisplayText.Plain(r.Name);
            Count = r.Current + " / " + r.Max;
            Pips = new List<PipViewModel>();
            if (r.Max <= 8) { for (int i = 0; i < r.Max; i++) { Pips.Add(new PipViewModel(i < r.Current)); } }
        }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Count { get; private set; }
        [CreateProperty] public List<PipViewModel> Pips { get; private set; }
    }

    public sealed class GearViewModel : ViewModel
    {
        public GearViewModel(CharacterSheet.Gear g, bool equipped)
        {
            Name = DisplayText.Plain(g.Name);
            Detail = DisplayText.Plain(g.Detail);
            Quantity = g.Quantity > 1 ? "×" + g.Quantity : string.Empty;
            Equipped = equipped;
            Icon = g.Category == "Weapon" ? "attack" : g.Category == "Armor" ? "ac" : equipped ? "equip" : "pack";
            Tooltip = equipped ? "Equipped" : "Carried";
        }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Detail { get; private set; }
        [CreateProperty] public string Quantity { get; private set; }
        [CreateProperty] public bool Equipped { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
        [CreateProperty] public string Tooltip { get; private set; }
    }

    /// <summary>A need (hunger, fatigue…): the word, and a bar of what's left.</summary>
    public sealed class GaugeViewModel : ViewModel
    {
        public GaugeViewModel(CharacterSheet.Gauge n)
        {
            Name = DisplayText.Plain(n.Name);
            Word = DisplayText.Plain(n.Word);
            Severity = n.Severity == 2 ? "pressing" : n.Severity == 1 ? "noticeable" : string.Empty;
            // Warmth is a temperature, not a 0-100 need: word only.
            ShowBar = n.Name != "Warmth";
            Fraction = (float)(1.0 - n.Value / 100.0);
        }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Word { get; private set; }
        /// <summary>cv-gauge--&lt;severity&gt;.</summary>
        [CreateProperty] public string Severity { get; private set; }
        [CreateProperty] public bool ShowBar { get; private set; }
        [CreateProperty] public float Fraction { get; private set; }
    }

    public sealed class LedgerViewModel : ViewModel
    {
        public LedgerViewModel(CharacterSheet.Recent r)
        {
            Day = r.Day >= 0 ? "DAY " + (r.Day + 1) : string.Empty;
            Text = DisplayText.Plain(r.Summary);
        }

        [CreateProperty] public string Day { get; private set; }
        [CreateProperty] public string Text { get; private set; }
    }

    /// <summary>
    /// The full sheet (Templates/Sheet/CharacterSheet.uxml): hero, vitals, ability medallions, saves and skills,
    /// resources, features, gear, needs, recent events.
    /// </summary>
    public sealed class SheetViewModel : ViewModel, ITemplated
    {
        public SheetViewModel(CharacterSheet s)
        {
            Sheet = s;
            Monogram = Ui.Monogram(s.Name);
            Identity = DisplayText.Plain(IdentityOf(s));
            Where = DisplayText.Plain(SheetViewModels.Where(s));
            Badges = new List<BadgeViewModel>();
            foreach (string c in s.Conditions) { Badges.Add(new BadgeViewModel(c, "blood", "condition")); }
            if (s.Mood.Length > 0 && s.Mood != "Steady") { Badges.Add(new BadgeViewModel(s.Mood, s.Mood == "Bold" ? "gold" : "blood", null)); }
            Looks = DisplayText.Plain(s.Appearance);

            Vitals = new List<ViewModel>();
            if (s.MaxHp > 0) { Vitals.Add(new HpVitalViewModel(s)); }
            if (s.ArmorClass > 0) { Vitals.Add(new VitalViewModel("ARMOR", s.ArmorClass.ToString(CultureInfo.InvariantCulture), null, "ac")); }
            if (s.Speed.Length > 0) { Vitals.Add(new VitalViewModel("SPEED", s.Speed.Replace(" ft.", string.Empty).Replace(" feet", string.Empty), "feet", null)); }
            // PF2e rolls Perception (Wisdom plus a proficiency the sheet doesn't carry), so only 5e shows initiative.
            if (!s.IsPf2e && s.Abilities.Count > 1) { Vitals.Add(new VitalViewModel("INITIATIVE", CharacterSheet.Signed(s.Abilities[1].Mod), null, null)); }
            if (!s.IsPf2e && s.ProficiencyBonus > 0) { Vitals.Add(new VitalViewModel("PROFICIENCY", CharacterSheet.Signed(s.ProficiencyBonus), null, null)); }
            if (s.IsPf2e && s.Level > 0) { Vitals.Add(new VitalViewModel("LEVEL", s.Level.ToString(CultureInfo.InvariantCulture), null, null)); }
            if (s.SpellDc > 0)
            {
                string sub = s.SpellAttack != int.MinValue ? CharacterSheet.Signed(s.SpellAttack) + " to hit" : CharacterSheet.Title(s.SpellAbility);
                Vitals.Add(new VitalViewModel("SPELL DC", s.SpellDc.ToString(CultureInfo.InvariantCulture), sub, "spell"));
            }

            Abilities = s.Abilities.ConvertAll(delegate (CharacterSheet.Ability a) { return new MedallionViewModel(a); });
            SavesTwoColumns = !s.IsPf2e;
            Saves = s.Saves.ConvertAll(delegate (CharacterSheet.Check c) { return new CheckViewModel(c, s.IsPf2e); });
            Skills = s.Skills.ConvertAll(delegate (CharacterSheet.Check c) { return new CheckViewModel(c, s.IsPf2e); });
            Resources = s.Resources.ConvertAll(delegate (CharacterSheet.Resource r) { return new ResourceViewModel(r); });
            Features = s.Features.ConvertAll(delegate (string f) { return DisplayText.Plain(f); });
            Gear = new List<GearViewModel>();
            foreach (var g in s.Equipped) { Gear.Add(new GearViewModel(g, true)); }
            foreach (var g in s.Carried) { Gear.Add(new GearViewModel(g, false)); }
            Needs = s.Needs.ConvertAll(delegate (CharacterSheet.Gauge n) { return new GaugeViewModel(n); });
            Recent = new List<LedgerViewModel>();
            for (int i = 0; i < s.RecentEvents.Count && i < 5; i++) { Recent.Add(new LedgerViewModel(s.RecentEvents[i])); }
        }

        public string Template { get { return "Sheet/CharacterSheet"; } }
        public CharacterSheet Sheet { get; private set; }

        [CreateProperty] public string Monogram { get; private set; }
        [CreateProperty] public string Identity { get; private set; }
        [CreateProperty] public string Where { get; private set; }
        [CreateProperty] public List<BadgeViewModel> Badges { get; private set; }
        [CreateProperty] public string Looks { get; private set; }
        /// <summary>The hit-point box first (its own template), then the plain boxes.</summary>
        [CreateProperty] public List<ViewModel> Vitals { get; private set; }
        [CreateProperty] public List<MedallionViewModel> Abilities { get; private set; }
        [CreateProperty] public bool SavesTwoColumns { get; private set; }
        [CreateProperty] public List<CheckViewModel> Saves { get; private set; }
        [CreateProperty] public List<CheckViewModel> Skills { get; private set; }
        [CreateProperty] public List<ResourceViewModel> Resources { get; private set; }
        [CreateProperty] public List<string> Features { get; private set; }
        [CreateProperty] public List<GearViewModel> Gear { get; private set; }
        [CreateProperty] public List<GaugeViewModel> Needs { get; private set; }
        [CreateProperty] public List<LedgerViewModel> Recent { get; private set; }

        private static string IdentityOf(CharacterSheet s)
        {
            var parts = new List<string>();
            if (s.Lineage.Length > 0) { parts.Add(s.Lineage); }
            if (s.ClassLine.Length > 0) { parts.Add(s.ClassLine); }
            else if (s.Level > 0) { parts.Add("Level " + s.Level); }
            if (s.Background.Length > 0) { parts.Add(s.Background); }
            if (parts.Count == 0) { parts.Add(s.IsPc ? "Adventurer" : "Companion"); }
            return string.Join(" · ", parts.ToArray());
        }
    }

    /// <summary>"Armor Class 15", optionally with a bar under it (hit points).</summary>
    public sealed class StatLineViewModel : ViewModel
    {
        public StatLineViewModel(string key, string value, float fraction = -1f)
        {
            Key = DisplayText.Plain(key);
            Value = DisplayText.Plain(value);
            HasBar = fraction >= 0f;
            Fraction = HasBar ? fraction : 0f;
        }

        [CreateProperty] public string Key { get; private set; }
        [CreateProperty] public string Value { get; private set; }
        [CreateProperty] public bool HasBar { get; private set; }
        [CreateProperty] public float Fraction { get; private set; }
    }

    public sealed class StatAbilityViewModel : ViewModel
    {
        public StatAbilityViewModel(CharacterSheet.Ability a)
        {
            Short = DisplayText.Plain(a.Short);
            Value = a.Score >= 0 ? a.Score + " (" + CharacterSheet.Signed(a.Mod) + ")" : CharacterSheet.Signed(a.Mod);
        }

        [CreateProperty] public string Short { get; private set; }
        [CreateProperty] public string Value { get; private set; }
    }

    public sealed class ParagraphViewModel : ViewModel
    {
        public ParagraphViewModel(string text, bool italic)
        {
            Text = DisplayText.Plain(text);
            Italic = italic;
        }

        [CreateProperty] public string Text { get; private set; }
        [CreateProperty] public bool Italic { get; private set; }
    }

    /// <summary>"Traits", "Gear", "At the table", "Lately": a heading and its paragraphs.</summary>
    public sealed class StatSectionViewModel : ViewModel
    {
        public StatSectionViewModel(string title)
        {
            Title = title;
            Paragraphs = new List<ParagraphViewModel>();
        }

        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public List<ParagraphViewModel> Paragraphs { get; private set; }
    }

    /// <summary>
    /// The parchment stat block (Templates/Sheet/StatBlock.uxml) for a companion or NPC. With a schema (the companion
    /// stat block the builder edits), every schema field the parchment has no fixed line for is drawn too, under its
    /// group, so a system or plugin that adds fields needs no client work.
    /// </summary>
    public sealed class StatBlockViewModel : ViewModel, ITemplated
    {
        /// <summary>Schema keys the fixed lines already show.</summary>
        private static readonly HashSet<string> Drawn = new HashSet<string>
        {
            "statBlockHp", "armorClass", "movement", "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma",
        };

        public StatBlockViewModel(CharacterSheet s, StatBlockSchema schema = null)
        {
            Sheet = s;
            Name = DisplayText.Plain(s.Name);
            TypeLine = DisplayText.Plain(TypeLineOf(s));
            Badges = s.Conditions.ConvertAll(delegate (string c) { return DisplayText.Plain(c.ToUpperInvariant()); });

            TopLines = new List<StatLineViewModel>();
            if (s.ArmorClass > 0) { TopLines.Add(new StatLineViewModel("Armor Class", s.ArmorClass.ToString(CultureInfo.InvariantCulture))); }
            if (s.MaxHp > 0) { TopLines.Add(new StatLineViewModel("Hit Points", SheetViewModels.Whole(s.CurrentHp) + " of " + SheetViewModels.Whole(s.MaxHp), (float)s.HpFraction)); }
            if (s.Speed.Length > 0) { TopLines.Add(new StatLineViewModel("Speed", s.Speed)); }

            Abilities = s.Abilities.ConvertAll(delegate (CharacterSheet.Ability a) { return new StatAbilityViewModel(a); });

            Lines = new List<StatLineViewModel>();
            string saves = Listed(s.Saves, s.IsPf2e);
            if (saves.Length > 0) { Lines.Add(new StatLineViewModel("Saving Throws", saves)); }
            string skills = Listed(s.Skills, s.IsPf2e);
            if (skills.Length > 0) { Lines.Add(new StatLineViewModel("Skills", skills)); }
            if (s.PassivePerception > 0) { Lines.Add(new StatLineViewModel("Senses", "passive Perception " + s.PassivePerception)); }
            if (s.SpellDc > 0)
            {
                string cast = "spell save DC " + s.SpellDc;
                if (s.SpellAttack != int.MinValue) { cast += ", " + CharacterSheet.Signed(s.SpellAttack) + " to hit"; }
                if (s.SpellAbility.Length > 0) { cast += " (" + CharacterSheet.Title(s.SpellAbility) + ")"; }
                Lines.Add(new StatLineViewModel("Spellcasting", cast));
            }
            if (s.Resources.Count > 0)
            {
                var res = new List<string>();
                foreach (var r in s.Resources) { res.Add(r.Name + " " + r.Current + "/" + r.Max); }
                Lines.Add(new StatLineViewModel("Resources", string.Join(", ", res.ToArray())));
            }
            if (s.Mood.Length > 0) { Lines.Add(new StatLineViewModel("Spirit", s.Mood)); }
            if (schema != null) { AddSchemaLines(s, schema); }

            Sections = new List<StatSectionViewModel>();
            if (s.Features.Count > 0)
            {
                var traits = Section("Traits");
                foreach (string f in s.Features) { traits.Paragraphs.Add(new ParagraphViewModel(f + ".", false)); }
            }
            if (s.Equipped.Count > 0 || s.Carried.Count > 0)
            {
                var gear = new List<string>();
                foreach (var g in s.Equipped) { gear.Add(g.Name + (g.Quantity > 1 ? " ×" + g.Quantity : string.Empty) + " (worn)"); }
                foreach (var g in s.Carried) { gear.Add(g.Name + (g.Quantity > 1 ? " ×" + g.Quantity : string.Empty)); }
                Section("Gear").Paragraphs.Add(new ParagraphViewModel(string.Join(", ", gear.ToArray()) + ".", false));
            }
            if (s.Appearance.Length > 0 || s.Activity.Length > 0)
            {
                var table = Section("At the table");
                if (s.Appearance.Length > 0) { table.Paragraphs.Add(new ParagraphViewModel(s.Appearance, true)); }
                string where = SheetViewModels.Where(s);
                if (where.Length > 0) { table.Paragraphs.Add(new ParagraphViewModel(where + ".", false)); }
            }
            if (s.RecentEvents.Count > 0)
            {
                var lately = Section("Lately");
                for (int i = 0; i < s.RecentEvents.Count && i < 3; i++) { lately.Paragraphs.Add(new ParagraphViewModel(s.RecentEvents[i].Summary, true)); }
            }
        }

        public string Template { get { return "Sheet/StatBlock"; } }
        public CharacterSheet Sheet { get; private set; }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string TypeLine { get; private set; }
        [CreateProperty] public List<string> Badges { get; private set; }
        /// <summary>Armor class, hit points (with a bar), speed: above the abilities.</summary>
        [CreateProperty] public List<StatLineViewModel> TopLines { get; private set; }
        [CreateProperty] public List<StatAbilityViewModel> Abilities { get; private set; }
        [CreateProperty] public List<StatLineViewModel> Lines { get; private set; }
        [CreateProperty] public List<StatSectionViewModel> Sections { get; private set; }

        private StatSectionViewModel Section(string title)
        {
            var section = new StatSectionViewModel(title);
            Sections.Add(section);
            return section;
        }

        /// <summary>Schema fields the fixed lines don't cover, read from the character's system stats.</summary>
        private void AddSchemaLines(CharacterSheet s, StatBlockSchema schema)
        {
            foreach (var f in schema.Fields)
            {
                if (Drawn.Contains(f.Key) || f.Type == "abilities") { continue; }
                string value = Format(s.Stats.Get(f.Key), f);
                if (value.Length > 0) { Lines.Add(new StatLineViewModel(f.Label, value)); }
            }
        }

        /// <summary>int and text as they are; a list joined; attacks as "Bite +4 (1d6+2)".</summary>
        public static string Format(JsonValue v, StatBlockField f)
        {
            switch (v.Kind)
            {
                case JsonKind.Number: return SheetViewModels.Whole(v.NumberValue);
                case JsonKind.String: return v.StringValue;
                case JsonKind.Array:
                    var parts = new List<string>();
                    foreach (var item in v.ArrayValue)
                    {
                        if (item.Kind == JsonKind.String) { parts.Add(item.StringValue); continue; }
                        if (item.Kind != JsonKind.Object) { continue; }
                        string name = item.GetString("name", string.Empty);
                        var bonus = item.Get("bonus");
                        string damage = item.GetString("damage", string.Empty);
                        string text = name;
                        if (bonus.Kind == JsonKind.Number) { text += " " + CharacterSheet.Signed((int)bonus.NumberValue); }
                        if (damage.Length > 0) { text += " (" + damage + ")"; }
                        if (text.Trim().Length > 0) { parts.Add(text.Trim()); }
                    }
                    return string.Join(", ", parts.ToArray());
                default: return string.Empty;
            }
        }

        private static string TypeLineOf(CharacterSheet s)
        {
            var parts = new List<string>();
            if (s.Lineage.Length > 0) { parts.Add(s.Lineage); }
            if (s.ClassLine.Length > 0) { parts.Add(s.ClassLine); }
            else if (s.Level > 0) { parts.Add("level " + s.Level); }
            parts.Add(s.IsCompanion ? "companion" : "non-player character");
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>"Wis +5, Cha +3": proficient (or trained) entries only, as a stat block lists them.</summary>
        private static string Listed(List<CharacterSheet.Check> checks, bool ranked)
        {
            var parts = new List<string>();
            foreach (var c in checks)
            {
                if (c.Rank <= 0) { continue; }
                string name = checks.Count <= 6 && !ranked ? CharacterSheet.Title(c.Ability.ToLowerInvariant()) : c.Name;
                parts.Add(name + " " + CharacterSheet.Signed(c.Mod) + (ranked && c.Rank > 1 ? " (" + CharacterSheet.RankName(c.Rank).ToLowerInvariant() + ")" : string.Empty));
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
