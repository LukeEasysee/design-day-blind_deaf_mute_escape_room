using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

// Put this on a 3D button object (it needs a collider).
// Left-click: slides the paper to the other side of the wall (world Z = targetZ).
// Click again: slides it back to where it started.
public class SendPaperButton : MonoBehaviour
{
    [Tooltip("The paper object to send.")]
    [SerializeField] Transform paper;

    [Tooltip("World Z position the paper moves to on the other side of the wall.")]
    [SerializeField] float targetZ = 2.7f;

    [Tooltip("How long the move takes, in seconds. 0 = teleport instantly.")]
    [SerializeField] float moveDuration = 1f;

    [Tooltip("Camera used for mouse clicks. Leave empty to use Camera.main.")]
    [SerializeField] Camera cam;

    float homeZ;
    bool sent;

    readonly RaycastHit[] hits = new RaycastHit[16];
    const float FirstPersonReach = 3f;
    Coroutine moving;

    public bool IsSent => sent;

    PaperNetworkSync netSync;

    void Awake()
    {
        if (paper != null)
        {
            homeZ = paper.position.z;
            netSync = paper.GetComponent<PaperNetworkSync>();
            if (netSync != null) netSync.SentChanged += SetSent;
        }
        if (cam == null) cam = Camera.main;
    }

    void OnDestroy()
    {
        if (netSync != null) netSync.SentChanged -= SetSent;
    }

    void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        // In game: the local player's camera, aiming with the crosshair.
        var local = EscapeRoom.Networking.NetworkPlayer.Local;
        Camera c = local != null && local.PlayerCamera != null && local.PlayerCamera.isActiveAndEnabled ? local.PlayerCamera : cam;
        if (c == null || !c.isActiveAndEnabled) c = cam = Camera.main;
        if (c == null) return;

        if (ClickedOnButton(PenMouseController.PointerRay(c, mouse)))
            Press();
    }

    // Toggles the paper between its starting spot and the other side of the wall.
    // Pressing again mid-move simply turns it around.
    // Multiplayer: asks the server to toggle; every client then plays the same move.
    public void Press()
    {
        if (paper == null) return;
        if (netSync != null && netSync.IsSpawned)
        {
            netSync.RequestToggle();
            return;
        }
        SetSent(!sent);
    }

    void SetSent(bool value)
    {
        if (paper == null) return;
        sent = value;
        if (moving != null) StopCoroutine(moving);
        moving = StartCoroutine(MovePaper(sent ? targetZ : homeZ));
    }

    IEnumerator MovePaper(float z)
    {
        Vector3 from = paper.position;
        Vector3 to = new Vector3(from.x, from.y, z);

        for (float t = 0f; t < moveDuration; t += Time.deltaTime)
        {
            paper.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / moveDuration));
            yield return null;
        }

        paper.position = to;
        moving = null;
    }

    // True if the first thing under the mouse is this button. A pen/eraser that is
    // currently held is skipped, since it floats under the mouse and would block the click.
    bool ClickedOnButton(Ray ray)
    {
        int count = Physics.RaycastNonAlloc(ray, hits, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
        Transform nearest = null;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].distance >= nearestDist) continue;
            var tool = hits[i].collider.GetComponentInParent<PenMouseController>();
            if (tool != null && tool.IsHeld) continue;
            nearest = hits[i].collider.transform;
            nearestDist = hits[i].distance;
        }
        // First person (crosshair): only within arm's reach. Drawing view / free mouse: anywhere on screen.
        if (Cursor.lockState == CursorLockMode.Locked && nearestDist > FirstPersonReach) return false;
        return nearest != null && nearest.IsChildOf(transform);
    }
}
