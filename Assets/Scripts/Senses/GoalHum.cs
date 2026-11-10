using UnityEngine;

namespace EscapeRoom.Senses
{
    /// <summary>
    /// A silent "hum" beacon: it makes no sound, it only shows sound pulses to the Blind
    /// player, so they can see where to walk. Place it at the exit
    /// (prefab: Assets/Prefabs/GoalHum.prefab).
    /// </summary>
    public class GoalHum : MonoBehaviour
    {
        [Tooltip("Seconds between pulses.")]
        public float interval = 1f;
        [Tooltip("Pulse size/brightness.")]
        public float strength = 1.3f;

        float nextPulse;

        void Update()
        {
            if (!SoundVisualizer.Active || Time.time < nextPulse) return;
            nextPulse = Time.time + interval;
            SoundVisualizer.Pulse(transform.position, strength);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.55f, 0.85f, 1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, 0.3f);
            Gizmos.DrawWireSphere(transform.position, 0.6f);
        }
    }
}
