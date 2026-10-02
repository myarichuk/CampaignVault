using System;
using System.Collections.Generic;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// Whether a character can gain a level (the server's LevelUpStatus, from get_entity's "levelUp"). Ready: the
    /// campaign's XP rule says the level is earned. Possible: the menu can be opened (milestone campaigns are never
    /// ready by XP, so the player or the DM decides).
    /// </summary>
    public sealed class LevelUpStatus
    {
        public bool Possible;
        public bool Ready;
        public int Level;
        public int TargetLevel;
        public int Xp;
        /// <summary>The XP the next level needs; -1 when the campaign levels by milestone.</summary>
        public int XpNeeded = -1;

        public static LevelUpStatus Parse(JsonValue v)
        {
            if (v == null || v.Kind != JsonKind.Object) { return null; }
            return new LevelUpStatus
            {
                Possible = v.GetBool("possible", false),
                Ready = v.GetBool("ready", false),
                Level = (int)v.GetNumber("level", 0),
                TargetLevel = (int)v.GetNumber("targetLevel", 0),
                Xp = (int)v.GetNumber("xp", 0),
                XpNeeded = (int)v.GetNumber("xpNeeded", -1),
            };
        }

        /// <summary>"2,700 XP of 2,700 needed", or just the target level for a milestone campaign.</summary>
        public string XpLine
        {
            get
            {
                if (XpNeeded < 0) { return "Level " + TargetLevel + " by milestone: the story decides."; }
                return Xp.ToString("N0") + " XP of " + XpNeeded.ToString("N0") + " needed for level " + TargetLevel + ".";
            }
        }
    }

    /// <summary>
    /// What the next level offers a character (character_level_up, action options): what it gives, and its choices as the
    /// builder's level choice slots, so the menu is the builder's own cards. The picks are the builder's too
    /// (<see cref="LevelChoices"/>): slot id → option ids.
    /// </summary>
    public sealed class LevelUpOffer
    {
        public string CharacterId = string.Empty;
        public string Name = string.Empty;
        public string ClassName = string.Empty;
        public LevelUpStatus Status;
        /// <summary>"Second Wind (level 2). ..." — what the class gives at the new level.</summary>
        public readonly List<string> Features = new List<string>();
        public readonly List<LevelSlot> Slots = new List<LevelSlot>();
        /// <summary>Every slot's options; an option's group is its slot's id.</summary>
        public readonly List<BuilderOption> Options = new List<BuilderOption>();

        public static LevelUpOffer Parse(JsonValue v)
        {
            var offer = new LevelUpOffer
            {
                CharacterId = v.GetString("characterId", string.Empty),
                Name = v.GetString("name", string.Empty),
                ClassName = v.GetString("className", string.Empty),
                Status = LevelUpStatus.Parse(v.Get("status")),
            };
            foreach (var f in v.GetArray("features"))
            {
                string name = f.GetString("name", string.Empty);
                if (name.Length == 0) { continue; }
                string text = f.GetString("description", string.Empty);
                string from = f.GetString("from", string.Empty);
                offer.Features.Add(name + (from.Length > 0 ? " (" + from + ")" : string.Empty) + (text.Length > 0 ? ". " + text.TrimEnd('.') + "." : "."));
            }
            foreach (var s in v.GetArray("slots"))
            {
                var slot = LevelSlot.Parse(s);
                offer.Slots.Add(slot);
                foreach (var o in s.GetArray("options"))
                {
                    var option = BuilderOption.Parse(o);
                    option.Group = slot.Id;
                    offer.Options.Add(option);
                }
            }
            return offer;
        }

        public List<BuilderOption> OptionsOf(LevelSlot slot)
        {
            return Options.FindAll(delegate (BuilderOption o) { return o.Group == slot.Id; });
        }

        /// <summary>
        /// The slot has all it needs: a feat, or one ability (+2), or two (+1 each) for an improvement; its full count for
        /// a required choice; anything for one that is optional.
        /// </summary>
        public static bool IsComplete(LevelSlot slot, List<string> picked)
        {
            if (picked == null || picked.Count == 0) { return !slot.Required; }
            if (slot.IsAsi) { return picked.Count <= 2; }
            return picked.Count == Math.Max(1, slot.Picks) || (!slot.Required && picked.Count <= slot.Picks);
        }

        public bool CanApply(JsonValue choice)
        {
            var picks = LevelChoices.Picks(choice);
            foreach (var slot in Slots)
            {
                List<string> picked;
                if (!picks.TryGetValue(slot.Id, out picked)) { picked = null; }
                if (!IsComplete(slot, picked)) { return false; }
            }
            return true;
        }

        /// <summary>What still needs a pick, for the button's hint: "Level 4 · Ability Score Improvement".</summary>
        public string FirstMissing(JsonValue choice)
        {
            var picks = LevelChoices.Picks(choice);
            foreach (var slot in Slots)
            {
                List<string> picked;
                if (!picks.TryGetValue(slot.Id, out picked)) { picked = null; }
                if (!IsComplete(slot, picked)) { return slot.Title; }
            }
            return string.Empty;
        }

        /// <summary>The apply call's picks: slot id → a list of option ids (always a list; the server reads lists).</summary>
        public static JsonValue PicksArgument(JsonValue choice)
        {
            var result = JsonValue.NewObject();
            foreach (var kv in LevelChoices.Picks(choice)) { result.ObjectValue[kv.Key] = CharacterDraft.StringArray(kv.Value); }
            return result;
        }
    }
}
