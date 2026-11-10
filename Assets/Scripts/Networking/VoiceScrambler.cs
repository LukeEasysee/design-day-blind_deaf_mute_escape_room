using UnityEngine;

namespace EscapeRoom.Networking
{
    /// <summary>
    /// Makes speech unintelligible while keeping "someone is talking" readable.
    /// Must sit AFTER the AudioSource on the GameObject (filters run in component order).
    ///
    /// How: an envelope follower tracks how loud the voice is moment to moment.
    /// That envelope drives a low, filtered "wah" tone (think adults in old cartoons),
    /// mixed with a steeply low-passed copy of the real voice. Rhythm, loudness and
    /// emotion come through; consonants (which carry the words) are gone.
    /// </summary>
    public class VoiceScrambler : MonoBehaviour
    {
        [Tooltip("Cutoff of the 4-stage low-pass on the real voice. Lower = less intelligible.")]
        [Range(60f, 1000f)] public float cutoffHz = 180f;
        [Tooltip("How much of the (heavily filtered) real voice is kept.")]
        [Range(0f, 1f)] public float voiceMix = 0.35f;
        [Tooltip("How much of the envelope-driven mumble tone is added.")]
        [Range(0f, 1f)] public float mumbleMix = 0.6f;
        [Tooltip("Base pitch of the mumble tone.")]
        [Range(50f, 300f)] public float mumblePitchHz = 110f;
        [Range(0f, 4f)] public float outputGain = 1.6f;

        // Audio-thread state
        float sampleRate = 48000f;
        float env;
        float phase;
        float wobblePhase;
        readonly float[] lp = new float[4];
        readonly float[] toneLp = new float[2];

        // Copies of settings for the audio thread (read once per buffer).
        volatile float aCutoff, aVoiceMix, aMumbleMix, aPitch, aGain;

        void OnEnable() => Prepare(AudioSettings.outputSampleRate);

        public void Prepare(float outputSampleRate)
        {
            sampleRate = outputSampleRate;
            CopySettings();
        }

        void Update() => CopySettings();

        void CopySettings()
        {
            aCutoff = cutoffHz;
            aVoiceMix = voiceMix;
            aMumbleMix = mumbleMix;
            aPitch = mumblePitchHz;
            aGain = outputGain;
        }

        void OnAudioFilterRead(float[] data, int channels) => Process(data, channels);

        /// <summary>Scrambles an interleaved buffer in place. Public so it can be tested offline.</summary>
        public void Process(float[] data, int channels)
        {
            float sr = sampleRate;
            float lpCoef = 1f - Mathf.Exp(-2f * Mathf.PI * aCutoff / sr);
            float toneCoef = 1f - Mathf.Exp(-2f * Mathf.PI * 400f / sr);
            float attack = 1f - Mathf.Exp(-1f / (0.010f * sr));
            float release = 1f - Mathf.Exp(-1f / (0.050f * sr));
            float vMix = aVoiceMix, mMix = aMumbleMix, gain = aGain, pitch = aPitch;

            for (int i = 0; i < data.Length; i += channels)
            {
                // Mono input
                float x = 0f;
                for (int c = 0; c < channels; c++) x += data[i + c];
                x /= channels;

                // Envelope follower
                float a = Mathf.Abs(x);
                env += (a > env ? attack : release) * (a - env);

                // 4-pole low-pass on the real voice (steep: kills consonants)
                float y = x;
                for (int s = 0; s < 4; s++)
                {
                    lp[s] += lpCoef * (y - lp[s]);
                    y = lp[s];
                }

                // Mumble: slightly wobbling saw tone, softened, shaped by the voice envelope
                wobblePhase += 3.1f / sr;
                if (wobblePhase > 1f) wobblePhase -= 1f;
                float f = pitch * (1f + 0.12f * Mathf.Sin(wobblePhase * 2f * Mathf.PI) + 4f * env);
                phase += f / sr;
                if (phase > 1f) phase -= 1f;
                float tone = phase * 2f - 1f;
                toneLp[0] += toneCoef * (tone - toneLp[0]);
                toneLp[1] += toneCoef * (toneLp[0] - toneLp[1]);
                float mumble = toneLp[1] * env * 3f;

                float o = Mathf.Clamp((y * vMix + mumble * mMix) * gain, -1f, 1f);
                for (int c = 0; c < channels; c++) data[i + c] = o;
            }
        }
    }
}
