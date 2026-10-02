using System.Linq;
using NUnit.Framework;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>N7: the sheet reads get_entity's real payload (shapes taken from the n7 fixture dumps).</summary>
    public class CharacterSheetTests
    {
        private const string Dnd5eFighter = @"{
  ""character"": {
    ""id"": ""chars/aric"", ""name"": ""Aric Thorne"", ""classLevel"": ""Fighter 5"", ""currentHp"": 33, ""maxHp"": 44,
    ""currentAppearance"": ""A broad soldier in dented mail."", ""isPc"": true, ""isPartyCompanion"": false,
    ""currentLocationId"": ""locations/copper-ferry"", ""currentActivity"": ""Watching the far bank"",
    ""needs"": { ""activeNeeds"": { ""hunger"": 25, ""thirst"": 65, ""tiredness"": 85 } },
    ""systemStats"": {
      ""$system"": ""dnd5e"", ""armorClass"": 18, ""strength"": 16, ""dexterity"": 14, ""constitution"": 15,
      ""intelligence"": 10, ""wisdom"": 12, ""charisma"": 8,
      ""skillModifiers"": { ""Athletics"": 6, ""Intimidation"": 2 },
      ""savingThrowModifiers"": { ""Strength"": 6, ""Constitution"": 5 },
      ""hitDie"": ""d10"", ""level"": 5, ""classLevels"": [ { ""class"": ""Fighter"", ""level"": 5 } ],
      ""race"": ""Human"", ""background"": ""Soldier"", ""movement"": 30,
      ""willpower"": 75, ""morale"": 30, ""temperature"": 2,
      ""attributes"": { ""morale"": 70, ""proficiencyBonus"": 3, ""passivePerception"": 11 },
      ""statusEffects"": [ { ""name"": ""Poisoned"", ""category"": ""Legacy"" } ],
      ""resourcePools"": { ""second_wind"": { ""current"": 1, ""max"": 1 }, ""action_surge"": { ""current"": 0, ""max"": 1 }, ""gold"": { ""current"": 40, ""max"": 999 } }
    }
  },
  ""recentInteractions"": [ { ""summary"": ""Bandits ambushed the party at dusk."", ""category"": ""Combat"", ""dayLogged"": 0 } ],
  ""equipped"": [ { ""id"": ""items/aric-mail"", ""name"": ""Chain Mail"", ""quantity"": 1, ""coreCategory"": ""Armor"", ""isEquipped"": true } ],
  ""carried"": [ { ""id"": ""items/aric-potion"", ""name"": ""Potion of Healing"", ""quantity"": 2, ""coreCategory"": ""Other"" } ]
}";

        private const string Pf2eWizard = @"{
  ""character"": {
    ""id"": ""chars/liesl"", ""name"": ""Liesl Varn"", ""classLevel"": ""Wizard 4"", ""currentHp"": 23, ""maxHp"": 34, ""isPc"": true,
    ""systemStats"": {
      ""$system"": ""pf2e"", ""armorClass"": 18, ""strengthMod"": 0, ""dexterityMod"": 2, ""constitutionMod"": 1,
      ""intelligenceMod"": 4, ""wisdomMod"": 1, ""charismaMod"": 0,
      ""skillModifiers"": { ""arcana"": 12, ""society"": 10 },
      ""savingThrowModifiers"": { ""fortitude"": 7, ""reflex"": 8, ""will"": 9 },
      ""skillProficiencies"": { ""arcana"": ""Expert"", ""society"": ""Trained"" },
      ""saveProficiencies"": { ""fortitude"": ""Trained"", ""reflex"": ""Trained"", ""will"": ""Expert"" },
      ""level"": 4, ""ancestry"": ""Elf"", ""heritage"": ""Ancient Elf"", ""background"": ""Scholar"",
      ""spellcastingAbility"": ""intelligence"", ""spellDc"": 20, ""spellcastingProficiency"": ""Trained"",
      ""classFeats"": [ ""reach_spell"" ],
      ""resourcePools"": { ""spell_slots_2"": { ""current"": 2, ""max"": 3 }, ""spell_slots_1"": { ""current"": 3, ""max"": 3 }, ""focus_points"": { ""current"": 1, ""max"": 1 } }
    }
  }
}";

        private const string Pf2eWolfCompanion = @"{ ""character"": { ""id"": ""chars/ash"", ""name"": ""Ash"", ""isPartyCompanion"": true, ""maxHp"": 24, ""currentHp"": 24,
  ""systemStats"": { ""$system"": ""pf2e"", ""level"": 1, ""statBlockHp"": 24, ""strengthMod"": 3, ""dexterityMod"": 4,
    ""savingThrowModifiers"": { ""Fortitude"": 8, ""Reflex"": 10, ""Will"": 6 },
    ""skillModifiers"": { ""Perception"": 8, ""Stealth"": 9, ""Warfare Lore"": 4 } } } }";

        private const string Pf2eFighterWithStraySpellcasting = @"{ ""character"": { ""id"": ""chars/brakk"", ""name"": ""Brakk"", ""isPartyCompanion"": true,
  ""systemStats"": { ""$system"": ""pf2e"", ""level"": 3, ""ancestry"": ""Orc"", ""spellcastingAbility"": ""Wisdom"", ""spellDc"": 16, ""spellcastingProficiency"": ""Trained"" } } }";

        private static CharacterSheet Sheet(string json)
        {
            return CharacterSheet.FromPayload(JsonValue.Parse(json));
        }

        private const string NarrativeWren = @"{ ""character"": { ""id"": ""chars/wren"", ""name"": ""Wren Hollis"", ""isPc"": true,
  ""currentAppearance"": ""Tar-black hands."",
  ""psychology"": { ""traits"": [ ""wry"", ""restless"", ""loyal to a fault"" ], ""wants"": [ ""Find her brother"" ], ""fears"": [ ""Deep water"", ""being forgotten"" ] },
  ""systemStats"": { ""$system"": ""narrative"" } } }";

        [Test]
        public void Narrative_HasNoStats_AndShowsItsNatureDrivesAndFears_OnBothSheets()
        {
            var s = Sheet(NarrativeWren);

            Assert.IsFalse(s.HasStats);
            CollectionAssert.AreEqual(new[] { "Wry, restless, loyal to a fault.", "Drives: Find her brother.", "Fears: Deep water; being forgotten." }, s.NatureLines());

            var sheet = new SheetViewModel(s);
            CollectionAssert.AreEqual(s.NatureLines(), sheet.Nature);
            CollectionAssert.IsEmpty(sheet.Abilities);
            CollectionAssert.IsEmpty(sheet.Vitals);

            var card = new StatBlockViewModel(s);
            Assert.AreEqual("Nature", card.Sections[0].Title);
            CollectionAssert.AreEqual(s.NatureLines(), card.Sections[0].Paragraphs.Select(p => p.Text).ToArray());
        }

        private const string Dnd5eHunter = @"{
  ""character"": { ""id"": ""chars/rook"", ""name"": ""Rook"", ""classLevel"": ""Ranger 3"", ""isPc"": true,
    ""systemStats"": { ""$system"": ""dnd5e"", ""level"": 3, ""classLevels"": [ { ""class"": ""Ranger"", ""level"": 3 } ] } },
  ""classFeatures"": [
    { ""level"": 1, ""name"": ""Favored Enemy"", ""description"": ""Advantage on Survival checks to track your chosen enemy."", ""from"": null, ""spells"": [] },
    { ""level"": 3, ""name"": ""Hunter's Prey"", ""description"": ""Choose one."", ""from"": ""Hunter"", ""spells"": [] },
    { ""level"": 1, ""name"": ""Domain Spells"", ""description"": ""Always prepared."", ""from"": ""Life Domain"", ""spells"": [ ""cure_wounds"", ""bless"" ] }
  ]
}";

        [Test]
        public void ClassFeatures_AreReadFromThePayload_AndShownOneLineEach()
        {
            var s = Sheet(Dnd5eHunter);

            Assert.AreEqual(3, s.ClassFeatures.Count);
            Assert.AreEqual("Hunter", s.ClassFeatures[1].From);
            var sheet = new SheetViewModel(s);
            Assert.AreEqual("Hunter's Prey (Hunter, level 3). Choose one.", sheet.ClassFeatures[1]);
            Assert.AreEqual("Favored Enemy (level 1). Advantage on Survival checks to track your chosen enemy.", sheet.ClassFeatures[0]);
            StringAssert.EndsWith("Spells: Cure Wounds, Bless.", sheet.ClassFeatures[2]);
            var card = new StatBlockViewModel(s);
            Assert.IsTrue(card.Sections.Any(x => x.Title == "Class features" && x.Paragraphs.Count == 3));
        }

        [Test]
        public void ACharacterWithoutPsychology_HasNoNatureSection()
        {
            Assert.IsEmpty(Sheet(Pf2eWizard).NatureLines());
            CollectionAssert.IsEmpty(new SheetViewModel(Sheet(Pf2eWizard)).Nature);
        }

        [Test]
        public void Dnd5e_IdentityVitalsAndAbilities()
        {
            var s = Sheet(Dnd5eFighter);
            Assert.AreEqual("dnd5e", s.System);
            Assert.AreEqual("Human", s.Lineage);
            Assert.AreEqual("Fighter 5", s.ClassLine);
            Assert.AreEqual("Soldier", s.Background);
            Assert.AreEqual(18, s.ArmorClass);
            Assert.AreEqual("30 ft.", s.Speed);
            Assert.AreEqual(3, s.ProficiencyBonus);
            Assert.AreEqual(11, s.PassivePerception);
            Assert.AreEqual(new[] { "STR", "DEX", "CON", "INT", "WIS", "CHA" }, s.Abilities.Select(a => a.Short).ToArray());
            Assert.AreEqual(16, s.Abilities[0].Score);
            Assert.AreEqual(3, s.Abilities[0].Mod);
            Assert.AreEqual(-1, s.Abilities[5].Mod);
            Assert.AreEqual(-1, s.SpellDc, "a fighter has no spell DC");
        }

        [Test]
        public void Dnd5e_SavesAndSkills_ListEverything_MarkProficient()
        {
            var s = Sheet(Dnd5eFighter);
            Assert.AreEqual(6, s.Saves.Count);
            var str = s.Saves.First(c => c.Name == "Strength");
            Assert.AreEqual(1, str.Rank);
            Assert.AreEqual(6, str.Mod);
            var dex = s.Saves.First(c => c.Name == "Dexterity");
            Assert.AreEqual(0, dex.Rank);
            Assert.AreEqual(2, dex.Mod, "unproficient save = ability modifier");
            Assert.AreEqual(18, s.Skills.Count);
            Assert.AreEqual(6, s.Skills.First(c => c.Name == "Athletics").Mod);
            Assert.AreEqual(1, s.Skills.First(c => c.Name == "Athletics").Rank);
            Assert.AreEqual(1, s.Skills.First(c => c.Name == "Perception").Mod);
            Assert.AreEqual(0, s.Skills.First(c => c.Name == "Perception").Rank);
        }

        [Test]
        public void Dnd5e_ConditionsResourcesGearAndWords()
        {
            var s = Sheet(Dnd5eFighter);
            CollectionAssert.AreEqual(new[] { "Poisoned" }, s.Conditions);
            // Gold is money, not a resource to spend at the table.
            CollectionAssert.AreEquivalent(new[] { "Second Wind", "Action Surge" }, s.Resources.Select(r => r.Name).ToArray());
            Assert.AreEqual("Chain Mail", s.Equipped.Single().Name);
            Assert.AreEqual(2, s.Carried.Single().Quantity);
            Assert.AreEqual("Bandits ambushed the party at dusk.", s.RecentEvents.Single().Summary);
            Assert.AreEqual("Shaken", s.Mood, "systemStats morale 30 (the engine value), not the attribute");
            Assert.AreEqual("Thirsty", s.Needs.First(n => n.Name == "Thirst").Word);
            Assert.AreEqual(2, s.Needs.First(n => n.Name == "Tiredness").Severity);
            Assert.AreEqual("Cold", s.Needs.First(n => n.Name == "Warmth").Word);
        }

        [Test]
        public void Pf2e_AStatBlockCreature_ListsItsModifiersWithoutRanks_PerceptionAndLore()
        {
            var s = Sheet(Pf2eWolfCompanion);
            Assert.AreEqual(8, s.Perception);
            Assert.AreEqual(new[] { "Fortitude", "Reflex", "Will" }, s.Saves.Where(c => c.Rank > 0).Select(c => c.Name).ToArray());
            Assert.AreEqual(10, s.Saves[1].Mod);
            Assert.AreEqual(new[] { "Stealth", "Warfare Lore" }, s.Skills.Where(c => c.Rank > 0).Select(c => c.Name).ToArray());

            var card = new StatBlockViewModel(s);
            var lines = card.Lines.Select(l => l.Key + ": " + l.Value).ToArray();
            Assert.AreEqual("Perception: +8", lines[0]);
            CollectionAssert.Contains(lines, "Saving Throws: Fortitude +8, Reflex +10, Will +6");
            CollectionAssert.Contains(lines, "Skills: Stealth +9, Warfare Lore +4");
        }

        [Test]
        public void Pf2e_RanksSpellcastingPoolsAndFeats()
        {
            var s = Sheet(Pf2eWizard);
            Assert.IsTrue(s.IsPf2e);
            Assert.AreEqual("Ancient Elf", s.Lineage, "a heritage that names the ancestry isn't doubled");
            Assert.AreEqual("Rock Dwarf", CharacterSheet.Named("rock_dwarf"), "template names read as titles");
            Assert.AreEqual("Martial Disciple", CharacterSheet.Named("martial_disciple"));
            Assert.AreEqual("Wizard 4", s.ClassLine);
            Assert.AreEqual(-1, s.Abilities[0].Score, "PF2e has modifiers only");
            Assert.AreEqual(4, s.Abilities[3].Mod);
            Assert.AreEqual(new[] { "Fortitude", "Reflex", "Will" }, s.Saves.Select(c => c.Name).ToArray());
            Assert.AreEqual(2, s.Saves[2].Rank);
            Assert.AreEqual(9, s.Saves[2].Mod);
            Assert.AreEqual(16, s.Skills.Count);
            Assert.AreEqual(2, s.Skills.First(c => c.Name == "Arcana").Rank);
            Assert.AreEqual(0, s.Skills.First(c => c.Name == "Thievery").Rank);
            Assert.AreEqual(2, s.Skills.First(c => c.Name == "Thievery").Mod);
            Assert.AreEqual(20, s.SpellDc);
            Assert.AreEqual(new[] { "Rank 1 slots", "Rank 2 slots", "Focus Points" }, s.Resources.Select(r => r.Name).ToArray());
            CollectionAssert.Contains(s.Features, "Reach Spell");
        }

        [Test]
        public void Pf2e_NonCaster_HidesDefaultSpellcasting()
        {
            var s = Sheet(Pf2eFighterWithStraySpellcasting);
            Assert.AreEqual(-1, s.SpellDc);
            Assert.AreEqual(string.Empty, s.SpellAbility);
            Assert.IsTrue(s.IsCompanion);
        }

        [Test]
        public void NarrativeOrBareEntity_StillReads()
        {
            var s = Sheet(@"{ ""id"": ""chars/tam"", ""name"": ""Old Tam"", ""classLevel"": ""Ferryman"" }");
            Assert.IsFalse(s.HasStats);
            Assert.AreEqual("Old Tam", s.Name);
            Assert.AreEqual("Ferryman", s.ClassLine);
            Assert.AreEqual(0, s.Abilities.Count);
        }

        [Test]
        public void Words()
        {
            Assert.AreEqual("+3", CharacterSheet.Signed(3));
            Assert.AreEqual("+0", CharacterSheet.Signed(0));
            Assert.AreEqual("−2", CharacterSheet.Signed(-2));
            Assert.AreEqual("3rd-level slots", CharacterSheet.PoolName("spell_slots_3", "dnd5e"));
            Assert.AreEqual("Channel Divinity", CharacterSheet.PoolName("channel_divinity", "dnd5e"));
            Assert.AreEqual(-1, CharacterSheet.Dnd5eMod(8));
            Assert.AreEqual(string.Empty, CharacterSheet.TemperatureWord(20));
        }
    }
}
