using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using System.Threading.Tasks;

[RequireComponent(typeof(Renderer))]
public class PaintableWall : MonoBehaviour
{
    [Header("Settings")]
    public int   textureSize       = 1024;
    public Color baseColor         = Color.white;
    public float coverageThreshold = 0.9f;
    public Shader brushShader;

    [Header("Coverage Tuning")]
    [Range(0, 200)]
    public int paintedThreshold = 60;

    private const int COVERAGE_SIZE = 128;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private RenderTexture _coverageRT;
    private Material      _brushMaterial;
    private Texture2D     _cachedColorTex;
    private Texture2D     _readbackTex;

    private bool  _isComplete   = false;
    private bool  _isDirty      = false;
    private bool  _isCounting   = false;
    private float _lastCoverage = 0f;

    // UV mask baked from mesh triangles
    private bool[] _uvMask;
    private int    _uvPixelCount;

    // Initial pixels for "is painted?" comparison
    private Color32[] _initialPixels;

    public System.Action<PaintableWall> OnComplete;
    public float LastCoverage => _lastCoverage;

    void Start()
    {
        _paintRT    = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _coverageRT = new RenderTexture(COVERAGE_SIZE, COVERAGE_SIZE, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();
        _coverageRT.Create();

        // Fill with base color
        Texture2D initTex = new Texture2D(1, 1);
        initTex.SetPixel(0, 0, baseColor);
        initTex.Apply();
        Graphics.Blit(initTex, _paintRT);
        Destroy(initTex);

        GetComponent<Renderer>().material.SetTexture("_BaseMap", _paintRT);
        _brushMaterial  = new Material(brushShader);
        _cachedColorTex = new Texture2D(1, 1);
        _readbackTex    = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);

        CaptureInitialPixels();

        // Get mesh — works with both MeshFilter and ProBuilder
        Mesh mesh = GetMesh();
        if (mesh == null) { Debug.LogError($"{name}: No mesh found!"); return; }

        // Make mesh readable if it isn't (handles ProBuilder meshes)
        if (!mesh.isReadable)
        {
            Mesh copy = Instantiate(mesh);
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = copy;

            // Update collider if present
            MeshCollider mc = GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = copy;

            mesh = copy;
            Debug.Log($"{name}: copied mesh to make it readable");
        }

        if (mesh.uv == null || mesh.uv.Length == 0)
        {
            Debug.LogError($"{name}: Mesh has no UVs!");
            return;
        }

        BakeUVMask(mesh);
        StartCoroutine(CoverageLoop());
    }

    // ── Mesh Helper ───────────────────────────────────────────────────────
    // Tries MeshFilter first, falls back to any mesh on the object

    Mesh GetMesh()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
        return null;
    }

    // ── Initial Pixels ────────────────────────────────────────────────────

    void CaptureInitialPixels()
    {
        Texture2D tmp = new Texture2D(1, 1);
        tmp.SetPixel(0, 0, baseColor);
        tmp.Apply();
        Graphics.Blit(tmp, _coverageRT);
        Destroy(tmp);

        Texture2D capture = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        RenderTexture.active = _coverageRT;
        capture.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
        capture.Apply();
        RenderTexture.active = null;
        _initialPixels = capture.GetPixels32();
        Destroy(capture);
    }

    // ── UV Mask ───────────────────────────────────────────────────────────

    void BakeUVMask(Mesh mesh)
    {
        int       size = COVERAGE_SIZE;
        bool[]    mask = new bool[size * size];
        Vector2[] uvs  = mesh.uv;
        int[]     tris = mesh.triangles;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector2 a = uvs[tris[i]];
            Vector2 b = uvs[tris[i + 1]];
            Vector2 c = uvs[tris[i + 2]];

            Vector2Int p0 = new Vector2Int(Mathf.Clamp((int)(a.x * size), 0, size - 1), Mathf.Clamp((int)(a.y * size), 0, size - 1));
            Vector2Int p1 = new Vector2Int(Mathf.Clamp((int)(b.x * size), 0, size - 1), Mathf.Clamp((int)(b.y * size), 0, size - 1));
            Vector2Int p2 = new Vector2Int(Mathf.Clamp((int)(c.x * size), 0, size - 1), Mathf.Clamp((int)(c.y * size), 0, size - 1));

            int minX = Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x));
            int maxX = Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x));
            int minY = Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y));
            int maxY = Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y));

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (PointInTriangle(new Vector2(x, y), p0, p1, p2))
                        mask[y * size + x] = true;
        }

        // Erode 1px to remove unreachable border pixels
        bool[] eroded = new bool[size * size];
        for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
            {
                if (!mask[y * size + x]) continue;
                if (mask[(y-1)*size+x] && mask[(y+1)*size+x] &&
                    mask[y*size+(x-1)] && mask[y*size+(x+1)])
                    eroded[y * size + x] = true;
            }

        _uvMask       = eroded;
        _uvPixelCount = 0;
        foreach (bool b in eroded) if (b) _uvPixelCount++;

        Debug.Log($"{name}: UV mask — {_uvPixelCount}/{size*size} pixels ({_uvPixelCount * 100f / (size*size):0.0}%)");
    }

    bool PointInTriangle(Vector2 p, Vector2Int a, Vector2Int b, Vector2Int c)
    {
        float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
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

            Graphics.Blit(_paintRT, _coverageRT);
            RenderTexture.active = _coverageRT;
            _readbackTex.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
            _readbackTex.Apply();
            RenderTexture.active = null;

            Color32[] pixels    = _readbackTex.GetPixels32();
            Color32[] initial   = _initialPixels;
            bool[]    uvMask    = _uvMask;
            int       total     = _uvPixelCount;
            int       threshold = paintedThreshold;
            int       painted   = 0;

            Task countTask = Task.Run(() =>
            {
                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!uvMask[i]) continue;

                    int dist = initial != null
                        ? Mathf.Abs(pixels[i].r - initial[i].r) +
                          Mathf.Abs(pixels[i].g - initial[i].g) +
                          Mathf.Abs(pixels[i].b - initial[i].b)
                        : 255;

                    if (dist > threshold) count++;
                }
                painted = count;
            });

            while (!countTask.IsCompleted) yield return null;
            if (countTask.IsFaulted) Debug.LogError($"Coverage task failed: {countTask.Exception}");

            _lastCoverage = total > 0 ? (float)painted / total : 0f;
            _isCounting   = false;

            Debug.Log($"{gameObject.name} (wall): {_lastCoverage * 100f:0}% painted ({painted}/{total})");

            if (_lastCoverage >= coverageThreshold) Complete();
        }
    }

    void Complete()
    {
        _isComplete = true;
        OnComplete?.Invoke(this);
        Debug.Log($"{gameObject.name} wall complete!");
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
