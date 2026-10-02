using System.Collections.Generic;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>"DM drafts companions": the one call's prompt and the reading of its reply. Nothing is fixed silently.</summary>
    public class PartyDrafterTests
    {
        private static StatBlockSchema Schema()
        {
            return StatBlockSchema.Parse(JsonValue.Parse("{\"name\":\"companion\",\"fields\":["
                + "{\"key\":\"statBlockHp\",\"label\":\"Hit points\",\"type\":\"int\",\"min\":1,\"max\":500,\"required\":true},"
                + "{\"key\":\"armorClass\",\"label\":\"Armor class\",\"type\":\"int\",\"min\":5,\"max\":25},"
                + "{\"key\":\"skillModifiers\",\"label\":\"Skills\",\"type\":\"modifiers\",\"min\":-5,\"max\":20,\"keys\":[\"Athletics\",\"Perception\",\"Sleight of Hand\",\"Stealth\"]},"
                + "{\"key\":\"attacks\",\"label\":\"Attacks\",\"type\":\"rows\",\"item\":\"attack\",\"max\":6,\"columns\":["
                + "{\"key\":\"name\",\"label\":\"Attack\",\"required\":true},{\"key\":\"bonus\",\"label\":\"To hit\",\"type\":\"int\",\"min\":-5,\"max\":20,\"required\":true},"
                + "{\"key\":\"damage\",\"label\":\"Damage\",\"type\":\"dice\",\"required\":true},{\"key\":\"notes\",\"label\":\"Notes\"}]},"
                + "{\"key\":\"stance\",\"label\":\"Stance toward the party\",\"type\":\"text\"}]}"));
        }

        private static List<DraftedCompanion> Read(string reply, int partyLevel = 2)
        {
            var drafts = new List<DraftedCompanion>();
            string error;
            Assert.IsTrue(PartyDrafter.Parse(reply, Schema(), partyLevel, VaultController.MaxBuilderLevelFor("pf2e"), drafts, out error), error);
            return drafts;
        }

        [Test]
        public void Parse_ReadsEachCompanion_AsAStatBlockDraft()
        {
            var drafts = Read("[{\"name\":\"Brann\",\"concept\":\"Aric's old shield-brother.\",\"look\":\"Grey beard.\",\"level\":2,"
                + "\"statblock\":{\"statBlockHp\":16,\"armorClass\":\"16\",\"attacks\":\"Longsword +3, 1d8+1\",\"stance\":\"Loyal\"}}]");

            Assert.AreEqual(1, drafts.Count);
            var d = drafts[0].Draft;
            Assert.AreEqual("companion", d.Kind);
            Assert.AreEqual("Brann", d.Name);
            Assert.AreEqual("Aric's old shield-brother.", d.Concept);
            Assert.AreEqual("Grey beard.", d.Look);
            Assert.AreEqual(2, d.Level);
            Assert.AreEqual(2, d.PartyLevel, "the server's power check compares against it");
            var block = d.Get(PartyDrafter.StatBlockKey);
            Assert.AreEqual(16, (int)block.Get("statBlockHp").NumberValue);
            Assert.AreEqual(JsonKind.Number, block.Get("armorClass").Kind, "a number field written as a string still reads as a number");
            Assert.AreEqual("Loyal", block.GetString("stance", string.Empty));
            Assert.IsEmpty(drafts[0].Notes);
        }

        [Test]
        public void Parse_Skills_AreAnObjectInTheFieldsSpelling_FromAnObjectOrText()
        {
            var drafts = Read("[{\"name\":\"Wren\",\"level\":2,\"statblock\":{\"statBlockHp\":9,\"skillModifiers\":{\"perception\":\"+5\",\"sleight of hand\":4,\"Flying\":\"lots\"}}},"
                + "{\"name\":\"Tam\",\"level\":2,\"statblock\":{\"statBlockHp\":9,\"skillModifiers\":\"Stealth +6, athletics -1\"}}]");

            var wren = drafts[0].Draft.Get(PartyDrafter.StatBlockKey).Get("skillModifiers");
            Assert.AreEqual(JsonKind.Object, wren.Kind);
            Assert.AreEqual(5, (int)wren.Get("Perception").NumberValue);
            Assert.AreEqual(4, (int)wren.Get("Sleight of Hand").NumberValue);
            Assert.AreEqual("lots", wren.GetString("Flying", string.Empty), "an unknown skill and a non-number stay for the preview to flag");
            var tam = drafts[1].Draft.Get(PartyDrafter.StatBlockKey).Get("skillModifiers");
            Assert.AreEqual(6, (int)tam.Get("Stealth").NumberValue);
            Assert.AreEqual(-1, (int)tam.Get("Athletics").NumberValue);
        }

        [Test]
        public void Parse_Attacks_AreRows_FromAListOrTheTextForm()
        {
            var drafts = Read("[{\"name\":\"Wren\",\"level\":2,\"statblock\":{\"statBlockHp\":9,\"attacks\":[{\"name\":\"Shortbow\",\"bonus\":\"+5\",\"damage\":\"1d6+3 piercing\",\"notes\":\"range 80/320 ft.\"}]}},"
                + "{\"name\":\"Tam\",\"level\":2,\"statblock\":{\"statBlockHp\":9,\"attacks\":\"Longsword +3, 1d8+1 slashing; Shield bash +3, 1d4 bludgeoning\"}}]");

            var wren = drafts[0].Draft.Get(PartyDrafter.StatBlockKey).Get("attacks");
            Assert.AreEqual(JsonKind.Array, wren.Kind);
            Assert.AreEqual(5, (int)wren.ArrayValue[0].Get("bonus").NumberValue, "\"+5\" in a whole-number column is 5");
            Assert.AreEqual("range 80/320 ft.", wren.ArrayValue[0].GetString("notes", string.Empty));
            var tam = drafts[1].Draft.Get(PartyDrafter.StatBlockKey).Get("attacks");
            Assert.AreEqual(2, tam.ArrayValue.Count);
            Assert.AreEqual("Shield bash", tam.ArrayValue[1].GetString("name", string.Empty));
            Assert.AreEqual("1d4 bludgeoning", tam.ArrayValue[1].GetString("damage", string.Empty));

            string prompt = PartyDrafter.SystemPrompt("dnd5e", Schema(), new List<BuilderOption>(), new List<PartyMember>(), 2, 3, new Dictionary<string, string>());
            StringAssert.Contains("attacks: Attacks, a list of up to 6 objects with keys name (text, required), bonus (whole number -5 to 20, required), damage (dice then damage type", prompt);
        }

        [Test]
        public void TemplateSkills_ReadAsAnObject_AndAPartWithNoNumberKeepsItsText()
        {
            var field = Schema().Fields.Find(delegate (StatBlockField f) { return f.Key == "skillModifiers"; });
            var value = StatModifiers.FromText(field, "Nature +4, perception +5; Keen senses");
            Assert.AreEqual(4, (int)value.Get("Nature").NumberValue);
            Assert.AreEqual(5, (int)value.Get("Perception").NumberValue);
            Assert.AreEqual("Keen senses", value.GetString("Keen senses", string.Empty));
            Assert.AreEqual("+5", StatModifiers.Signed(5));
            Assert.AreEqual("-1", StatModifiers.Signed(-1));
        }

        [Test]
        public void Parse_AcceptsACodeFenceAndProseAroundTheList()
        {
            var drafts = Read("Here they are:\n```json\n[{\"name\":\"Pip\",\"level\":2,\"statblock\":{\"statBlockHp\":5}}]\n```");
            Assert.AreEqual("Pip", drafts[0].Draft.Name);
        }

        [Test]
        public void Parse_ALevelOutsideTheBand_IsMovedIntoIt_AndSaysSo()
        {
            var drafts = Read("[{\"name\":\"Old Wyrm\",\"level\":9,\"statblock\":{\"statBlockHp\":5}},{\"name\":\"Pup\",\"level\":0,\"statblock\":{\"statBlockHp\":3}}]", partyLevel: 2);
            Assert.AreEqual(3, drafts[0].Draft.Level);
            Assert.AreEqual("Drafted at level 9, set to 3.", drafts[0].Notes[0]);
            Assert.AreEqual(1, drafts[1].Draft.Level);
            Assert.AreEqual("Drafted at level 0, set to 1.", drafts[1].Notes[0]);
        }

        [Test]
        public void Parse_AKeyTheStatBlockDoesNotHave_IsDroppedVisibly_AndABadNumberIsLeftForThePreview()
        {
            var drafts = Read("[{\"name\":\"Kestrel\",\"level\":2,\"statblock\":{\"statBlockHp\":4,\"wingspan\":\"3 ft\",\"armorClass\":\"tough\"}}]");
            var block = drafts[0].Draft.Get(PartyDrafter.StatBlockKey);
            Assert.AreEqual(JsonKind.Null, block.Get("wingspan").Kind);
            CollectionAssert.Contains(drafts[0].Notes, "Dropped 'wingspan': not a stat block field.");
            Assert.AreEqual("tough", block.GetString("armorClass", string.Empty), "the server's preview flags it on the field the player can edit");
        }

        [Test]
        public void Parse_KeepsAtMostFour_AndSaysHowManyWereLeftOut()
        {
            var items = new List<string>();
            for (int i = 0; i < 6; i++) { items.Add("{\"name\":\"N" + i + "\",\"level\":1,\"statblock\":{\"statBlockHp\":5}}"); }
            var drafts = Read("[" + string.Join(",", items.ToArray()) + "]");
            Assert.AreEqual(PartyDrafter.MaxCompanions, drafts.Count);
            CollectionAssert.Contains(drafts[3].Notes, "2 more in the draft left out (at most 4).");
        }

        [Test]
        public void Parse_NotJsonOrEmpty_IsAnError_NotAnEmptyParty()
        {
            var drafts = new List<DraftedCompanion>();
            string error;
            Assert.IsFalse(PartyDrafter.Parse("Sure! Brann the fighter and Pip the hawk.", Schema(), 1, 3, drafts, out error));
            StringAssert.Contains("JSON", error);
            Assert.IsFalse(PartyDrafter.Parse("[]", Schema(), 1, 3, drafts, out error));
            StringAssert.Contains("no companions", error);
            Assert.IsEmpty(drafts);
        }

        [Test]
        public void Prompt_NamesThePlayersCharacters_TheLevelBand_TheFields_AndTheTemplates()
        {
            var party = new List<PartyMember>
            {
                new PartyMember { Id = "chars/aric", Name = "Aric Thorne", ClassLine = "Fighter 2", Level = 2, Draft = new CharacterDraft { Concept = "A town guard who stayed." } },
                new PartyMember { Id = "draft-1", Name = "Old Draft", Kind = "companion", Pending = true },
            };
            var mastiff = new BuilderOption { Id = "Mastiff", Label = "Mastiff" };
            mastiff.Values["statBlockHp"] = "5";
            string prompt = PartyDrafter.SystemPrompt("dnd5e", Schema(), new List<BuilderOption> { mastiff }, party, 2, 3,
                new Dictionary<string, string> { { "party_composition", "two players, we need a healer" } });

            StringAssert.Contains("Aric Thorne, Fighter 2: A town guard who stayed.", prompt);
            StringAssert.DoesNotContain("Old Draft", prompt);
            StringAssert.Contains("not player characters", prompt);
            StringAssert.Contains("shared history", prompt);
            StringAssert.Contains("from 1 to 3", prompt);
            StringAssert.Contains("statBlockHp: Hit points, whole number 1 to 500, required", prompt);
            StringAssert.Contains("skillModifiers: Skills, an object of name to whole-number bonus, like {\"Perception\": 4}, each -5 to 20, names from: Athletics, Perception, Sleight of Hand, Stealth", prompt);
            StringAssert.Contains("Mastiff: statBlockHp 5", prompt);
            StringAssert.Contains("we need a healer", prompt);
            StringAssert.Contains("ONLY a JSON array", PartyDrafter.Instruction());
        }
    }
}
