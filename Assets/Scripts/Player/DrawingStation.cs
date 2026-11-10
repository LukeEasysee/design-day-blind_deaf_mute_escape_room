using UnityEngine;

namespace EscapeRoom.Player
{
    /// <summary>
    /// A spot the local player can "sit down" at to draw: look at one of the <see cref="targets"/>
    /// (table / paper) within range and press E. The player's camera moves to <see cref="viewPoint"/>
    /// (the fixed drawing view), the mouse is freed, and the pen/eraser/button work with the mouse
    /// as in the original drawing setup. E / Esc returns to first person.
    /// Only the local view changes; other players still see you standing at the table.
    /// </summary>
    public class DrawingStation : MonoBehaviour
    {
        [Tooltip("Camera pose for the drawing view.")]
        public Transform viewPoint;
        [Tooltip("Field of view while drawing.")]
        public float fieldOfView = 60f;
        [Tooltip("Looking at any of these (or this object) lets you enter the drawing view.")]
        public Collider[] targets;
        public float useDistance = 3f;
        [Tooltip("Also counts if the crosshair hits anything this close to the station (e.g. the map board on the table).")]
        public float areaRadius = 1.3f;

        /// <summary>The station the local player is currently using (null = first person).</summary>
        public static DrawingStation Active { get; private set; }

        Camera usedCamera;
        Transform cameraParent;
        Vector3 cameraLocalPos;
        Quaternion cameraLocalRot;
        float cameraFov;
        readonly System.Collections.Generic.List<Renderer> hiddenBody = new();

        public bool IsTarget(Collider c)
        {
            if (c == null) return false;
            if (c.transform.IsChildOf(transform)) return true;
            if (targets != null) foreach (var t in targets) if (t == c) return true;
            return false;
        }

        public static DrawingStation FromCollider(Collider c)
        {
            foreach (var s in FindObjectsByType<DrawingStation>())
                if (s.IsTarget(c)) return s;
            return null;
        }

        public static DrawingStation FromHit(RaycastHit hit)
        {
            var direct = FromCollider(hit.collider);
            if (direct != null) return direct;
            foreach (var s in FindObjectsByType<DrawingStation>())
                if (Vector3.Distance(hit.point, s.transform.position) <= s.areaRadius) return s;
            return null;
        }

        public void Enter(Camera cam)
        {
            if (Active != null || cam == null || viewPoint == null) return;
            usedCamera = cam;
            cameraParent = cam.transform.parent;
            cameraLocalPos = cam.transform.localPosition;
            cameraLocalRot = cam.transform.localRotation;
            cameraFov = cam.fieldOfView;

            // Hide our own body locally so it doesn't block the view (others still see us at the table).
            hiddenBody.Clear();
            var owner = cam.GetComponentInParent<EscapeRoom.Networking.NetworkPlayer>();
            if (owner != null)
                foreach (var r in owner.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { r.enabled = false; hiddenBody.Add(r); }

            cam.transform.SetParent(null, true);
            cam.transform.SetPositionAndRotation(viewPoint.position, viewPoint.rotation);
            cam.fieldOfView = fieldOfView;
            Active = this;
        }

        public void Exit()
        {
            if (Active != this) return;
            Active = null;
            if (global::PenMouseController.HeldTool != null) global::PenMouseController.HeldTool.SendMessage("Drop", SendMessageOptions.DontRequireReceiver);
            foreach (var r in hiddenBody) if (r != null) r.enabled = true;
            hiddenBody.Clear();
            if (usedCamera == null) return;
            usedCamera.transform.SetParent(cameraParent, false);
            usedCamera.transform.localPosition = cameraLocalPos;
            usedCamera.transform.localRotation = cameraLocalRot;
            usedCamera.fieldOfView = cameraFov;
            usedCamera = null;
        }

        // The level unloads on restart: give the camera back to the player.
        void OnDisable() => Exit();

        void OnDrawGizmos()
        {
            if (viewPoint == null) return;
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.8f);
            Gizmos.matrix = Matrix4x4.TRS(viewPoint.position, viewPoint.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, fieldOfView, 0.6f, 0.05f, 1.5f);
        }
    }
}
