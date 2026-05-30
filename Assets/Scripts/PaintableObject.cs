using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using System.Threading.Tasks;

[RequireComponent(typeof(MeshCollider))]
[RequireComponent(typeof(Renderer))]
public class PaintableObject : MonoBehaviour
{
    [Header("Textures")]
    public Texture2D cleanTexture;
    public Texture2D initialTexture;
    public Shader brushShader;

    [Header("Settings")]
    public int   textureSize       = 1024;
    public Color baseColor         = Color.white;
    public float coverageThreshold = 0.8f;

    // Small RT used only for coverage readback — 64x cheaper than full size
    private const int COVERAGE_SIZE = 128;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private RenderTexture _coverageRT;  // downsampled copy for readback
    private Material      _brushMaterial;
    private Texture2D     _cachedColorTex;
    private Texture2D     _readbackTex;  // 128x128 — tiny

    private bool  _isComplete      = false;
    private bool  _isDirty         = false;
    private bool  _isCounting      = false; // prevents overlapping coverage checks
    private float _lastCoverage    = 0f;

    // Reachable mask at COVERAGE_SIZE resolution
    private bool[] _reachableMask;
    private int    _totalValidPixels = 0;

    public System.Action<PaintableObject> OnComplete;

    void Start()
    {
        // Full resolution RTs for painting
        _paintRT = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT  = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();

        // Small RT just for coverage — 128x128
        _coverageRT  = new RenderTexture(COVERAGE_SIZE, COVERAGE_SIZE, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _coverageRT.Create();

        // Initialize paint RT with base color
        if (initialTexture != null)
            Graphics.Blit(initialTexture, _paintRT);
        else
        {
            Texture2D initTex = new Texture2D(1, 1);
            initTex.SetPixel(0, 0, baseColor);
            initTex.Apply();
            Graphics.Blit(initTex, _paintRT);
            Destroy(initTex);
        }

        GetComponent<Renderer>().material.SetTexture("_BaseMap", _paintRT);

        _brushMaterial  = new Material(brushShader);
        _cachedColorTex = new Texture2D(1, 1);

        // Readback texture is now tiny — 128x128 instead of 1024x1024
        _readbackTex = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);

        // Mesh checks
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError($"{name}: Missing MeshFilter or mesh!");
            return;
        }

        Mesh mesh = mf.sharedMesh;
        if (mesh.uv == null || mesh.uv.Length == 0)
        {
            Debug.LogError($"{name}: Mesh has no UVs — enable Read/Write in import settings.");
            return;
        }

        BakeUVMask(mesh);
        StartCoroutine(CoverageLoop());
    }

    // ── UV Mask ───────────────────────────────────────────────────────────
    // Bakes at COVERAGE_SIZE resolution so the mask matches the readback texture

    void BakeUVMask(Mesh mesh)
    {
        int size = COVERAGE_SIZE;

        // Step 1 — rasterize all UV triangles into a base mask
        bool[] mask = new bool[size * size];
        Vector2[] uvs  = mesh.uv;
        int[]     tris = mesh.triangles;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector2 a = uvs[tris[i]];
            Vector2 b = uvs[tris[i + 1]];
            Vector2 c = uvs[tris[i + 2]];

            Vector2Int p0 = new Vector2Int((int)(a.x * size), (int)(a.y * size));
            Vector2Int p1 = new Vector2Int((int)(b.x * size), (int)(b.y * size));
            Vector2Int p2 = new Vector2Int((int)(c.x * size), (int)(c.y * size));

            int minX = Mathf.Max(0, Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)));
            int maxX = Mathf.Min(size - 1, Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)));
            int minY = Mathf.Max(0, Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)));
            int maxY = Mathf.Min(size - 1, Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)));

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (PointInTriangle(new Vector2(x, y), p0, p1, p2))
                        mask[y * size + x] = true;
        }

        // Step 2 — filter to reachable pixels via raycasting
        bool[]   reachable = new bool[size * size];
        Bounds   bounds    = GetComponent<Renderer>().bounds;
        Vector3  center    = bounds.center;
        float    radius    = bounds.extents.magnitude * 2f;
        int      spread    = Mathf.Max(1, size / 64); // ~2px spread at 128

        int rayCount = 1000;
        for (int i = 0; i < rayCount; i++)
        {
            float t           = i / (float)rayCount;
            float inclination = Mathf.Acos(1 - 2 * t);
            float azimuth     = 2 * Mathf.PI * 1.618033f * i;

            Vector3 dir    = new Vector3(
                Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                Mathf.Sin(inclination) * Mathf.Sin(azimuth),
                Mathf.Cos(inclination));
            Vector3 origin = center + dir * radius;

            if (Physics.Raycast(new Ray(origin, -dir), out RaycastHit hit, radius * 2f))
            {
                if (hit.collider.gameObject != gameObject) continue;

                Vector2 uv = hit.textureCoord;
                int px = Mathf.Clamp((int)(uv.x * size), 0, size - 1);
                int py = Mathf.Clamp((int)(uv.y * size), 0, size - 1);

                for (int dy = -spread; dy <= spread; dy++)
                    for (int dx = -spread; dx <= spread; dx++)
                    {
                        int nx = Mathf.Clamp(px + dx, 0, size - 1);
                        int ny = Mathf.Clamp(py + dy, 0, size - 1);
                        if (mask[ny * size + nx])
                            reachable[ny * size + nx] = true;
                    }
            }
        }

        _reachableMask = reachable;
        _totalValidPixels = 0;
        foreach (bool b in reachable)
            if (b) _totalValidPixels++;

        Debug.Log($"{name}: {_totalValidPixels} reachable pixels at {size}x{size} ({_totalValidPixels * 100f / (size * size):0.0}%)");
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
        => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

    // ── Paint ─────────────────────────────────────────────────────────────

    public void Paint(Vector2 uv, Color color, float brushSize, float hardness)
    {
        if (_isComplete) return;

        _cachedColorTex.SetPixel(0, 0, color);
        _cachedColorTex.Apply();

        _brushMaterial.SetTexture("_MainTex",  _paintRT);
        _brushMaterial.SetVector("_PaintPos",  new Vector4(uv.x, uv.y, 0, 0));
        _brushMaterial.SetColor("_PaintColor", color);
        _brushMaterial.SetFloat("_BrushSize",  brushSize);
        _brushMaterial.SetFloat("_Hardness",   hardness);

        Graphics.Blit(_paintRT, _tempRT, _brushMaterial);
        Graphics.Blit(_tempRT,  _paintRT);

        _isDirty = true;
    }

    // ── Coverage ──────────────────────────────────────────────────────────

    IEnumerator CoverageLoop()
    {
        while (!_isComplete)
        {
            yield return new WaitForSeconds(1f);
            if (!_isDirty || _isCounting) continue;
            _isDirty    = false;
            _isCounting = true;

            // Downsample to 128x128
            Graphics.Blit(_paintRT, _coverageRT);

            RenderTexture.active = _coverageRT;
            _readbackTex.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
            _readbackTex.Apply();
            RenderTexture.active = null;

            // Copy data for background thread
            Color32[] pixels   = _readbackTex.GetPixels32();
            bool[]    mask     = _reachableMask;
            int       total    = _totalValidPixels;
            byte      br       = (byte)(baseColor.r * 255);
            byte      bg       = (byte)(baseColor.g * 255);
            byte      bb       = (byte)(baseColor.b * 255);
            int       painted  = 0;

            // Run pixel counting on background thread
            Task countTask = Task.Run(() =>
            {
                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!mask[i]) continue;
                    int dist = Mathf.Abs(pixels[i].r - br) +
                               Mathf.Abs(pixels[i].g - bg) +
                               Mathf.Abs(pixels[i].b - bb);
                    if (dist > 38) count++; // 38 ≈ 0.15 * 255
                }
                painted = count;
            });

            // Wait for thread to finish without blocking main thread
            while (!countTask.IsCompleted)
                yield return null;

            if (countTask.IsFaulted)
                Debug.LogError($"Coverage task failed: {countTask.Exception}");

            _lastCoverage = total > 0 ? (float)painted / total : 0f;
            _isCounting   = false;

            Debug.Log($"{gameObject.name}: {_lastCoverage * 100f:0}% painted");

            if (_lastCoverage >= coverageThreshold)
                Complete();
        }
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
        _coverageRT.Release();
        Destroy(_cachedColorTex);
        Destroy(_readbackTex);
    }
}
