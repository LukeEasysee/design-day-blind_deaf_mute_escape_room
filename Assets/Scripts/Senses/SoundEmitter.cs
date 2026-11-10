using UnityEngine;

namespace EscapeRoom.Senses
{
    /// <summary>
    /// Makes an AudioSource visible to the Blind player as sound pulses while it plays.
    /// Add it next to any AudioSource in a level (machines, ticking clocks, the goal hum...).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SoundEmitter : MonoBehaviour
    {
        [Tooltip("Seconds between pulses while the sound plays.")]
        public float interval = 0.7f;
        [Tooltip("Base size/brightness of the pulses.")]
        public float strength = 1f;
        [Tooltip("Scale pulses by how loud the audio currently is.")]
        public bool followLoudness = true;
        [Tooltip("Optional offset (e.g. the speaker cone of a machine).")]
        public Vector3 localOffset;

        AudioSource source;
        float nextPulse;
        readonly float[] samples = new float[256];

        void Awake() => source = GetComponent<AudioSource>();

        void Update()
        {
            if (!SoundVisualizer.Active || source == null || !source.isPlaying) return;
            if (Time.time < nextPulse) return;
            nextPulse = Time.time + interval;

            float s = strength;
            if (followLoudness)
            {
                source.GetOutputData(samples, 0);
                float sum = 0f;
                foreach (float v in samples) sum += v * v;
                float rms = Mathf.Sqrt(sum / samples.Length);
                if (rms < 0.0005f) return; // silent right now
                // Loudness nudges the pulse size; it never shrinks it to nothing.
                s *= Mathf.Lerp(0.7f, 1.3f, Mathf.Clamp01(rms * 8f));
            }
            SoundVisualizer.Pulse(transform.TransformPoint(localOffset), s);
        }
    }
}
