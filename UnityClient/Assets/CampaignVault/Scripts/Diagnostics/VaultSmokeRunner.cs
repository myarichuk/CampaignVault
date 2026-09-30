using System;
using System.Collections;
using UnityEngine;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.Diagnostics
{
    /// <summary>
    /// End-to-end smoke test of a player build:
    ///   CampaignVaultClient -vault-smoke http://127.0.0.1:PORT [-vault-smoke-llm http://127.0.0.1:PORT/v1]
    /// Runs VaultSmokeScenario through the controller (no UI involved), logs
    /// one "[VaultSmoke] PASS|FAIL step: detail" line per check and quits
    /// with exit code 0 (all pass) or 1. Any logged exception fails the run.
    /// Prefs are in-memory for the whole run (see VaultBootstrap).
    /// Point it only at a scratch server: it creates and deletes a campaign.
    /// </summary>
    public class VaultSmokeRunner : MonoBehaviour
    {
        private const string Tag = "[VaultSmoke] ";
        private int _exceptions;

        public static string RequestedServerUrl() { return ArgAfter("-vault-smoke"); }

        private static string ArgAfter(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag) { return args[i + 1]; }
            }
            return null;
        }

        public void Run(VaultBootstrap bootstrap, string serverUrl)
        {
            Application.logMessageReceived += OnLog;
            StartCoroutine(Main(bootstrap, serverUrl, ArgAfter("-vault-smoke-llm")));
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception) { _exceptions++; }
        }

        private IEnumerator Main(VaultBootstrap bootstrap, string serverUrl, string llmUrl)
        {
            Debug.Log(Tag + "start server=" + serverUrl + " llm=" + (llmUrl ?? "(none)"));
            var scenario = new VaultSmokeScenario(bootstrap.State, bootstrap.Controller);
            scenario.Log = delegate (string line) { Debug.Log(Tag + line); };
            yield return scenario.Run(serverUrl, llmUrl);
            scenario.Check("no-exceptions", _exceptions == 0, _exceptions + " exceptions logged");
            Application.logMessageReceived -= OnLog;
            Debug.Log(Tag + "RESULT pass=" + scenario.Passed + " fail=" + scenario.Failed);
            Application.Quit(scenario.Failed == 0 ? 0 : 1);
        }
    }
}
