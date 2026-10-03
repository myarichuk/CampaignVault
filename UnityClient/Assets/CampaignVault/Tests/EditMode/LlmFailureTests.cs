using NUnit.Framework;
using CampaignVault.UnityClient.AI;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Failure model: every status maps to a kind, a plain-words line and a copy-pastable block with no secrets in it.</summary>
    public class LlmFailureTests
    {
        private const string Key = "sk-live-abcdef1234567890";

        private static LlmFailure.Context Context(string body = "")
        {
            return new LlmFailure.Context
            {
                Endpoint = "https://api.example.test/v1/chat/completions?key=" + Key,
                Model = "gpt-x",
                RequestMeta = "gpt-x · 3 msgs · 120 chars · 0 tools",
                Attempts = 3,
                ApiKey = Key,
                Body = body,
            };
        }

        [TestCase(0L, "Request timeout contacting the chat endpoint", LlmFailureKind.Timeout, true)]
        [TestCase(0L, "Cannot connect to destination host contacting the chat endpoint", LlmFailureKind.Network, true)]
        [TestCase(200L, "Chat endpoint returned an empty stream.", LlmFailureKind.Overloaded, true)]
        [TestCase(200L, "Chat endpoint returned invalid JSON.", LlmFailureKind.Malformed, true)]
        [TestCase(401L, "HTTP 401 from chat endpoint", LlmFailureKind.Auth, false)]
        [TestCase(403L, "HTTP 403 from chat endpoint", LlmFailureKind.Auth, false)]
        [TestCase(404L, "HTTP 404 from chat endpoint", LlmFailureKind.ModelNotFound, false)]
        [TestCase(429L, "HTTP 429 from chat endpoint", LlmFailureKind.RateLimit, true)]
        [TestCase(503L, "HTTP 503 from chat endpoint", LlmFailureKind.Overloaded, true)]
        [TestCase(400L, "HTTP 400 from chat endpoint", LlmFailureKind.BadRequest, true)]
        public void From_ClassifiesEveryStatus(long status, string raw, LlmFailureKind kind, bool retryable)
        {
            var failure = LlmFailure.From(status, raw, Context());
            Assert.AreEqual(kind, failure.Kind);
            Assert.AreEqual(retryable, failure.Retryable);
            Assert.AreEqual(kind == LlmFailureKind.Auth || kind == LlmFailureKind.ModelNotFound, failure.NeedsSettings);
            Assert.IsNotEmpty(failure.Friendly);
            StringAssert.DoesNotContain("HTTP", failure.Friendly, "no status codes for the player");
        }

        [Test]
        public void Technical_HasTheFactsAMaintainerNeeds()
        {
            var failure = LlmFailure.From(429, "HTTP 429 from chat endpoint: slow down", Context("{\"error\":{\"message\":\"slow down\"}}"));
            StringAssert.Contains("kind: RateLimit", failure.Technical);
            StringAssert.Contains("http status: 429", failure.Technical);
            StringAssert.Contains("endpoint: https://api.example.test/v1/chat/completions", failure.Technical);
            StringAssert.Contains("model: gpt-x", failure.Technical);
            StringAssert.Contains("3 msgs", failure.Technical);
            StringAssert.Contains("attempts: 3", failure.Technical);
            StringAssert.Contains("slow down", failure.Technical);
            StringAssert.Contains("time: ", failure.Technical);
        }

        [Test]
        public void Technical_NeverContainsTheKeyOrAnAuthHeader()
        {
            string body = "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\"},\"echo\":\"Authorization: Bearer " + Key + "\"}";
            var failure = LlmFailure.From(401, "HTTP 401 from chat endpoint: Incorrect API key " + Key, Context(body));
            StringAssert.DoesNotContain(Key, failure.Technical);
            StringAssert.DoesNotContain("abcdef1234567890", failure.Technical);
            StringAssert.DoesNotContain("Bearer " + Key, failure.Technical);
            StringAssert.DoesNotContain("key=" + Key, failure.Technical, "the query string is dropped from the endpoint");
            StringAssert.Contains("[redacted]", failure.Technical);
        }

        [Test]
        public void Redact_CatchesKeyShapedStringsEvenWhenTheKeyIsUnknown()
        {
            string text = LlmFailure.Redact("x-api-key: abc123456789 and sk-proj-AAAAAAAA1111 and ?api_key=zzzzzzzz&a=1", string.Empty);
            StringAssert.DoesNotContain("abc123456789", text);
            StringAssert.DoesNotContain("AAAAAAAA1111", text);
            StringAssert.DoesNotContain("zzzzzzzz", text);
            StringAssert.Contains("a=1", text);
        }

        [Test]
        public void Setup_AndCancelled_AreNotRetryable()
        {
            var setup = LlmFailure.Setup("No API key set for \"OpenAI\" (add one in Settings).", Context());
            Assert.IsTrue(setup.NeedsSettings);
            Assert.IsFalse(setup.Retryable);
            Assert.IsFalse(LlmFailure.Cancelled(Context()).Retryable);
        }

        [Test]
        public void Malformed_IsRetryableAndSaysWhat()
        {
            var failure = LlmFailure.Malformed("an empty reply", Context());
            Assert.AreEqual(LlmFailureKind.Malformed, failure.Kind);
            Assert.IsTrue(failure.Retryable);
            StringAssert.Contains("an empty reply", failure.Technical);
        }
    }
}
