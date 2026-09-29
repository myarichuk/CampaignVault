using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Net
{
    /// <summary>
    /// Connection settings for one CampaignVault server. The bearer token lives
    /// in memory only: it is never written to PlayerPrefs, never logged, and is
    /// only ever sent as an Authorization header (never ?token=, which the
    /// server writes to its request logs).
    /// </summary>
    public class VaultClientConfig : MonoBehaviour
    {
        [Tooltip("Base URL of the CampaignVault server, e.g. http://localhost:5275")]
        public string ServerUrl = "http://localhost:5275";

        [Tooltip("MCP connector: play for live sessions, build for campaign setup.")]
        public string Connector = "play";

        [Tooltip("HTTP timeout per call, seconds.")]
        public int TimeoutSeconds = 60;

        private string _bearerToken = string.Empty;

        public bool HasBearerToken { get { return !string.IsNullOrEmpty(_bearerToken); } }

        public void SetBearerToken(string token) { _bearerToken = (token ?? string.Empty).Trim(); }

        public void ClearSecrets() { _bearerToken = string.Empty; }

        private void OnApplicationQuit() { ClearSecrets(); }
        private void OnDisable() { ClearSecrets(); }

        public string ActiveConnector()
        {
            if (Connector == "build") { return "build"; }
            return "play";
        }

        public string ConnectorPath(string connector)
        {
            if (connector == "build") { return ServerUrl.TrimEnd('/') + "/build"; }
            return ServerUrl.TrimEnd('/') + "/play";
        }

        public bool Validate(out string reason)
        {
            if (!TextSanitizer.IsAllowedHttpUrl(ServerUrl, out reason)) { return false; }
            if (ActiveConnector() != "play" && ActiveConnector() != "build")
            {
                reason = "Connector must be play or build.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        internal void ApplyAuth(UnityWebRequest request)
        {
            if (HasBearerToken) { request.SetRequestHeader("Authorization", "Bearer " + _bearerToken); }
        }

        internal string Redact(string text) { return TextSanitizer.Redact(text, _bearerToken); }

        /// <summary>GET /health; expects {"status":"healthy"}.</summary>
        public IEnumerator CheckHealth(Action<bool, string> done)
        {
            string reason;
            if (!TextSanitizer.IsAllowedHttpUrl(ServerUrl, out reason))
            {
                done(false, reason);
                yield break;
            }
            string url = ServerUrl.TrimEnd('/') + "/health";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Math.Min(TimeoutSeconds, 15);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    done(false, "HTTP " + request.responseCode + " " + request.error);
                    yield break;
                }
                JsonValue body;
                if (JsonValue.TryParse(request.downloadHandler.text, out body)
                    && body.GetString("status", string.Empty) == "healthy")
                {
                    done(true, "healthy");
                }
                else
                {
                    done(false, "unexpected /health payload");
                }
            }
        }
    }
}
