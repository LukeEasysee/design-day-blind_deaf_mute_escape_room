using EscapeRoom.Senses;
using Unity.Services.Vivox.AudioTaps;
using UnityEngine;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// Applies the local player's impairment:
    ///  - Blind: "blind vision". Their camera renders ONLY the BlindVision layer on black,
    ///           i.e. sound pulses (SoundVisualizer). Everyone else's camera hides that layer.
    ///  - Deaf:  game sounds get a low-pass filter on each AudioSource. Voices are
    ///           scrambled separately by VoiceChatManager / VoiceScrambler.
    ///  - Mute:  handled by VoiceChatManager (mic forced off).
    ///
    /// The filter goes on each source rather than on the AudioListener, because
    /// the listener filter had no audible effect in testing.
    /// </summary>
    public class RoleEffects : MonoBehaviour
    {
        public static PlayerRole CurrentRole { get; private set; } = PlayerRole.None;

        const float SweepInterval = 0.5f;

        float nextSweep;
        Camera styledCamera;
        CameraClearFlags originalClear;
        Color originalBackground;
        int originalMask;

        void OnEnable() => NetworkPlayer.LocalRoleChanged += Apply;
        void OnDisable() => NetworkPlayer.LocalRoleChanged -= Apply;

        void Apply(PlayerRole role)
        {
            CurrentRole = role;
            nextSweep = 0f; // re-apply immediately
            Debug.Log($"[RoleEffects] Local role is now {role}");
        }

        void Update()
        {
            if (Time.unscaledTime < nextSweep) return;
            nextSweep = Time.unscaledTime + SweepInterval;

            // Use the networked value so this stays correct even if an event was missed.
            if (NetworkPlayer.Local != null) CurrentRole = NetworkPlayer.Local.Role.Value;
            ApplyVision();
            bool deaf = CurrentRole == PlayerRole.Deaf;
            float cutoff = VoiceChatManager.Instance != null ? VoiceChatManager.Instance.deafGameAudioCutoffHz : 300f;

            foreach (var src in FindObjectsByType<AudioSource>())
            {
                if (src.GetComponent<VivoxAudioTap>() != null) continue; // voices: handled by VoiceScrambler

                var f = src.GetComponent<DeafGameAudioFilter>();
                if (f == null)
                {
                    if (!deaf) continue;
                    f = src.gameObject.AddComponent<DeafGameAudioFilter>();
                }
                f.Set(deaf, cutoff);
            }
        }

        void ApplyVision()
        {
            var cam = NetworkPlayer.Local != null ? NetworkPlayer.Local.PlayerCamera : null;
            if (cam == null) return;
            if (cam != styledCamera)
            {
                styledCamera = cam;
                originalClear = cam.clearFlags;
                originalBackground = cam.backgroundColor;
                originalMask = cam.cullingMask;
            }

            int layer = SoundVisualizer.Layer;
            int blindMask = layer >= 0 ? 1 << layer : 0;
            if (CurrentRole == PlayerRole.Blind)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.cullingMask = blindMask;
            }
            else
            {
                cam.clearFlags = originalClear;
                cam.backgroundColor = originalBackground;
                cam.cullingMask = originalMask & ~blindMask;
            }
        }
    }

    /// <summary>A 4-pole low-pass for game sounds while the local player is Deaf.</summary>
    public class DeafGameAudioFilter : MonoBehaviour
    {
        volatile bool active;
        volatile float cutoffHz = 300f;
        float sampleRate = 48000f;
        float[,] state = new float[8, 4];

        void OnEnable() => sampleRate = AudioSettings.outputSampleRate;

        public void Set(bool on, float cutoff)
        {
            active = on;
            cutoffHz = cutoff;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!active) return;
            if (channels > state.GetLength(0)) state = new float[channels, 4];
            float k = 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / sampleRate);
            for (int i = 0; i < data.Length; i += channels)
            {
                for (int c = 0; c < channels; c++)
                {
                    float y = data[i + c];
                    for (int s = 0; s < 4; s++)
                    {
                        state[c, s] += k * (y - state[c, s]);
                        y = state[c, s];
                    }
                    data[i + c] = y;
                }
            }
        }
    }
}
