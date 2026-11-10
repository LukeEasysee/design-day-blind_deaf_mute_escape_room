using UnityEngine;

// Put this on the pen (or eraser) model. Every frame it checks whether the tip is
// touching a DrawingSurface and, if so, draws ink (or erases) where it touches.
public class Drawing : MonoBehaviour
{
    [Tooltip("Tick this on the eraser: it paints with the paper's colour instead of ink.")]
    [SerializeField] bool isEraser;

    [Tooltip("Empty child placed at the very tip of the pen. Its blue (Z) axis must point out of the tip, toward the paper.")]
    [SerializeField] Transform tip;

    [Tooltip("How close (in metres) the tip must be to the paper to count as touching.")]
    [SerializeField] float touchDistance = 0.01f;

    [Tooltip("The check starts this far back inside the pen, so it still works if the tip sinks slightly into the paper.")]
    [SerializeField] float backOffset = 0.05f;

    [Tooltip("Line thickness in texture pixels. Give the eraser a bigger value, e.g. 20.")]
    [SerializeField] int brushRadius = 4;

    [Tooltip("Ignored when Is Eraser is ticked.")]
    [SerializeField] Color inkColor = Color.black;
    [SerializeField] LayerMask paperLayers = ~0;

    // If the touch point jumps more than this (in UV space) between frames, start a new
    // stroke instead of connecting, e.g. when the tip slides over the edge onto another face.
    const float MaxStrokeJump = 0.25f;

    readonly RaycastHit[] hits = new RaycastHit[8];
    DrawingSurface lastSurface;
    Vector2 lastUV;

    public bool IsTouchingPaper => lastSurface != null;

    void Reset() => tip = transform;

    PenMouseController controller;

    void Awake() => controller = GetComponent<PenMouseController>();

    void Update()
    {
        // Multiplayer: only the player holding this tool draws. Everyone else receives
        // the strokes over the network (PaperNetworkSync), so nothing is drawn twice.
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.IsListening && controller != null && !controller.IsHeld)
        {
            lastSurface = null;
            return;
        }

        Transform t = tip != null ? tip : transform;
        Vector3 dir = t.forward;
        Vector3 origin = t.position - dir * backOffset;

        int count = Physics.RaycastNonAlloc(origin, dir, hits, backOffset + touchDistance, paperLayers, QueryTriggerInteraction.Ignore);

        DrawingSurface surface = null;
        RaycastHit best = default;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].distance >= bestDist) continue;
            if (!hits[i].collider.TryGetComponent(out DrawingSurface s)) continue;
            surface = s;
            best = hits[i];
            bestDist = hits[i].distance;
        }

        if (surface == null)
        {
            lastSurface = null; // pen lifted: next touch starts a new stroke
            return;
        }

        Vector2 uv = best.textureCoord;
        Color32 ink = isEraser ? surface.PaperColor : inkColor;
        bool continueStroke = surface == lastSurface && (uv - lastUV).magnitude < MaxStrokeJump;

        if (continueStroke) surface.DrawLine(lastUV, uv, brushRadius, ink);
        else surface.DrawDot(uv, brushRadius, ink);

        lastSurface = surface;
        lastUV = uv;
    }

    void OnDrawGizmosSelected()
    {
        Transform t = tip != null ? tip : transform;
        Gizmos.color = Color.red;
        Gizmos.DrawLine(t.position - t.forward * backOffset, t.position + t.forward * touchDistance);
    }
}
