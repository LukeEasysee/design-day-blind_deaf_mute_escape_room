using System.Collections.Generic;
using UnityEngine;

namespace EscapeRoom.Player
{
    /// <summary>
    /// Drop into a level to choose where players appear. Index 0, 1 and 2 are used for
    /// the host and the first and second joiners. The blue arrow shows facing direction.
    /// </summary>
    public class PlayerSpawnPoint : MonoBehaviour
    {
        public int index;

        static readonly List<PlayerSpawnPoint> all = new();

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        public static PlayerSpawnPoint Get(int i)
        {
            if (all.Count == 0)
                all.AddRange(FindObjectsByType<PlayerSpawnPoint>());
            foreach (var s in all) if (s.index == i) return s;
            return all.Count > 0 ? all[i % all.Count] : null;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.9f, 0.35f);
            Gizmos.DrawLine(transform.position + Vector3.up * 0.9f, transform.position + Vector3.up * 0.9f + transform.forward * 0.8f);
        }
    }
}
