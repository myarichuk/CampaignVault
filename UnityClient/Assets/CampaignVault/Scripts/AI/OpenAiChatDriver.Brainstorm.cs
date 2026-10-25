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
        /// done(reply, error) — exactly one of them is non-null.
        /// </summary>
        public IEnumerator Brainstorm(IList<KeyValuePair<string, string>> roleMessages, Action<string> onDelta, Action<string, string> done, bool retryBusy = true)
        {
            string reason;
            if (!Byok.Validate(out reason))
            {
                LastFailure = LlmFailure.Setup(reason, FailureContext(Byok.ChatUrl(), 0, string.Empty));
                done(null, reason);
                yield break;
            }
            if (IsBusy)
            {
                const string busy = "The DM is busy with a turn. Try again when it finishes.";
                LastFailure = LlmFailure.Other(busy, FailureContext(Byok.ChatUrl(), 0, string.Empty));
                done(null, busy);
                yield break;
            }
            LastFailure = null;
            IsBusy = true;
            _cancelRequested = false;
            _retryBusy = retryBusy;
            try
            {
                var messages = JsonValue.NewArray();
                foreach (var m in roleMessages) { messages.ArrayValue.Add(RoleMessage(m.Key, m.Value)); }
                string model = Byok.Model.Trim();
                JsonValue response = null;
                string failure = null;
                yield return RequestReply(
                    delegate (bool stream, bool usageOptions)
                    {
                        var request = NewRequest(model, stream, usageOptions);
                        request.ObjectValue["messages"] = messages;
                        return request;
                    },
                    onDelta ?? delegate { },
                    delegate (JsonValue r, string err) { response = r; failure = err; });
                if (failure != null) { done(null, failure); yield break; }
                RecordUsage(MeasureUsage(response, model), model);
                var choices = response.GetArray("choices");
                JsonValue message = choices.Count > 0 ? choices[0].Get("message") : JsonValue.Null;
                string content = string.Empty;
                string reasoning;
                if (!message.IsNull) { ExtractContent(message, out content, out reasoning); }
                content = content.Trim();
                if (content.Length == 0)
                {
                    LastFailure = LlmFailure.Malformed("an empty reply", FailureContext(Byok.ChatUrl(), 1, CapText(response.ToJson(), 1500)));
                    done(null, model + " returned an empty reply.");
                    yield break;
                }
                done(content, null);
            }
            finally { IsBusy = false; _retryBusy = true; }
        }

        /// <summary>Quiet retries of a busy provider; off for a throwaway helper call (a level hint) that shouldn't hold the DM up.</summary>
        private bool _retryBusy = true;
    }
}
