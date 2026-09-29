using System.Collections.Generic;
using UnityEngine;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Tabletop sounds synthesized at startup: no audio assets to license or
    /// import. A soft tick for buttons, a dice clatter, a two-note chime for
    /// success and a low thud for failure. Quiet by design; Settings can mute.
    /// </summary>
    public static class VaultSfx
    {
        public enum Cue { Click, Dice, Success, Fail }

        public static bool Muted;
        public static float Volume = 0.35f;

        private const int Rate = 44100;
        private static AudioSource _source;
        private static readonly Dictionary<Cue, AudioClip> Clips = new Dictionary<Cue, AudioClip>();

        public static void Play(Cue cue)
        {
            if (Muted || !Application.isPlaying) { return; }
            Ensure();
            AudioClip clip;
            if (Clips.TryGetValue(cue, out clip)) { _source.PlayOneShot(clip, Volume); }
        }

        private static void Ensure()
        {
            if (_source != null) { return; }
            var go = new GameObject("VaultSfx");
            Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            var rng = new System.Random(7);
            Clips[Cue.Click] = Make("click", 0.03f, delegate (float t, int i)
            {
                return (float)(rng.NextDouble() * 2 - 1) * 0.3f * Mathf.Exp(-t * 220f)
                    + Mathf.Sin(2f * Mathf.PI * 1800f * t) * 0.25f * Mathf.Exp(-t * 160f);
            });
            Clips[Cue.Dice] = Make("dice", 0.5f, delegate (float t, int i)
            {
                // Five clacks at irregular offsets, each a short filtered-noise burst.
                float v = 0f;
                float[] hits = { 0f, 0.07f, 0.16f, 0.27f, 0.41f };
                for (int h = 0; h < hits.Length; h++)
                {
                    float dt = t - hits[h];
                    if (dt < 0f || dt > 0.05f) { continue; }
                    v += ((float)(rng.NextDouble() * 2 - 1) * 0.5f + Mathf.Sin(2f * Mathf.PI * (900f + h * 170f) * dt) * 0.5f)
                        * Mathf.Exp(-dt * 90f) * (1f - h * 0.12f);
                }
                return v * 0.6f;
            });
            Clips[Cue.Success] = Make("success", 0.7f, delegate (float t, int i)
            {
                float a = Mathf.Sin(2f * Mathf.PI * 659.25f * t) * Mathf.Exp(-t * 6f);
                float b = t < 0.12f ? 0f : Mathf.Sin(2f * Mathf.PI * 880f * (t - 0.12f)) * Mathf.Exp(-(t - 0.12f) * 5f);
                return (a + b) * 0.28f;
            });
            Clips[Cue.Fail] = Make("fail", 0.5f, delegate (float t, int i)
            {
                float freq = Mathf.Lerp(140f, 70f, t / 0.5f);
                return Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t * 7f) * 0.45f;
            });
        }

        private static AudioClip Make(string name, float seconds, System.Func<float, int, float> sample)
        {
            int count = Mathf.CeilToInt(seconds * Rate);
            var data = new float[count];
            for (int i = 0; i < count; i++) { data[i] = Mathf.Clamp(sample((float)i / Rate, i), -1f, 1f); }
            var clip = AudioClip.Create("vault-" + name, count, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
