using UnityEngine;
using UnityEngine.InputSystem;

// Put this on the pen, next to Drawing.
//  - Left-click the pen to pick it up.
//  - Move the mouse over the paper: the pen hovers above it, following the mouse.
//  - Hold left mouse button to press the pen onto the paper and draw.
//  - Right-click or Escape to put the pen back where it was.
// Runs before Drawing so the pen is already in place when Drawing checks for the paper.
[DefaultExecutionOrder(-10)]
[RequireComponent(typeof(Drawing))]
public class PenMouseController : MonoBehaviour
{
    [Tooltip("Camera used for mouse picking. Leave empty to use Camera.main.")]
    [SerializeField] Camera cam;

    [Tooltip("Same tip as on the Drawing component.")]
    [SerializeField] Transform tip;

    [Tooltip("How high (in metres) the tip floats above the paper when not pressing.")]
    [SerializeField] float hoverHeight = 0.03f;

    [Tooltip("How far (in metres) from the camera the pen floats when the mouse is not over the paper.")]
    [SerializeField] float holdDistance = 0.5f;

    [SerializeField] LayerMask paperLayers = ~0;

    readonly RaycastHit[] hits = new RaycastHit[16];
    bool held;
    bool waitForRelease;
    Vector3 startPosition;
    Quaternion startRotation;
    Rigidbody body;
    bool bodyWasKinematic;

    // Only one tool (pen, eraser, ...) can be held at a time.
    static PenMouseController current;

    public bool IsHeld => held;

    void Reset() => tip = transform;

    void Awake()
    {
        if (cam == null) cam = Camera.main;
        if (tip == null) tip = transform;
        body = GetComponent<Rigidbody>();
    }

    void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || cam == null) return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());

        if (!held)
        {
            if (mouse.leftButton.wasPressedThisFrame && ClickedOnPen(ray)) PickUp();
            return;
        }

        bool dropPressed = mouse.rightButton.wasPressedThisFrame ||
                           (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame);
        if (dropPressed)
        {
            Drop();
            return;
        }

        // The click that picked the pen up shouldn't immediately start drawing.
        if (waitForRelease && !mouse.leftButton.isPressed) waitForRelease = false;
        bool pressing = mouse.leftButton.isPressed && !waitForRelease;

        if (TryHitPaper(ray, out RaycastHit hit))
        {
            // Pressing: tip sits right on the paper (Drawing will see it and draw).
            // Not pressing: tip hovers a little above it.
            Vector3 tipTarget = hit.point + hit.normal * (pressing ? 0f : hoverHeight);
            PlaceTip(tipTarget, -hit.normal);
        }
        else
        {
            // Mouse is off the paper: just float the pen in front of the camera.
            PlaceTip(ray.GetPoint(holdDistance), ray.direction);
        }
    }

    // True if the first thing under the mouse is this tool. The tool currently being
    // held is skipped, since it floats under the mouse and would block the click.
    bool ClickedOnPen(Ray ray)
    {
        int count = Physics.RaycastNonAlloc(ray, hits, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
        Transform nearest = null;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Transform t = hits[i].collider.transform;
            if (current != null && t.IsChildOf(current.transform)) continue;
            if (hits[i].distance >= nearestDist) continue;
            nearest = t;
            nearestDist = hits[i].distance;
        }
        return nearest != null && nearest.IsChildOf(transform);
    }

    bool TryHitPaper(Ray ray, out RaycastHit best)
    {
        best = default;
        float bestDist = float.MaxValue;
        bool found = false;
        int count = Physics.RaycastNonAlloc(ray, hits, Mathf.Infinity, paperLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (hits[i].distance >= bestDist) continue;
            if (!hits[i].collider.TryGetComponent(out DrawingSurface _)) continue;
            best = hits[i];
            bestDist = hits[i].distance;
            found = true;
        }
        return found;
    }

    // Rotates the pen so the tip's forward (blue axis) points along tipForward,
    // then moves the pen so the tip ends up exactly at tipPosition.
    void PlaceTip(Vector3 tipPosition, Vector3 tipForward)
    {
        Quaternion tipOffset = Quaternion.Inverse(transform.rotation) * tip.rotation;
        Vector3 up = Vector3.ProjectOnPlane(cam.transform.up, tipForward);
        if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(cam.transform.forward, tipForward);

        transform.rotation = Quaternion.LookRotation(tipForward, up) * Quaternion.Inverse(tipOffset);
        transform.position += tipPosition - tip.position;
    }

    void PickUp()
    {
        if (current != null && current != this) current.Drop();
        current = this;
        held = true;
        waitForRelease = true;
        startPosition = transform.position;
        startRotation = transform.rotation;
        if (body != null)
        {
            bodyWasKinematic = body.isKinematic;
            body.isKinematic = true;
        }
    }

    void Drop()
    {
        if (current == this) current = null;
        held = false;
        transform.SetPositionAndRotation(startPosition, startRotation);
        if (body != null) body.isKinematic = bodyWasKinematic;
    }
}
