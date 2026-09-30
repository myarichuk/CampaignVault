using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// A character, any party id. Player characters get the full sheet (hero,
    /// vitals, ability medallions, saves and skills, resources, gear, needs,
    /// recent events); companions and NPCs get a parchment stat block. Raw
    /// engine values stay in the F12 inspector.
    /// </summary>
    public sealed class CharacterSheetOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private string _id = string.Empty;
        private CharacterSheet _sheet;
        private string _error = string.Empty;
        private bool _loading;
        private Label _title;
        private VisualElement _modal;

        public CharacterSheetOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "Character"; } }
        protected override string TitleIcon { get { return "character"; } }
        protected override string ModalClass { get { return "cv-modal--sheet"; } }

        /// <summary>True once the sheet for the current id has loaded (or failed).</summary>
        public bool Loaded { get { return !_loading; } }

        public void SetCharacter(string id)
        {
            _id = id ?? string.Empty;
            _sheet = null;
            _error = string.Empty;
        }

        protected override void BuildContent()
        {
            _title = Root.Q<Label>(className: "cv-modal__title");
            _modal = Root.Q(className: "cv-modal");
        }

        public override void OnOpen()
        {
            _loading = true;
            Render();
            string id = _id;
            _controller.Run(Load(id));
        }

        private System.Collections.IEnumerator Load(string id)
        {
            McpOutcome<JsonValue> result = null;
            yield return _controller.GetCharacterDetail(id, delegate (McpOutcome<JsonValue> o) { result = o; });
            if (id != _id) { yield break; }
            _loading = false;
            if (result.Ok) { _sheet = CharacterSheet.FromPayload(result.Data); }
            else { _error = result.ErrorMessage; }
            Render();
        }

        private void Render()
        {
            Body.Clear();
            Foot.Clear();
            bool statBlock = _sheet != null && !_sheet.IsPc && _id != _state.PcId;
            _modal.EnableInClassList("cv-modal--statblock", statBlock);
            if (_loading && _sheet == null) { Body.Add(Ui.Empty("character", "Reading the sheet…")); return; }
            if (_sheet == null) { Body.Add(Ui.Empty("warning", "Could not load " + _id + ": " + _error)); return; }
            // The stat block carries the name itself; its dialog is titled by role.
            Ui.SetText(_title, statBlock ? (_sheet.IsCompanion ? "COMPANION" : "CHARACTER") : _sheet.Name.ToUpperInvariant());
            if (statBlock) { RenderStatBlock(_sheet); }
            else { RenderSheet(_sheet); }

            if (_id == _state.PcId) { Foot.Add(Ui.Text("YOUR CHARACTER", "cv-caption")); }
            else if (_sheet.Id.Length > 0)
            {
                string id = _id;
                Foot.Add(Ui.Button("PLAY AS THIS CHARACTER", "character", null, delegate { _controller.SetPcId(id); Render(); }));
            }
        }

        // =================================================================== sheet

        private void RenderSheet(CharacterSheet s)
        {
            var hero = Ui.El("cv-sheet__hero");
            hero.Add(Ui.Text(Ui.Monogram(s.Name), "cv-sheet__crest"));
            var identity = Ui.El("cv-sheet__identity");
            identity.Add(Ui.Text(Identity(s), "cv-sheet__lineage"));
            string where = Where(s);
            if (where.Length > 0) { identity.Add(Ui.Text(where, "cv-sheet__where")); }
            var badges = Ui.El("cv-sheet__badges");
            foreach (string c in s.Conditions) { badges.Add(Ui.Chip(c, "blood", "condition")); }
            if (s.Mood.Length > 0 && s.Mood != "Steady") { badges.Add(Ui.Chip(s.Mood, s.Mood == "Bold" ? "gold" : "blood")); }
            if (badges.childCount > 0) { identity.Add(badges); }
            hero.Add(identity);
            Body.Add(hero);
            if (s.Appearance.Length > 0) { Body.Add(Ui.Text(s.Appearance, "cv-sheet__looks")); }

            Body.Add(Vitals(s));
            if (s.Abilities.Count > 0) { Body.Add(Abilities(s)); }

            var cols = Ui.El("cv-sheet__cols");
            var left = Ui.El("cv-sheet__col cv-sheet__col--left");
            var right = Ui.El("cv-sheet__col");
            cols.Add(left);
            cols.Add(right);
            Body.Add(cols);

            if (s.Saves.Count > 0)
            {
                var saves = Section(left, "Saving throws");
                var list = Ui.El(s.IsPf2e ? null : "cv-checks--two");
                foreach (var check in s.Saves) { list.Add(CheckRow(check, s.IsPf2e)); }
                saves.Add(list);
            }
            if (s.Skills.Count > 0)
            {
                var skills = Section(left, "Skills");
                foreach (var check in s.Skills) { skills.Add(CheckRow(check, s.IsPf2e)); }
            }

            if (s.Resources.Count > 0)
            {
                var res = Section(right, "Resources");
                foreach (var r in s.Resources) { res.Add(ResourceRow(r)); }
            }
            if (s.Features.Count > 0)
            {
                var feats = Section(right, "Features & traits");
                var wrap = Ui.El("cv-features");
                foreach (string f in s.Features) { wrap.Add(Ui.Text(f, "cv-feature")); }
                feats.Add(wrap);
            }
            if (s.Equipped.Count > 0 || s.Carried.Count > 0)
            {
                var gear = Section(right, "Equipment");
                foreach (var g in s.Equipped) { gear.Add(GearRow(g, true)); }
                foreach (var g in s.Carried) { gear.Add(GearRow(g, false)); }
            }
            if (s.Needs.Count > 0)
            {
                var needs = Section(right, "Condition & needs");
                foreach (var n in s.Needs) { needs.Add(GaugeRow(n)); }
            }
            if (s.RecentEvents.Count > 0)
            {
                var recent = Section(right, "Lately");
                for (int i = 0; i < s.RecentEvents.Count && i < 5; i++) { recent.Add(LedgerRow(s.RecentEvents[i])); }
            }
        }

        private static string Identity(CharacterSheet s)
        {
            var parts = new List<string>();
            if (s.Lineage.Length > 0) { parts.Add(s.Lineage); }
            if (s.ClassLine.Length > 0) { parts.Add(s.ClassLine); }
            else if (s.Level > 0) { parts.Add("Level " + s.Level); }
            if (s.Background.Length > 0) { parts.Add(s.Background); }
            if (parts.Count == 0) { parts.Add(s.IsPc ? "Adventurer" : "Companion"); }
            return string.Join(" · ", parts.ToArray());
        }

        private static string Where(CharacterSheet s)
        {
            var parts = new List<string>();
            if (s.Location.Length > 0) { parts.Add(Ui.PrettyId(s.Location)); }
            if (s.Activity.Length > 0) { parts.Add(s.Activity); }
            return string.Join(" — ", parts.ToArray());
        }

        private static VisualElement Vitals(CharacterSheet s)
        {
            var strip = Ui.El("cv-vitals");
            if (s.MaxHp > 0)
            {
                var hp = Ui.El("cv-vital cv-vital--hp");
                var row = Ui.El("cv-vital__row");
                var numbers = Ui.El("cv-row");
                numbers.style.alignItems = Align.FlexEnd;
                numbers.Add(Ui.Text(Whole(s.CurrentHp), "cv-vital__value"));
                numbers.Add(Ui.Text("/ " + Whole(s.MaxHp), "cv-vital__max"));
                row.Add(numbers);
                row.Add(Ui.Text("HIT POINTS", "cv-caption"));
                hp.Add(row);
                hp.Add(Ui.Bar(s.HpFraction, "cv-bar--thick"));
                strip.Add(hp);
            }
            if (s.ArmorClass > 0) { strip.Add(Vital("ARMOR", s.ArmorClass.ToString(CultureInfo.InvariantCulture), null, "cv-vital--ac")); }
            if (s.Speed.Length > 0) { strip.Add(Vital("SPEED", s.Speed.Replace(" ft.", string.Empty).Replace(" feet", string.Empty), "feet", null)); }
            // PF2e rolls Perception (Wisdom plus a proficiency the sheet doesn't carry), so only 5e shows initiative.
            if (!s.IsPf2e && s.Abilities.Count > 1) { strip.Add(Vital("INITIATIVE", CharacterSheet.Signed(s.Abilities[1].Mod), null, null)); }
            if (!s.IsPf2e && s.ProficiencyBonus > 0) { strip.Add(Vital("PROFICIENCY", CharacterSheet.Signed(s.ProficiencyBonus), null, null)); }
            if (s.IsPf2e && s.Level > 0) { strip.Add(Vital("LEVEL", s.Level.ToString(CultureInfo.InvariantCulture), null, null)); }
            if (s.SpellDc > 0)
            {
                string sub = s.SpellAttack != int.MinValue ? CharacterSheet.Signed(s.SpellAttack) + " to hit" : CharacterSheet.Title(s.SpellAbility);
                strip.Add(Vital("SPELL DC", s.SpellDc.ToString(CultureInfo.InvariantCulture), sub, "cv-vital--spell"));
            }
            return strip;
        }

        private static VisualElement Vital(string caption, string value, string sub, string classes)
        {
            var v = Ui.El("cv-vital");
            Ui.AddClasses(v, classes);
            v.Add(Ui.Text(value, "cv-vital__value"));
            v.Add(Ui.Text(caption, "cv-caption cv-vital__caption"));
            if (!string.IsNullOrEmpty(sub)) { v.Add(Ui.Text(sub, "cv-vital__sub")); }
            return v;
        }

        private static VisualElement Abilities(CharacterSheet s)
        {
            var row = Ui.El("cv-abilities");
            foreach (var a in s.Abilities)
            {
                var m = Ui.El("cv-ability");
                m.Add(Ui.Text(a.Short, "cv-ability__name"));
                m.Add(Ui.Text(CharacterSheet.Signed(a.Mod), "cv-ability__mod"));
                if (a.Score >= 0) { m.Add(Ui.Text(a.Score.ToString(CultureInfo.InvariantCulture), "cv-ability__score")); }
                else { m.Add(Ui.Text(a.Name, "cv-ability__full")); }
                TooltipLayer.Attach(m, a.Name);
                row.Add(m);
            }
            return row;
        }

        private static VisualElement Section(VisualElement parent, string title)
        {
            var section = Ui.El("cv-sheet__section");
            var head = Ui.El("cv-sheet__heading");
            head.Add(Ui.Text(title.ToUpperInvariant(), "cv-caption"));
            section.Add(head);
            parent.Add(section);
            return section;
        }

        private static VisualElement CheckRow(CharacterSheet.Check c, bool ranked)
        {
            var row = Ui.El("cv-check");
            if (ranked)
            {
                row.AddToClassList("cv-check--rank" + c.Rank);
                if (c.Rank > 0) { row.AddToClassList("cv-check--proficient"); }
                row.Add(Ui.Text(CharacterSheet.RankLetter(c.Rank), "cv-check__rank"));
                TooltipLayer.Attach(row, CharacterSheet.RankName(c.Rank));
            }
            else
            {
                if (c.Rank > 0) { row.AddToClassList("cv-check--proficient"); }
                row.Add(Ui.El("cv-check__pip"));
                if (c.Rank > 0) { TooltipLayer.Attach(row, "Proficient"); }
            }
            row.Add(Ui.Text(c.Name, "cv-check__name"));
            if (c.Ability.Length > 0 && c.Ability != ShortOf(c.Name)) { row.Add(Ui.Text(c.Ability, "cv-check__ability")); }
            row.Add(Ui.Text(CharacterSheet.Signed(c.Mod), "cv-check__mod"));
            return row;
        }

        /// <summary>Saves named after an ability ("Strength") don't repeat it as "STR".</summary>
        private static string ShortOf(string name)
        {
            return name.Length >= 3 ? name.Substring(0, 3).ToUpperInvariant() : name;
        }

        private static VisualElement ResourceRow(CharacterSheet.Resource r)
        {
            var row = Ui.El("cv-resource");
            row.Add(Ui.Text(r.Name, "cv-resource__name"));
            if (r.Max <= 8)
            {
                var pips = Ui.El("cv-resource__pips");
                for (int i = 0; i < r.Max; i++) { pips.Add(Ui.El("cv-pip" + (i < r.Current ? " cv-pip--full" : string.Empty))); }
                row.Add(pips);
            }
            row.Add(Ui.Text(r.Current + " / " + r.Max, "cv-resource__count"));
            return row;
        }

        private static VisualElement GearRow(CharacterSheet.Gear g, bool equipped)
        {
            var wrap = Ui.El();
            var row = Ui.El("cv-gear" + (equipped ? " cv-gear--equipped" : string.Empty));
            row.Add(Ui.Icon(GearIcon(g, equipped)));
            var col = Ui.El("cv-grow");
            col.Add(Ui.Text(g.Name, "cv-gear__name"));
            if (g.Detail.Length > 0) { col.Add(Ui.Text(g.Detail, "cv-gear__detail")); }
            row.Add(col);
            if (g.Quantity > 1) { row.Add(Ui.Text("×" + g.Quantity, "cv-gear__qty")); }
            if (equipped) { row.Add(Ui.Text("WORN", "cv-caption")); }
            TooltipLayer.Attach(row, equipped ? "Equipped" : "Carried");
            wrap.Add(row);
            return wrap;
        }

        private static string GearIcon(CharacterSheet.Gear g, bool equipped)
        {
            switch (g.Category)
            {
                case "Weapon": return "attack";
                case "Armor": return "ac";
                default: return equipped ? "equip" : "pack";
            }
        }

        private static VisualElement GaugeRow(CharacterSheet.Gauge n)
        {
            var row = Ui.El("cv-gauge" + (n.Severity == 2 ? " cv-gauge--pressing" : n.Severity == 1 ? " cv-gauge--noticeable" : string.Empty));
            row.Add(Ui.Text(n.Name, "cv-gauge__name"));
            row.Add(Ui.Text(n.Word, "cv-gauge__word"));
            // Warmth is a temperature, not a 0-100 need: word only.
            if (n.Name != "Warmth") { row.Add(Ui.Bar(1.0 - n.Value / 100.0)); }
            return row;
        }

        private static VisualElement LedgerRow(CharacterSheet.Recent r)
        {
            var row = Ui.El("cv-ledger");
            row.Add(Ui.Text(r.Day >= 0 ? "DAY " + (r.Day + 1) : string.Empty, "cv-ledger__day"));
            row.Add(Ui.Text(r.Summary, "cv-ledger__text"));
            return row;
        }

        // ============================================================== stat block

        private void RenderStatBlock(CharacterSheet s)
        {
            var block = Ui.El("cv-statblock");
            block.Add(Ui.Text(s.Name, "cv-statblock__name"));
            block.Add(Ui.Text(TypeLine(s), "cv-statblock__type"));
            if (s.Conditions.Count > 0)
            {
                var badges = Ui.El("cv-statblock__badges");
                foreach (string c in s.Conditions) { badges.Add(Ui.Text(c.ToUpperInvariant(), "cv-statblock__badge")); }
                block.Add(badges);
            }
            block.Add(Ui.El("cv-statblock__rule"));

            if (s.ArmorClass > 0) { Line(block, "Armor Class", s.ArmorClass.ToString(CultureInfo.InvariantCulture)); }
            if (s.MaxHp > 0)
            {
                Line(block, "Hit Points", Whole(s.CurrentHp) + " of " + Whole(s.MaxHp));
                block.Add(Ui.Bar(s.HpFraction));
            }
            if (s.Speed.Length > 0) { Line(block, "Speed", s.Speed); }

            if (s.Abilities.Count > 0)
            {
                block.Add(Ui.El("cv-statblock__rule"));
                var row = Ui.El("cv-statblock__abilities");
                foreach (var a in s.Abilities)
                {
                    var col = Ui.El("cv-statblock__ability");
                    col.Add(Ui.Text(a.Short, "cv-statblock__ability-name"));
                    string value = a.Score >= 0 ? a.Score + " (" + CharacterSheet.Signed(a.Mod) + ")" : CharacterSheet.Signed(a.Mod);
                    col.Add(Ui.Text(value, "cv-statblock__ability-value"));
                    row.Add(col);
                }
                block.Add(row);
                block.Add(Ui.El("cv-statblock__rule"));
            }

            string saves = Listed(s.Saves, s.IsPf2e);
            if (saves.Length > 0) { Line(block, "Saving Throws", saves); }
            string skills = Listed(s.Skills, s.IsPf2e);
            if (skills.Length > 0) { Line(block, "Skills", skills); }
            if (s.PassivePerception > 0) { Line(block, "Senses", "passive Perception " + s.PassivePerception); }
            if (s.SpellDc > 0)
            {
                string cast = "spell save DC " + s.SpellDc;
                if (s.SpellAttack != int.MinValue) { cast += ", " + CharacterSheet.Signed(s.SpellAttack) + " to hit"; }
                if (s.SpellAbility.Length > 0) { cast += " (" + CharacterSheet.Title(s.SpellAbility) + ")"; }
                Line(block, "Spellcasting", cast);
            }
            if (s.Resources.Count > 0)
            {
                var res = new List<string>();
                foreach (var r in s.Resources) { res.Add(r.Name + " " + r.Current + "/" + r.Max); }
                Line(block, "Resources", string.Join(", ", res.ToArray()));
            }
            if (s.Mood.Length > 0) { Line(block, "Spirit", s.Mood); }

            if (s.Features.Count > 0)
            {
                block.Add(Ui.Text("Traits", "cv-statblock__section"));
                foreach (string f in s.Features) { block.Add(Ui.Text(f + ".", "cv-statblock__para")); }
            }
            if (s.Equipped.Count > 0 || s.Carried.Count > 0)
            {
                block.Add(Ui.Text("Gear", "cv-statblock__section"));
                var gear = new List<string>();
                foreach (var g in s.Equipped) { gear.Add(g.Name + (g.Quantity > 1 ? " ×" + g.Quantity : string.Empty) + " (worn)"); }
                foreach (var g in s.Carried) { gear.Add(g.Name + (g.Quantity > 1 ? " ×" + g.Quantity : string.Empty)); }
                block.Add(Ui.Text(string.Join(", ", gear.ToArray()) + ".", "cv-statblock__para"));
            }
            if (s.Appearance.Length > 0 || s.Activity.Length > 0)
            {
                block.Add(Ui.Text("At the table", "cv-statblock__section"));
                if (s.Appearance.Length > 0) { block.Add(Ui.Text(s.Appearance, "cv-statblock__para cv-statblock__para--italic")); }
                string where = Where(s);
                if (where.Length > 0) { block.Add(Ui.Text(where + ".", "cv-statblock__para")); }
            }
            if (s.RecentEvents.Count > 0)
            {
                block.Add(Ui.Text("Lately", "cv-statblock__section"));
                for (int i = 0; i < s.RecentEvents.Count && i < 3; i++) { block.Add(Ui.Text(s.RecentEvents[i].Summary, "cv-statblock__para cv-statblock__para--italic")); }
            }
            Body.Add(block);
        }

        private static string TypeLine(CharacterSheet s)
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

        private static void Line(VisualElement block, string key, string value)
        {
            var line = Ui.El("cv-statblock__line");
            line.Add(Ui.Text(key, "cv-statblock__key"));
            line.Add(Ui.Text(value, "cv-statblock__value"));
            block.Add(line);
        }

        private static string Whole(double v)
        {
            return v.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
