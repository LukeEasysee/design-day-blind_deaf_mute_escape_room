using UnityEngine;

// Put this on the paper (the cube). It creates a blank texture at runtime
// and lets a pen (see Drawing.cs) paint into it.
// The paper needs a MeshCollider: only MeshColliders report where on the
// texture a raycast hit (BoxColliders always report 0,0).
[RequireComponent(typeof(Renderer))]
public class DrawingSurface : MonoBehaviour
{
    [SerializeField] int textureWidth = 1024;
    [SerializeField] int textureHeight = 1024;
    [SerializeField] Color paperColor = Color.white;

    Texture2D texture;
    bool dirty;

    public Color PaperColor => paperColor;

    /// <summary>Raised for strokes drawn on THIS machine (from, to, radius, color, isDot).
    /// PaperNetworkSync sends them to the other players.</summary>
    public event System.Action<Vector2, Vector2, int, Color32, bool> Drawn;

    void Awake()
    {
        if (!(GetComponent<Collider>() is MeshCollider))
            Debug.LogWarning($"{name}: DrawingSurface needs a MeshCollider (not a BoxCollider) so the pen can find where it touches.", this);

        texture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        Clear();

        // .material makes a per-object copy, so other objects sharing the material are unaffected.
        GetComponent<Renderer>().material.mainTexture = texture;
    }

    void LateUpdate()
    {
        if (!dirty) return;
        texture.Apply(false);
        dirty = false;
    }

    void OnDestroy()
    {
        if (texture != null) Destroy(texture);
    }

    public void Clear()
    {
        var data = texture.GetPixelData<Color32>(0);
        Color32 c = paperColor;
        for (int i = 0; i < data.Length; i++) data[i] = c;
        dirty = true;
    }

    public void DrawDot(Vector2 uv, int radius, Color32 color)
    {
        DotCore(uv, radius, color);
        Drawn?.Invoke(uv, uv, radius, color, true);
    }

    // Stamps dots along the segment so fast pen movement still gives a solid line.
    public void DrawLine(Vector2 fromUV, Vector2 toUV, int radius, Color32 color)
    {
        LineCore(fromUV, toUV, radius, color);
        Drawn?.Invoke(fromUV, toUV, radius, color, false);
    }

    /// <summary>Apply a stroke that another player drew (does not re-broadcast it).</summary>
    public void ApplyRemoteStroke(Vector2 fromUV, Vector2 toUV, int radius, Color32 color, bool isDot)
    {
        if (isDot) DotCore(fromUV, radius, color);
        else LineCore(fromUV, toUV, radius, color);
    }

    void DotCore(Vector2 uv, int radius, Color32 color)
    {
        var data = texture.GetPixelData<Color32>(0);
        Stamp(data, UVToPixel(uv), radius, color);
        dirty = true;
    }

    void LineCore(Vector2 fromUV, Vector2 toUV, int radius, Color32 color)
    {
        var data = texture.GetPixelData<Color32>(0);
        Vector2 a = UVToPixel(fromUV);
        Vector2 b = UVToPixel(toUV);
        float spacing = Mathf.Max(1f, radius * 0.5f);
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / spacing));
        for (int i = 0; i <= steps; i++)
            Stamp(data, Vector2.Lerp(a, b, i / (float)steps), radius, color);
        dirty = true;
    }

    Vector2 UVToPixel(Vector2 uv) => new Vector2(uv.x * textureWidth, uv.y * textureHeight);

    void Stamp(Unity.Collections.NativeArray<Color32> data, Vector2 center, int radius, Color32 color)
    {
        int cx = Mathf.RoundToInt(center.x);
        int cy = Mathf.RoundToInt(center.y);
        int r2 = radius * radius;
        int xMin = Mathf.Max(0, cx - radius), xMax = Mathf.Min(textureWidth - 1, cx + radius);
        int yMin = Mathf.Max(0, cy - radius), yMax = Mathf.Min(textureHeight - 1, cy + radius);

        for (int y = yMin; y <= yMax; y++)
        {
            int dy = y - cy;
            int row = y * textureWidth;
            for (int x = xMin; x <= xMax; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy <= r2) data[row + x] = color;
            }
        }
    }
}
