using System.IO;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// A profile naming another as its storyteller: one hop, by stable id, falling back to itself when the reference is empty or
    /// gone, and checked before a turn starts. The routing itself is in DriverFakeProviderTests.
    /// </summary>
    public class NarratorProfileTests
    {
        private GameObject _go;
        private ByokSettings _byok;
        private string _file;

        [SetUp]
        public void SetUp()
        {
            // The player's real provider profiles are never read or written.
            _file = Path.Combine(Path.GetTempPath(), "vault-providers-" + System.Guid.NewGuid().ToString("N") + ".json");
            ByokSettings.PathOverride = _file;
            _go = new GameObject("NarratorProfileTests");
            _byok = _go.AddComponent<ByokSettings>();
            _byok.Profiles.Clear();
            _byok.Profiles.Add(Profile("Rules", "rules-model"));
            _byok.Profiles.Add(Profile("Prose", "prose-model"));
            _byok.Profiles.Add(Profile("Spare", "spare-model"));
            _byok.ActiveIndex = 0;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            ByokSettings.PathOverride = null;
            if (File.Exists(_file)) { File.Delete(_file); }
        }

        private static ProviderProfile Profile(string name, string model)
        {
            // Ollama: keyless, so validity turns only on what each test changes.
            return new ProviderProfile { Name = name, Preset = "ollama", BaseUrl = "http://localhost:11434/v1", Model = model };
        }

        private ProviderProfile Rules { get { return _byok.Profiles[0]; } }
        private ProviderProfile Prose { get { return _byok.Profiles[1]; } }
        private ProviderProfile Spare { get { return _byok.Profiles[2]; } }

        [Test]
        public void NoNarrator_TheProfileNarratesItself()
        {
            Assert.AreSame(Rules, _byok.Narrator);
            Assert.IsFalse(_byok.HasSeparateNarrator);
        }

        [Test]
        public void ANamedProfile_WritesTheScenes()
        {
            Rules.Narrator = ByokSettings.IdOf(Prose);
            Assert.AreSame(Prose, _byok.Narrator);
            Assert.IsTrue(_byok.HasSeparateNarrator);
        }

        [Test]
        public void ADanglingReference_FallsBackToTheProfileItself()
        {
            Rules.Narrator = "no-such-profile";
            Assert.AreSame(Rules, _byok.Narrator, "a turn with a deleted storyteller still gets told");
            Assert.IsFalse(_byok.HasSeparateNarrator);
        }

        [Test]
        public void References_NeverChain()
        {
            Rules.Narrator = ByokSettings.IdOf(Prose);
            Prose.Narrator = ByokSettings.IdOf(Spare);
            Assert.AreSame(Prose, _byok.Narrator, "only the active profile's own choice counts");
        }

        [Test]
        public void SinglePass_HasNoSeparateNarrator()
        {
            Rules.Narrator = ByokSettings.IdOf(Prose);
            Rules.SinglePass = true;
            Assert.IsFalse(_byok.HasSeparateNarrator, "one call does everything, so there is no storyteller call to route");
        }

        [Test]
        public void Ids_AreAssignedOnce_AndNewProfilesGetOne()
        {
            Assert.AreEqual(string.Empty, Spare.Id, "a profile from an older file has none yet");
            string id = ByokSettings.IdOf(Spare);
            Assert.IsNotEmpty(id);
            Assert.AreEqual(id, ByokSettings.IdOf(Spare), "stable");
            Assert.AreNotEqual(id, ByokSettings.IdOf(Prose));
            var added = _byok.AddProfile("openai");
            Assert.IsNotEmpty(added.Id);
        }

        [Test]
        public void DeletingTheNarrator_ClearsTheReferencesToIt()
        {
            ProviderProfile rules = Rules, spare = Spare;
            rules.Narrator = ByokSettings.IdOf(Prose);
            spare.Narrator = Prose.Id;
            _byok.ActiveIndex = 1;
            _byok.DeleteActive();
            Assert.AreEqual(2, _byok.Profiles.Count);
            Assert.AreEqual(string.Empty, rules.Narrator);
            Assert.AreEqual(string.Empty, spare.Narrator);
        }

        [Test]
        public void Validate_ChecksTheStoryteller_AndNamesIt()
        {
            Rules.Narrator = ByokSettings.IdOf(Prose);
            Prose.Preset = "openai"; // needs a key, has none
            string reason;
            Assert.IsFalse(_byok.Validate(out reason), "a storyteller that can't be reached would fail only after the loop committed the turn");
            StringAssert.StartsWith("Storyteller profile \"Prose\": ", reason);
            StringAssert.Contains("No API key", reason);

            Assert.IsTrue(ByokSettings.ValidateProfile(Rules, out reason), "the rules profile on its own is fine");
            Rules.SinglePass = true;
            Assert.IsTrue(_byok.Validate(out reason), "single-pass never calls the storyteller");
        }

        [Test]
        public void SaveAndLoad_KeepTheIdAndTheReference()
        {
            Rules.Narrator = ByokSettings.IdOf(Prose);
            _byok.Save();
            _byok.Load();
            Assert.AreEqual(3, _byok.Profiles.Count);
            Assert.AreEqual("prose-model", _byok.Narrator.Model);
            Assert.AreSame(_byok.Profiles[1], _byok.Narrator);
        }

        [Test]
        public void AFileFromBeforeNarrators_LoadsWithEveryProfileNarratingItself()
        {
            File.WriteAllText(_file, "{\"Active\":0,\"Profiles\":[{\"Name\":\"Old\",\"Preset\":\"ollama\",\"BaseUrl\":\"http://localhost:11434/v1\",\"Model\":\"llama3.1\"}]}");
            _byok.Load();
            Assert.AreEqual("Old", _byok.Active.Name);
            Assert.AreEqual(string.Empty, _byok.Active.Narrator ?? string.Empty);
            Assert.AreSame(_byok.Active, _byok.Narrator);
            string reason;
            Assert.IsTrue(_byok.Validate(out reason), reason);
        }
    }
}
