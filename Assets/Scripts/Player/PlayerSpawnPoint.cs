using System.Collections.Generic;
using EscapeRoom.Networking;
using UnityEngine;

namespace EscapeRoom.Player
{
    /// <summary>
    /// Where a player appears when a round starts. Set <see cref="role"/> so each impairment
    /// starts in its own room (the level's "Spawners" use Blind / Deaf / Mute).
    /// Points with Role = None are used as a fallback (by <see cref="index"/>).
    /// The blue arrow shows the facing direction.
    /// </summary>
    public class PlayerSpawnPoint : MonoBehaviour
    {
        public PlayerRole role = PlayerRole.None;
        public int index;

        static readonly List<PlayerSpawnPoint> all = new();

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        public static PlayerSpawnPoint ForRole(PlayerRole r, int fallbackIndex)
        {
            all.RemoveAll(s => s == null);
            foreach (var s in all) if (r != PlayerRole.None && s.role == r) return s;
            foreach (var s in all) if (s.role == PlayerRole.None && s.index == fallbackIndex) return s;
            return all.Count > 0 ? all[fallbackIndex % all.Count] : null;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = role switch
            {
                PlayerRole.Blind => new Color(0.6f, 0.6f, 1f, 0.9f),
                PlayerRole.Deaf => new Color(1f, 0.8f, 0.3f, 0.9f),
                PlayerRole.Mute => new Color(1f, 0.4f, 0.4f, 0.9f),
                _ => new Color(0.2f, 0.6f, 1f, 0.8f),
            };
            Vector3 head = transform.position + Vector3.up * 0.9f;
            Gizmos.DrawWireSphere(head, 0.35f);
            Gizmos.DrawLine(head, head + transform.forward * 0.8f);
        }
    }
}
