using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;

[RequireComponent(typeof(MeshCollider))]
[RequireComponent(typeof(Renderer))]
public class PaintableObject : MonoBehaviour
{
    [Header("Textures")] public Texture2D cleanTexture;
    public Shader brushShader;
    public Shader uvMaskShader;

    [Header("Settings")] public int textureSize = 1024;
    public Color baseColor = Color.white;
    public float coverageThreshold = 0.8f;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private Material _brushMaterial;
    private Texture2D _cachedColorTex;
    private Texture2D _readbackTex;

    private bool _isComplete = false;
    private bool _isDirty = false;
    private int _totalValidPixels = 0;
    
    private bool[] _reachableMask;

    public System.Action<PaintableObject> OnComplete;

    void Start() // Start instead of Awake — mesh is guaranteed ready
    {
        // --- RT setup ---
        _paintRT = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();

        Texture2D initTex = new Texture2D(1, 1);
        initTex.SetPixel(0, 0, baseColor);
        initTex.Apply();
        Graphics.Blit(initTex, _paintRT);
        Destroy(initTex);

        GetComponent<Renderer>().material.SetTexture("_BaseMap", _paintRT);

        _brushMaterial = new Material(brushShader);
        _cachedColorTex = new Texture2D(1, 1);
        _readbackTex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);

        // --- Mesh checks ---
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null)
        {
            Debug.LogError($"{name}: No MeshFilter!");
            return;
        }

        if (mf.sharedMesh == null)
        {
            Debug.LogError($"{name}: MeshFilter has no mesh!");
            return;
        }

        Mesh mesh = mf.sharedMesh;
        Debug.Log(
            $"{name}: mesh has {mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles, {mesh.uv.Length} UVs");

        if (mesh.uv == null || mesh.uv.Length == 0)
        {
            Debug.LogError($"{name}: Mesh has no UVs! Check Read/Write is enabled on the mesh import settings.");
            return;
        }

        BakeUVMask(mesh);
        StartCoroutine(CoverageLoop());
    }

    void BakeUVMask(Mesh mesh)
    {
        bool[] mask = new bool[textureSize * textureSize];

        // First rasterize ALL UV triangles as the base valid area
        Vector2[] uvs = mesh.uv;
        int[] tris = mesh.triangles;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector2 a = uvs[tris[i]];
            Vector2 b = uvs[tris[i + 1]];
            Vector2 c = uvs[tris[i + 2]];

            Vector2Int p0 = new Vector2Int((int)(a.x * textureSize), (int)(a.y * textureSize));
            Vector2Int p1 = new Vector2Int((int)(b.x * textureSize), (int)(b.y * textureSize));
            Vector2Int p2 = new Vector2Int((int)(c.x * textureSize), (int)(c.y * textureSize));

            int minX = Mathf.Max(0, Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)));
            int maxX = Mathf.Min(textureSize - 1, Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)));
            int minY = Mathf.Max(0, Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)));
            int maxY = Mathf.Min(textureSize - 1, Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)));

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (PointInTriangle(new Vector2(x, y), p0, p1, p2))
                    mask[y * textureSize + x] = true;
        }

        // Now filter to only REACHABLE pixels using raycasting
        bool[] reachableMask = new bool[textureSize * textureSize];
        Bounds bounds = GetComponent<Renderer>().bounds;
        Vector3 center = bounds.center;
        float radius = bounds.extents.magnitude * 2f; // cast from outside the object

        // Cast rays from many directions (fibonacci sphere for even distribution)
        int rayCount = 1000;
        for (int i = 0; i < rayCount; i++)
        {
            // Fibonacci sphere — evenly distributes points on a sphere
            float t = i / (float)rayCount;
            float inclination = Mathf.Acos(1 - 2 * t);
            float azimuth = 2 * Mathf.PI * 1.618033f * i;

            Vector3 dir = new Vector3(
                Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                Mathf.Sin(inclination) * Mathf.Sin(azimuth),
                Mathf.Cos(inclination)
            );

            Vector3 origin = center + dir * radius;
            Ray ray = new Ray(origin, -dir); // shoot inward

            if (Physics.Raycast(ray, out RaycastHit hit, radius * 2f))
            {
                // Only count hits on this object
                if (hit.collider.gameObject != gameObject) continue;

                // Mark this UV pixel as reachable
                Vector2 uv = hit.textureCoord;
                int px = Mathf.Clamp((int)(uv.x * textureSize), 0, textureSize - 1);
                int py = Mathf.Clamp((int)(uv.y * textureSize), 0, textureSize - 1);

                // Mark a small area around the hit point to account for gaps between rays
                int spread = textureSize / 128; // ~8px spread at 1024
                for (int dy = -spread; dy <= spread; dy++)
                for (int dx = -spread; dx <= spread; dx++)
                {
                    int nx = Mathf.Clamp(px + dx, 0, textureSize - 1);
                    int ny = Mathf.Clamp(py + dy, 0, textureSize - 1);
                    // Only mark if it was a valid UV pixel to begin with
                    if (mask[ny * textureSize + nx])
                        reachableMask[ny * textureSize + nx] = true;
                }
            }
        }

        // Count reachable pixels
        _totalValidPixels = 0;
        foreach (bool b in reachableMask)
            if (b)
                _totalValidPixels++;

        // Store reachable mask for coverage comparison
        _reachableMask = reachableMask;

        Debug.Log(
            $"{name}: {_totalValidPixels} reachable pixels out of {textureSize * textureSize} ({_totalValidPixels * 100f / (textureSize * textureSize):0.0}%)");
    }

    bool PointInTriangle(Vector2 p, Vector2Int a, Vector2Int b, Vector2Int c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);

        bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

        return !(hasNeg && hasPos);
    }

    float Sign(Vector2 p1, Vector2Int p2, Vector2Int p3)
    {
        return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }

    // ── Paint ─────────────────────────────────────────────────────────────

    public void Paint(Vector2 uv, Color color, float brushSize, float hardness)
    {
        if (_isComplete) return;

        _cachedColorTex.SetPixel(0, 0, color);
        _cachedColorTex.Apply();

        _brushMaterial.SetTexture("_MainTex", _paintRT);
        _brushMaterial.SetVector("_PaintPos", new Vector4(uv.x, uv.y, 0, 0));
        _brushMaterial.SetColor("_PaintColor", color);
        _brushMaterial.SetFloat("_BrushSize", brushSize);
        _brushMaterial.SetFloat("_Hardness", hardness);

        Graphics.Blit(_paintRT, _tempRT, _brushMaterial);
        Graphics.Blit(_tempRT, _paintRT);

        _isDirty = true;
    }

    // ── Coverage ──────────────────────────────────────────────────────────

    IEnumerator CoverageLoop()
    {
        while (!_isComplete)
        {
            yield return new WaitForSeconds(0.5f);
            if (!_isDirty) continue;
            _isDirty = false;

            float coverage = CalculateCoverage();
            Debug.Log($"{gameObject.name}: {coverage * 100f:0}% painted");

            if (coverage >= coverageThreshold)
                Complete();
        }
    }

    float CalculateCoverage()
    {
        if (_totalValidPixels == 0) return 0f;

        RenderTexture.active = _paintRT;
        _readbackTex.ReadPixels(new Rect(0, 0, textureSize, textureSize), 0, 0);
        _readbackTex.Apply();
        RenderTexture.active = null;

        Color[] pixels  = _readbackTex.GetPixels();
        int     painted = 0;

        for (int i = 0; i < pixels.Length; i++)
            // Only check pixels that are reachable
            if (_reachableMask[i] && ColorDistance(pixels[i], baseColor) > 0.15f)
                painted++;

        return (float)painted / _totalValidPixels;
    }

    float ColorDistance(Color a, Color b)
    {
        return (Mathf.Abs(a.r - b.r) +
                Mathf.Abs(a.g - b.g) +
                Mathf.Abs(a.b - b.b)) / 3f;
    }

    void Complete()
    {
        _isComplete = true;
        if (cleanTexture != null)
            Graphics.Blit(cleanTexture, _paintRT);
        OnComplete?.Invoke(this);
        Debug.Log($"{gameObject.name} complete!");
    }

    void OnDestroy()
    {
        _paintRT.Release();
        _tempRT.Release();
        Destroy(_cachedColorTex);
        Destroy(_readbackTex);
    }
}