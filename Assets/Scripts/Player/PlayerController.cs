using EscapeRoom.Networking;
using EscapeRoom.Senses;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeRoom.Player
{
    /// <summary>
    /// First-person walking controller (no jump, no sprint), owner-authoritative.
    ///  - WASD: walk.  Mouse: look (while the cursor is locked).
    ///  - Hold Left / Right mouse button: raise the left / right hand. The raised arm points where you look.
    ///  - Esc: free the cursor (to use the lobby UI). Click in the game view to lock it again.
    /// Look pitch and raised hands are synced so other players see where you look and point.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        public float walkSpeed = 2.6f;
        public float acceleration = 12f;
        public float gravity = 20f;

        [Header("Look")]
        public float lookSensitivity = 0.12f;
        public float minPitch = -80f;
        public float maxPitch = 80f;
        [SerializeField] Transform cameraPivot;

        /// <summary>Camera pitch in degrees (positive = looking down), synced to everyone.</summary>
        public readonly NetworkVariable<float> Pitch = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>Bit 0 = left hand raised, bit 1 = right hand raised.</summary>
        public readonly NetworkVariable<byte> Hands = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public bool LeftHandRaised => (Hands.Value & 1) != 0;
        public bool RightHandRaised => (Hands.Value & 2) != 0;
        /// <summary>Horizontal speed in m/s, measured on every client (also for remote players).</summary>
        public float GroundSpeed { get; private set; }

        /// <summary>Look pitch in degrees: live for the owner, synced value for everyone else.</summary>
        public float CurrentPitch => IsOwner ? pitch : Pitch.Value;

        public static bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        CharacterController cc;
        Vector3 velocity;
        float pitch;
        Vector3 lastPosition;

        void Awake() => cc = GetComponent<CharacterController>();

        bool toolUnlockedCursor;
        bool toolHeldLastFrame;

        [Header("Interaction")]
        public float interactDistance = 3f;

        /// <summary>Prompt for what the crosshair is on (shown by LobbyUI), or null.</summary>
        public static string InteractionHint { get; private set; }

        public override void OnNetworkSpawn()
        {
            lastPosition = transform.position;

            var body = GetComponent<CharacterPose>();
            if (IsOwner && body != null) body.SetHeadHidden(true);
            else if (body != null) body.Footstep += OnRemoteFootstep; // Blind player's sound pulses

            GameFlow.PhaseChanged += OnPhaseChanged;
            GameFlow.RoundStarted += OnRoundStarted;
            ApplyPhase();
        }

        public override void OnNetworkDespawn()
        {
            GameFlow.PhaseChanged -= OnPhaseChanged;
            GameFlow.RoundStarted -= OnRoundStarted;
            var body = GetComponent<CharacterPose>();
            if (body != null) body.Footstep -= OnRemoteFootstep;
            if (IsOwner)
            {
                LockCursor(false);
                SetSceneViewers(true);
            }
        }

        void OnPhaseChanged(GamePhase phase) => ApplyPhase();

        // A (re)loaded level started: everyone goes back to their spawn point.
        void OnRoundStarted(int round)
        {
            ApplyPhase();
            if (IsOwner) MoveToSpawnPoint();
        }

        /// <summary>
        /// Lobby: no body, scene camera, free cursor.
        /// In game: body + own camera (scene/level cameras off). Movement only while Playing,
        /// so players are frozen while the level restarts.
        /// </summary>
        void ApplyPhase()
        {
            bool inGame = GameFlow.IsInGame;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = inGame;
            cc.enabled = IsOwner && GameFlow.IsPlaying;
            velocity = Vector3.zero;

            if (!IsOwner) return;

            var me = GetComponent<NetworkPlayer>();
            var cam = me != null ? me.PlayerCamera : null;
            if (cam != null)
            {
                cam.enabled = inGame;
                var listener = cam.GetComponent<AudioListener>();
                if (listener != null) listener.enabled = inGame;
            }
            SetSceneViewers(!inGame);
            LockCursor(inGame);
        }

        // Every camera/listener that isn't a player's (bootstrap lobby camera, the level's own test camera).
        static void SetSceneViewers(bool on)
        {
            foreach (var c in FindObjectsByType<Camera>())
                if (c.GetComponentInParent<NetworkPlayer>() == null) c.enabled = on;
            foreach (var l in FindObjectsByType<AudioListener>())
                if (l.GetComponentInParent<NetworkPlayer>() == null) l.enabled = on;
        }

        /// <summary>Owner: put the player back at their role's spawn point.</summary>
        public void Respawn()
        {
            if (IsOwner) MoveToSpawnPoint();
        }

        // Spawn points come from the level (PlayerSpawnPoint per role). Without any, players spread in a small circle.
        void MoveToSpawnPoint()
        {
            var me = GetComponent<NetworkPlayer>();
            var spawn = PlayerSpawnPoint.ForRole(me != null ? me.Role.Value : PlayerRole.None, (int)(OwnerClientId % 3));
            Vector3 pos = spawn != null ? spawn.transform.position
                : new Vector3(Mathf.Cos(OwnerClientId * 2.1f), 0f, Mathf.Sin(OwnerClientId * 2.1f)) * 1.5f;
            Quaternion rot = spawn != null ? Quaternion.Euler(0f, spawn.transform.eulerAngles.y, 0f) : Quaternion.identity;
            bool wasEnabled = cc.enabled;
            cc.enabled = false;
            transform.SetPositionAndRotation(pos, rot);
            cc.enabled = wasEnabled;
            lastPosition = pos;
            velocity = Vector3.zero;
            pitch = 0f;
            Pitch.Value = 0f;
            if (cameraPivot != null) cameraPivot.localRotation = Quaternion.identity;
        }

        void OnRemoteFootstep(Vector3 position) => SoundVisualizer.Pulse(position, 0.35f);

        static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void Update()
        {
            if (IsSpawned && IsOwner && GameFlow.IsPlaying) OwnerUpdate();

            // Speed for animation, derived from movement so it also works for remote (NetworkTransform) players.
            Vector3 delta = transform.position - lastPosition;
            delta.y = 0f;
            float speed = Time.deltaTime > 0f ? delta.magnitude / Time.deltaTime : 0f;
            GroundSpeed = Mathf.Lerp(GroundSpeed, speed, 1f - Mathf.Exp(-10f * Time.deltaTime));
            lastPosition = transform.position;

            if (!IsOwner && cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(Pitch.Value, 0f, 0f);
        }

        void OwnerUpdate()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            // Drawing view: camera sits at the station, mouse is free for the pen/button,
            // body stays put. E (or Esc when not holding a tool) goes back to first person.
            var station = DrawingStation.Active;
            if (station != null)
            {
                InteractionHint = null;
                if (CursorLocked) LockCursor(false);
                bool toolNow = global::PenMouseController.HeldTool != null;
                bool leave = kb != null && (kb.eKey.wasPressedThisFrame ||
                             (kb.escapeKey.wasPressedThisFrame && !toolNow && !toolHeldLastFrame));
                toolHeldLastFrame = toolNow;
                if (Hands.Value != 0) Hands.Value = 0;
                velocity = new Vector3(0f, cc.isGrounded ? -1f : velocity.y - gravity * Time.deltaTime, 0f);
                cc.Move(velocity * Time.deltaTime);
                if (leave)
                {
                    station.Exit();
                    toolUnlockedCursor = false;
                    LockCursor(true);
                }
                return;
            }
            toolHeldLastFrame = false;

            // Holding a pen/eraser: the mouse is freed so it can move the tool over the paper
            // (look and hand-raising pause). Dropping it (RMB / Esc) captures the mouse again.
            bool tool = global::PenMouseController.HeldTool != null;
            if (tool)
            {
                if (CursorLocked) LockCursor(false);
                toolUnlockedCursor = true;
            }
            else if (toolUnlockedCursor)
            {
                toolUnlockedCursor = false;
                LockCursor(true);
            }
            // Cursor: Esc frees it for the UI, clicking the game view captures it again.
            else if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);
            else if (!CursorLocked && mouse != null && mouse.leftButton.wasPressedThisFrame && !LobbyUIHasMouse())
                LockCursor(true);

            bool locked = CursorLocked;

            UpdateInteraction(locked && !tool, kb);

            // Look
            if (locked && !tool && mouse != null)
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                transform.Rotate(0f, d.x, 0f);
                pitch = Mathf.Clamp(pitch - d.y, minPitch, maxPitch);
                if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                if (Mathf.Abs(Pitch.Value - pitch) > 0.5f) Pitch.Value = pitch;
            }

            // Hands
            byte hands = 0;
            if (locked && !tool && mouse != null)
            {
                if (mouse.leftButton.isPressed) hands |= 1;
                if (mouse.rightButton.isPressed) hands |= 2;
            }
            if (hands != Hands.Value) Hands.Value = hands;

            // Walk
            Vector2 input = Vector2.zero;
            if (kb != null && (locked || tool))
            {
                if (kb.wKey.isPressed) input.y += 1;
                if (kb.sKey.isPressed) input.y -= 1;
                if (kb.dKey.isPressed) input.x += 1;
                if (kb.aKey.isPressed) input.x -= 1;
            }
            Vector3 wish = (transform.forward * input.y + transform.right * input.x);
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            Vector3 horizontal = Vector3.MoveTowards(new Vector3(velocity.x, 0f, velocity.z), wish * walkSpeed, acceleration * Time.deltaTime);

            velocity.y = cc.isGrounded ? -1f : velocity.y - gravity * Time.deltaTime;
            velocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
            cc.Move(velocity * Time.deltaTime);
        }

        // What is the crosshair on? Shows a prompt, and E on the table/paper enters the drawing view.
        // (Clicking the pen / send button is handled by their own scripts, with the same reach.)
        void UpdateInteraction(bool canInteract, Keyboard kb)
        {
            InteractionHint = null;
            var cam = GetComponent<NetworkPlayer>()?.PlayerCamera;
            if (!canInteract || cam == null) return;

            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out RaycastHit hit, interactDistance, ~0, QueryTriggerInteraction.Ignore)) return;

            if (hit.collider.GetComponentInParent<global::SendPaperButton>() != null)
            {
                InteractionHint = "Click: send the paper over / back";
                return;
            }
            var pen = hit.collider.GetComponentInParent<global::PenMouseController>();
            if (pen != null)
            {
                var sync = pen.GetComponent<global::PenNetworkSync>();
                InteractionHint = sync != null && sync.IsSpawned && sync.IsHeldByOther ? "Someone else is holding this" : "Click: pick up";
                return;
            }
            var station = DrawingStation.FromHit(hit);
            if (station != null)
            {
                InteractionHint = "E: drawing view";
                if (kb != null && kb.eKey.wasPressedThisFrame)
                {
                    InteractionHint = null;
                    station.Enter(cam);
                    LockCursor(false);
                }
            }
        }

        // The lobby panel sits in the top-left; don't steal clicks meant for its buttons.
        static bool LobbyUIHasMouse()
        {
            var m = Mouse.current;
            if (m == null) return false;
            Vector2 p = m.position.ReadValue();
            return LobbyUI.PanelRect.Contains(new Vector2(p.x, Screen.height - p.y));
        }

        /// <summary>World-space direction the player is looking (works for remote players too).</summary>
        public Vector3 LookDirection
        {
            get
            {
                return Quaternion.AngleAxis(CurrentPitch, transform.right) * transform.forward;
            }
        }
    }
}
