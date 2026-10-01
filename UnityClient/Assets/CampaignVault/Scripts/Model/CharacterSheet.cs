using System;
using System.Collections.Generic;
using System.Globalization;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// What a character sheet or stat block shows, read from get_entity's full
    /// payload (character + equipped/carried + recentInteractions). Player
    /// terms only: engine internals (willpower, temperature, needs) come out as
    /// labelled words, and the raw values stay in the F12 inspector.
    /// </summary>
    public sealed class CharacterSheet
    {
        public sealed class Ability
        {
            public string Short = string.Empty;
            public string Name = string.Empty;
            /// <summary>5e score; -1 when the ruleset has only modifiers (PF2e).</summary>
            public int Score = -1;
            public int Mod;
        }

        /// <summary>A save or skill. Rank: 0 untrained, 1 trained/proficient, 2 expert, 3 master, 4 legendary.</summary>
        public sealed class Check
        {
            public string Name = string.Empty;
            public string Ability = string.Empty;
            public int Mod;
            public int Rank;
        }

        public sealed class Resource
        {
            public string Key = string.Empty;
            public string Name = string.Empty;
            public int Current;
            public int Max;
        }

        public sealed class Gear
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public int Quantity = 1;
            public string Category = string.Empty;
            public string Detail = string.Empty;
        }

        public sealed class Recent
        {
            public string Summary = string.Empty;
            public string Category = string.Empty;
            public int Day = -1;
        }

        /// <summary>A 0-100 need as a word, e.g. Hunger 65 → "Hungry".</summary>
        public sealed class Gauge
        {
            public string Name = string.Empty;
            public float Value;
            public string Word = string.Empty;
            /// <summary>0 fine, 1 noticeable, 2 pressing.</summary>
            public int Severity;
        }

        public string Id = string.Empty;
        public string Name = string.Empty;
        public bool IsPc;
        public bool IsCompanion;
        /// <summary>"dnd5e", "pf2e", or empty for narrative/unknown systems.</summary>
        public string System = string.Empty;
        public string Lineage = string.Empty;
        public string ClassLine = string.Empty;
        public string Background = string.Empty;
        public int Level = -1;
        public double CurrentHp = -1;
        public double MaxHp = -1;
        public int ArmorClass = -1;
        public string Speed = string.Empty;
        public int ProficiencyBonus = -1;
        public int PassivePerception = -1;
        public int SpellDc = -1;
        public int SpellAttack = int.MinValue;
        public string SpellAbility = string.Empty;
        public string Appearance = string.Empty;
        public string Activity = string.Empty;
        public string Location = string.Empty;
        public string Mood = string.Empty;

        public readonly List<Ability> Abilities = new List<Ability>();
        public readonly List<Check> Saves = new List<Check>();
        public readonly List<Check> Skills = new List<Check>();
        public readonly List<Resource> Resources = new List<Resource>();
        public readonly List<string> Conditions = new List<string>();
        public readonly List<string> Features = new List<string>();
        public readonly List<Gear> Equipped = new List<Gear>();
        public readonly List<Gear> Carried = new List<Gear>();
        public readonly List<Recent> RecentEvents = new List<Recent>();
        public readonly List<Gauge> Needs = new List<Gauge>();
        /// <summary>The raw systemStats object, for stat-block fields the sheet has no slot for (a schema's extras).</summary>
        public JsonValue Stats = JsonValue.Null;

        public bool HasStats { get { return System.Length > 0; } }
        public bool IsPf2e { get { return System == "pf2e"; } }

        public double HpFraction
        {
            get
            {
                if (MaxHp <= 0) { return 1.0; }
                double f = CurrentHp / MaxHp;
                return f < 0 ? 0 : f > 1 ? 1 : f;
            }
        }

        private static readonly string[][] AbilityNames =
        {
            new[] { "STR", "Strength" }, new[] { "DEX", "Dexterity" }, new[] { "CON", "Constitution" },
            new[] { "INT", "Intelligence" }, new[] { "WIS", "Wisdom" }, new[] { "CHA", "Charisma" },
        };

        private static readonly string[][] Dnd5eSkills =
        {
            new[] { "Acrobatics", "DEX" }, new[] { "Animal Handling", "WIS" }, new[] { "Arcana", "INT" }, new[] { "Athletics", "STR" },
            new[] { "Deception", "CHA" }, new[] { "History", "INT" }, new[] { "Insight", "WIS" }, new[] { "Intimidation", "CHA" },
            new[] { "Investigation", "INT" }, new[] { "Medicine", "WIS" }, new[] { "Nature", "INT" }, new[] { "Perception", "WIS" },
            new[] { "Performance", "CHA" }, new[] { "Persuasion", "CHA" }, new[] { "Religion", "INT" }, new[] { "Sleight of Hand", "DEX" },
            new[] { "Stealth", "DEX" }, new[] { "Survival", "WIS" },
        };

        private static readonly string[][] Pf2eSkills =
        {
            new[] { "Acrobatics", "DEX" }, new[] { "Arcana", "INT" }, new[] { "Athletics", "STR" }, new[] { "Crafting", "INT" },
            new[] { "Deception", "CHA" }, new[] { "Diplomacy", "CHA" }, new[] { "Intimidation", "CHA" }, new[] { "Medicine", "WIS" },
            new[] { "Nature", "WIS" }, new[] { "Occultism", "INT" }, new[] { "Performance", "CHA" }, new[] { "Religion", "WIS" },
            new[] { "Society", "INT" }, new[] { "Stealth", "DEX" }, new[] { "Survival", "WIS" }, new[] { "Thievery", "DEX" },
        };

        private static readonly string[][] Pf2eSaves = { new[] { "Fortitude", "CON" }, new[] { "Reflex", "DEX" }, new[] { "Will", "WIS" } };

        /// <summary>Reads get_entity's data object, or a bare character entity (older callers).</summary>
        public static CharacterSheet FromPayload(JsonValue payload)
        {
            var character = payload.Get("character");
            if (character.Kind != JsonKind.Object) { character = payload; }
            var sheet = new CharacterSheet
            {
                Id = character.GetString("id", string.Empty),
                Name = character.GetString("name", "Unknown"),
                IsPc = character.GetBool("isPc", false),
                IsCompanion = character.GetBool("isPartyCompanion", false),
                CurrentHp = character.GetNumber("currentHp", -1),
                MaxHp = character.GetNumber("maxHp", -1),
                Appearance = character.GetString("currentAppearance", string.Empty),
                Activity = character.GetString("currentActivity", string.Empty),
                Location = character.GetString("currentLocationId", string.Empty),
                Speed = character.GetString("speed", string.Empty),
            };
            string classLevel = character.GetString("classLevel", string.Empty);
            var stats = character.Get("systemStats");
            if (stats.Kind == JsonKind.Object) { sheet.Stats = stats; sheet.ReadStats(stats, classLevel); }
            else { sheet.ClassLine = classLevel; }

            foreach (var c in character.GetArray("conditions")) { sheet.AddCondition(c.Kind == JsonKind.String ? c.StringValue : c.GetString("name", string.Empty)); }
            var needs = character.Get("needs").Get("activeNeeds");
            sheet.ReadNeeds(needs.Kind == JsonKind.Object ? needs : payload.Get("knownNeeds"));
            ReadGear(payload.GetArray("equipped"), sheet.Equipped);
            ReadGear(payload.GetArray("carried"), sheet.Carried);
            foreach (var e in payload.GetArray("recentInteractions"))
            {
                string summary = e.GetString("summary", string.Empty);
                if (summary.Length == 0) { continue; }
                sheet.RecentEvents.Add(new Recent
                {
                    Summary = summary,
                    Category = e.GetString("category", string.Empty),
                    Day = (int)e.GetNumber("dayLogged", -1),
                });
            }
            return sheet;
        }

        private void ReadStats(JsonValue stats, string classLevel)
        {
            System = stats.GetString("$system", string.Empty).ToLowerInvariant();
            if (System != "dnd5e" && System != "pf2e") { System = string.Empty; }
            Level = (int)stats.GetNumber("level", -1);
            ArmorClass = (int)stats.GetNumber("armorClass", -1);
            Background = stats.GetString("background", string.Empty);
            SpellAbility = stats.GetString("spellcastingAbility", string.Empty);
            if (Speed.Length == 0)
            {
                double movement = stats.GetNumber("movement", -1);
                if (movement > 0) { Speed = movement.ToString("0", CultureInfo.InvariantCulture) + " ft."; }
            }

            var classes = new List<string>();
            foreach (var cl in stats.GetArray("classLevels"))
            {
                string name = cl.GetString("class", string.Empty);
                if (name.Length > 0) { classes.Add(Title(name) + " " + cl.GetNumber("level", 0).ToString("0", CultureInfo.InvariantCulture)); }
            }
            ClassLine = classes.Count > 0 ? string.Join(" / ", classes.ToArray()) : classLevel;

            var attributes = stats.Get("attributes");
            ProficiencyBonus = (int)attributes.GetNumber("proficiencyBonus", -1);
            PassivePerception = (int)attributes.GetNumber("passivePerception", -1);

            if (System == "pf2e")
            {
                string ancestry = stats.GetString("ancestry", string.Empty);
                string heritage = stats.GetString("heritage", string.Empty);
                Lineage = heritage.Length > 0 && ancestry.Length > 0 && heritage.IndexOf(ancestry, StringComparison.OrdinalIgnoreCase) < 0
                    ? heritage + " " + ancestry : heritage.Length > 0 ? heritage : ancestry;
                foreach (var a in AbilityNames)
                {
                    Abilities.Add(new Ability { Short = a[0], Name = a[1], Mod = (int)stats.GetNumber(a[1].ToLowerInvariant() + "Mod", 0) });
                }
                ReadRanked(Pf2eSaves, stats.Get("savingThrowModifiers"), stats.Get("saveProficiencies"), Saves);
                ReadRanked(Pf2eSkills, stats.Get("skillModifiers"), stats.Get("skillProficiencies"), Skills);
                SpellDc = (int)stats.GetNumber("spellDc", -1);
                // PF2e sheets without a spellcasting proficiency still carry defaults; only casters show the DC.
                if (stats.GetString("spellcastingProficiency", string.Empty).Length == 0 || !HasCasterFeature(stats)) { SpellDc = -1; SpellAbility = string.Empty; }
                foreach (string list in new[] { "ancestryFeats", "classFeats", "skillFeats", "generalFeats" })
                {
                    foreach (var f in stats.GetArray(list)) { AddFeature(f.StringValue); }
                }
            }
            else if (System == "dnd5e")
            {
                Lineage = stats.GetString("race", string.Empty);
                foreach (var a in AbilityNames)
                {
                    int score = (int)stats.GetNumber(a[1].ToLowerInvariant(), 10);
                    Abilities.Add(new Ability { Short = a[0], Name = a[1], Score = score, Mod = Dnd5eMod(score) });
                }
                var saves = stats.Get("savingThrowModifiers");
                foreach (var a in AbilityNames)
                {
                    int listed;
                    bool proficient = TryGetInt(saves, a[1], out listed);
                    Saves.Add(new Check { Name = a[1], Ability = a[0], Mod = proficient ? listed : ModOf(a[0]), Rank = proficient ? 1 : 0 });
                }
                var skills = stats.Get("skillModifiers");
                foreach (var s in Dnd5eSkills)
                {
                    int listed;
                    bool proficient = TryGetInt(skills, s[0], out listed);
                    Skills.Add(new Check { Name = s[0], Ability = s[1], Mod = proficient ? listed : ModOf(s[1]), Rank = proficient ? 1 : 0 });
                }
                SpellDc = (int)stats.GetNumber("spellSaveDc", -1);
                double attack = stats.GetNumber("spellAttackBonus", double.NaN);
                if (!double.IsNaN(attack)) { SpellAttack = (int)attack; }
                foreach (var f in stats.GetArray("feats")) { AddFeature(f.StringValue); }
            }

            var traits = stats.Get("traits");
            if (traits.Kind == JsonKind.Object && traits.ObjectValue != null)
            {
                foreach (var kv in traits.ObjectValue)
                {
                    string value = kv.Value.Kind == JsonKind.String ? kv.Value.StringValue : string.Empty;
                    AddFeature(value.Length > 0 && value.Length <= 40 ? Title(kv.Key) + ": " + value : Title(kv.Key));
                }
            }

            foreach (var effect in stats.GetArray("statusEffects"))
            {
                AddCondition(effect.GetString("conditionName", effect.GetString("name", string.Empty)));
            }

            var pools = stats.Get("resourcePools");
            if (pools.Kind == JsonKind.Object && pools.ObjectValue != null)
            {
                foreach (var kv in pools.ObjectValue)
                {
                    int max = (int)kv.Value.GetNumber("max", 0);
                    if (max <= 0 || kv.Key == "gold") { continue; }
                    Resources.Add(new Resource { Key = kv.Key, Name = PoolName(kv.Key, System), Current = (int)kv.Value.GetNumber("current", max), Max = max });
                }
                Resources.Sort(delegate (Resource a, Resource b) { return PoolOrder(a.Key).CompareTo(PoolOrder(b.Key)); });
            }

            Mood = MoodWord(stats.GetNumber("morale", -1), stats.GetNumber("willpower", -1));
            double temperature = stats.GetNumber("temperature", double.NaN);
            if (!double.IsNaN(temperature))
            {
                string felt = TemperatureWord((float)temperature);
                if (felt.Length > 0) { Needs.Add(new Gauge { Name = "Warmth", Value = (float)temperature, Word = felt, Severity = temperature < 0 || temperature > 35 ? 2 : 1 }); }
            }
        }

        /// <summary>
        /// PF2e sheets carry spellcasting defaults even for a fighter; a caster's class gives it spell slot
        /// or focus pools, so those are the tell.
        /// </summary>
        private static bool HasCasterFeature(JsonValue stats)
        {
            var pools = stats.Get("resourcePools");
            if (pools.Kind != JsonKind.Object || pools.ObjectValue == null) { return false; }
            foreach (var kv in pools.ObjectValue)
            {
                if (kv.Key.StartsWith("spell_slots", StringComparison.Ordinal) || kv.Key.StartsWith("focus", StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        private void ReadRanked(string[][] table, JsonValue mods, JsonValue ranks, List<Check> into)
        {
            foreach (var row in table)
            {
                int listed;
                bool hasMod = TryGetInt(mods, row[0], out listed);
                int rank = RankOf(GetStringCI(ranks, row[0]));
                into.Add(new Check { Name = row[0], Ability = row[1], Mod = hasMod ? listed : ModOf(row[1]), Rank = rank });
            }
        }

        private int ModOf(string shortName)
        {
            foreach (var a in Abilities) { if (a.Short == shortName) { return a.Mod; } }
            return 0;
        }

        private void AddCondition(string name)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            foreach (string c in Conditions) { if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) { return; } }
            Conditions.Add(name);
        }

        private void AddFeature(string name)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            string pretty = name.IndexOf('_') >= 0 || name.IndexOf('-') >= 0 ? Title(name.Replace('_', ' ').Replace('-', ' ')) : name;
            if (!Features.Contains(pretty)) { Features.Add(pretty); }
        }

        private void ReadNeeds(JsonValue needs)
        {
            if (needs.Kind != JsonKind.Object || needs.ObjectValue == null) { return; }
            foreach (var kv in needs.ObjectValue)
            {
                if (kv.Value.Kind != JsonKind.Number) { continue; }
                float v = (float)kv.Value.NumberValue;
                string word = NeedWord(kv.Key, v);
                Needs.Add(new Gauge { Name = Title(kv.Key.Replace('_', ' ')), Value = v, Word = word, Severity = v >= 80 ? 2 : v >= 60 ? 1 : 0 });
            }
        }

        private static void ReadGear(List<JsonValue> rows, List<Gear> into)
        {
            foreach (var i in rows)
            {
                string name = i.GetString("name", string.Empty);
                if (name.Length == 0) { continue; }
                into.Add(new Gear
                {
                    Id = i.GetString("id", string.Empty),
                    Name = name,
                    Quantity = (int)i.GetNumber("quantity", 1),
                    Category = i.GetString("coreCategory", string.Empty),
                    Detail = i.GetString("description", string.Empty),
                });
            }
        }

        // ------------------------------------------------------------------ words

        public static int Dnd5eMod(int score)
        {
            return (int)Math.Floor((score - 10) / 2.0);
        }

        public static string Signed(int value)
        {
            return value >= 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : "−" + (-value).ToString(CultureInfo.InvariantCulture);
        }

        public static string RankLetter(int rank)
        {
            switch (rank)
            {
                case 1: return "T";
                case 2: return "E";
                case 3: return "M";
                case 4: return "L";
                default: return "U";
            }
        }

        public static string RankName(int rank)
        {
            switch (rank)
            {
                case 1: return "Trained";
                case 2: return "Expert";
                case 3: return "Master";
                case 4: return "Legendary";
                default: return "Untrained";
            }
        }

        private static int RankOf(string rank)
        {
            switch ((rank ?? string.Empty).ToLowerInvariant())
            {
                case "trained": return 1;
                case "expert": return 2;
                case "master": return 3;
                case "legendary": return 4;
                default: return 0;
            }
        }

        /// <summary>"spell_slots_2" → "2nd-level slots" (5e) / "Rank 2 slots" (PF2e); "action_surge" → "Action Surge".</summary>
        public static string PoolName(string key, string system)
        {
            const string slots = "spell_slots_";
            int level;
            if (key.StartsWith(slots, StringComparison.Ordinal) && int.TryParse(key.Substring(slots.Length), out level))
            {
                return system == "pf2e" ? "Rank " + level + " slots" : Ordinal(level) + "-level slots";
            }
            return Title(key.Replace('_', ' '));
        }

        private static int PoolOrder(string key)
        {
            const string slots = "spell_slots_";
            int level;
            if (key.StartsWith(slots, StringComparison.Ordinal) && int.TryParse(key.Substring(slots.Length), out level)) { return level; }
            return 100;
        }

        private static string Ordinal(int n)
        {
            if (n % 100 >= 11 && n % 100 <= 13) { return n + "th"; }
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        private static string NeedWord(string need, float v)
        {
            string key = need.ToLowerInvariant();
            string[] words;
            switch (key)
            {
                case "hunger": words = new[] { "Fed", "Peckish", "Hungry", "Starving" }; break;
                case "thirst": words = new[] { "Watered", "Dry", "Thirsty", "Parched" }; break;
                case "tiredness": words = new[] { "Rested", "Tired", "Weary", "Exhausted" }; break;
                case "fatigue": words = new[] { "Fresh", "Worn", "Fatigued", "Spent" }; break;
                case "stress": words = new[] { "Calm", "Uneasy", "Strained", "Breaking" }; break;
                case "social_drive": words = new[] { "Content", "Restless", "Lonely", "Isolated" }; break;
                default: words = new[] { "Low", "Rising", "High", "Pressing" }; break;
            }
            int band = v >= 80 ? 3 : v >= 60 ? 2 : v >= 35 ? 1 : 0;
            return words[band];
        }

        public static string TemperatureWord(float celsius)
        {
            if (celsius < -10) { return "Freezing"; }
            if (celsius < 5) { return "Cold"; }
            if (celsius < 12) { return "Chilly"; }
            if (celsius <= 28) { return string.Empty; }
            if (celsius <= 35) { return "Hot"; }
            return "Sweltering";
        }

        private static string MoodWord(double morale, double willpower)
        {
            if (morale < 0 && willpower < 0) { return string.Empty; }
            double m = morale < 0 ? 65 : morale;
            double w = willpower < 0 ? 75 : willpower;
            double low = Math.Min(m, w);
            if (low < 20) { return "Broken"; }
            if (low < 40) { return "Shaken"; }
            if (m >= 80 && w >= 60) { return "Bold"; }
            return "Steady";
        }

        public static string Title(string text)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }
            var chars = text.ToCharArray();
            bool start = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (start && char.IsLetter(chars[i])) { chars[i] = char.ToUpperInvariant(chars[i]); }
                start = chars[i] == ' ' || chars[i] == '-' || chars[i] == '/';
            }
            return new string(chars);
        }

        private static bool TryGetInt(JsonValue obj, string key, out int value)
        {
            value = 0;
            if (obj.Kind != JsonKind.Object || obj.ObjectValue == null) { return false; }
            foreach (var kv in obj.ObjectValue)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value.Kind == JsonKind.Number)
                {
                    value = (int)kv.Value.NumberValue;
                    return true;
                }
            }
            return false;
        }

        private static string GetStringCI(JsonValue obj, string key)
        {
            if (obj.Kind != JsonKind.Object || obj.ObjectValue == null) { return string.Empty; }
            foreach (var kv in obj.ObjectValue)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value.Kind == JsonKind.String) { return kv.Value.StringValue; }
            }
            return string.Empty;
        }
    }
}
