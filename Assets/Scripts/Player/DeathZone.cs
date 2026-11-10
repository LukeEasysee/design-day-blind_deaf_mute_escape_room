using EscapeRoom.Networking;
using UnityEngine;

namespace EscapeRoom.Player
{
    /// <summary>
    /// Trigger volume under the level. Only the falling player's own client reacts
    /// (remote players have their CharacterController disabled, so they never trigger it):
    ///  - the role in <see cref="restartsLevelFor"/> (Blind by default) falling restarts the level for everyone;
    ///  - anyone else is just put back at their spawn point.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class DeathZone : MonoBehaviour
    {
        public PlayerRole restartsLevelFor = PlayerRole.Blind;
        public string restartMessage = "The blind player fell! Restarting the level...";

        void Reset()
        {
            GetComponent<BoxCollider>().isTrigger = true;
        }

        void Awake() => GetComponent<BoxCollider>().isTrigger = true;

        void OnTriggerEnter(Collider other)
        {
            var controller = other.GetComponentInParent<PlayerController>();
            if (controller == null || !controller.IsOwner || !GameFlow.IsPlaying) return;

            var player = controller.GetComponent<NetworkPlayer>();
            if (player != null && player.Role.Value == restartsLevelFor && GameFlow.Instance != null)
                GameFlow.Instance.RequestRestartRpc(restartMessage);
            else
                controller.Respawn();
        }

        void OnDrawGizmos()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.15f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.8f);
            Gizmos.DrawWireCube(box.center, box.size);
        }
    }
}
