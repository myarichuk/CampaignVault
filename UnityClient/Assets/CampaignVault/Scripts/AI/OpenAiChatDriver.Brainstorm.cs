using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>A tool-free side conversation with the model (onboarding brainstorm, the DM-drafted handoff): its own messages, nothing of the play history.</summary>
    public partial class OpenAiChatDriver
    {
        /// <summary>
        /// A tool-free side conversation (onboarding brainstorming): the caller
        /// owns the messages; nothing touches the play history or the server.
        /// It goes through the active profile and counts toward the session's
        /// and the campaign's spend. done(reply, error) — exactly one of them is non-null.
        /// </summary>
        public IEnumerator Brainstorm(IList<KeyValuePair<string, string>> roleMessages, Action<string> onDelta, Action<string, string> done, bool retryBusy = true)
        {
            var messages = JsonValue.NewArray();
            foreach (var m in roleMessages) { messages.ArrayValue.Add(RoleMessage(m.Key, m.Value)); }
            ProviderProfile profile = Byok.Active;
            yield return Complete(profile, messages, onDelta, delegate (string content, TokenUsage usage, string error)
            {
                if (usage != null) { RecordUsage(usage, profile.Model.Trim()); }
                done(content, error);
            }, retryBusy);
        }

        /// <summary>
        /// One tool-free call through <paramref name="profile"/> with the caller's messages. Nothing touches the play history,
        /// and nothing is counted: <c>done(content, usage, error)</c> hands the priced usage back (null when the call failed
        /// before a reply) and the caller decides where it belongs. Exactly one of content and error is non-null.
        /// </summary>
        public IEnumerator Complete(ProviderProfile profile, JsonValue messages, Action<string> onDelta, Action<string, TokenUsage, string> done, bool retryBusy = true)
        {
            string reason;
            if (!ByokSettings.ValidateProfile(profile, out reason))
            {
                LastFailure = LlmFailure.Setup(reason, FailureContext(profile, 0, string.Empty));
                done(null, null, reason);
                yield break;
            }
            if (IsBusy)
            {
                const string busy = "The DM is busy with a turn. Try again when it finishes.";
                LastFailure = LlmFailure.Other(busy, FailureContext(profile, 0, string.Empty));
                done(null, null, busy);
                yield break;
            }
            LastFailure = null;
            IsBusy = true;
            _cancelRequested = false;
            _retryBusy = retryBusy;
            try
            {
                JsonValue response = null;
                string failure = null;
                yield return RequestReply(profile,
                    delegate (bool stream, bool usageOptions)
                    {
                        var request = NewRequest(profile, stream, usageOptions);
                        request.ObjectValue["messages"] = messages;
                        return request;
                    },
                    onDelta ?? delegate { },
                    delegate (JsonValue r, string err) { response = r; failure = err; });
                if (failure != null) { done(null, null, failure); yield break; }
                TokenUsage usage = MeasureUsage(response, profile);
                var choices = response.GetArray("choices");
                JsonValue message = choices.Count > 0 ? choices[0].Get("message") : JsonValue.Null;
                string content = string.Empty;
                string reasoning;
                if (!message.IsNull) { ExtractContent(message, out content, out reasoning); }
                content = content.Trim();
                if (content.Length == 0)
                {
                    LastFailure = LlmFailure.Malformed("an empty reply", FailureContext(profile, 1, CapText(response.ToJson(), 1500)));
                    done(null, usage, profile.Model.Trim() + " returned an empty reply.");
                    yield break;
                }
                done(content, usage, null);
            }
            finally { IsBusy = false; _retryBusy = true; }
        }

        /// <summary>Quiet retries of a busy provider; off for a throwaway helper call (a level hint) that shouldn't hold the DM up.</summary>
        private bool _retryBusy = true;
    }
}
