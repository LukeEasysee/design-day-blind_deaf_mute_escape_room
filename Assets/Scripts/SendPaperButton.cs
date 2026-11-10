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
    Coroutine moving;

    public bool IsSent => sent;

    void Awake()
    {
        if (paper != null) homeZ = paper.position.z;
        if (cam == null) cam = Camera.main;
    }

    void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || cam == null || !mouse.leftButton.wasPressedThisFrame) return;

        if (ClickedOnButton(cam.ScreenPointToRay(mouse.position.ReadValue())))
            Press();
    }

    // Toggles the paper between its starting spot and the other side of the wall.
    // Pressing again mid-move simply turns it around.
    public void Press()
    {
        if (paper == null) return;
        sent = !sent;
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
        return nearest != null && nearest.IsChildOf(transform);
    }
}
