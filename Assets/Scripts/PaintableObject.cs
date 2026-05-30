using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using System.Threading.Tasks;

[RequireComponent(typeof(MeshCollider))]
[RequireComponent(typeof(Renderer))]
public class PaintableObject : MonoBehaviour
{
    [Header("Textures")]
    public Texture2D  cleanTexture;
    public Texture2D  initialTexture;
    public Shader     brushShader;

    [Header("Settings")]
    public int   textureSize       = 1024;
    public Color baseColor         = Color.white;
    public float coverageThreshold = 0.8f;

    [Header("Color Matching")]
    public ColorPalette palette;
    [Tooltip("How many color zones to extract from the clean texture")]
    public int dominantColorCount = 4;
    [Tooltip("How close a painted color needs to be to the expected palette color (0=strict, 255=anything)")]
    [Range(0, 255)]
    public int colorMatchTolerance = 80;

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

    private bool[]    _reachableMask;
    private int       _totalValidPixels = 0;
    private Color32[] _initialPixels;

    // Per-pixel expected palette color (as byte[3])
    // Built at startup by mapping each clean texture pixel
    // to the closest available palette color
    private byte[][] _expectedColors; // [pixelIndex] = {r,g,b} of the palette color expected there

    public System.Action<PaintableObject> OnComplete;

    void Start()
    {
        _paintRT    = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _coverageRT = new RenderTexture(COVERAGE_SIZE, COVERAGE_SIZE, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();
        _coverageRT.Create();

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
        _readbackTex    = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);

        CaptureInitialPixels();
        BuildExpectedColors();

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

    // ── Initial Pixels ────────────────────────────────────────────────────

    void CaptureInitialPixels()
    {
        if (initialTexture != null)
            Graphics.Blit(initialTexture, _coverageRT);
        else
        {
            Texture2D tmp = new Texture2D(1, 1);
            tmp.SetPixel(0, 0, baseColor);
            tmp.Apply();
            Graphics.Blit(tmp, _coverageRT);
            Destroy(tmp);
        }

        Texture2D capture = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        RenderTexture.active = _coverageRT;
        capture.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
        capture.Apply();
        RenderTexture.active = null;
        _initialPixels = capture.GetPixels32();
        Destroy(capture);
    }

    // ── Expected Colors ───────────────────────────────────────────────────
    // For each pixel in the clean texture, find the closest available
    // palette color and store it. Coverage then checks against THAT color,
    // not the raw clean texture value — so only palette colors are ever expected.

    void BuildExpectedColors()
    {
        if (cleanTexture == null || palette == null || palette.colors.Length == 0)
        {
            _expectedColors = null;
            return;
        }

        // Downsample clean texture to coverage resolution
        Graphics.Blit(cleanTexture, _coverageRT);
        Texture2D capture = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        RenderTexture.active = _coverageRT;
        capture.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
        capture.Apply();
        RenderTexture.active = null;
        Color32[] cleanPixels = capture.GetPixels32();
        Destroy(capture);

        // Pre-convert palette colors to bytes for comparison
        byte[][] paletteBytes = new byte[palette.colors.Length][];
        for (int i = 0; i < palette.colors.Length; i++)
        {
            Color c = palette.colors[i];
            paletteBytes[i] = new byte[]
            {
                (byte)(c.r * 255),
                (byte)(c.g * 255),
                (byte)(c.b * 255)
            };
        }

        // For each clean texture pixel, find the closest palette color
        _expectedColors = new byte[cleanPixels.Length][];
        int[] matchCounts = new int[palette.colors.Length]; // for debug

        for (int i = 0; i < cleanPixels.Length; i++)
        {
            float bestDist = float.MaxValue;
            int   bestIdx  = 0;

            for (int p = 0; p < paletteBytes.Length; p++)
            {
                float dist = Mathf.Abs(cleanPixels[i].r - paletteBytes[p][0]) +
                             Mathf.Abs(cleanPixels[i].g - paletteBytes[p][1]) +
                             Mathf.Abs(cleanPixels[i].b - paletteBytes[p][2]);
                if (dist < bestDist) { bestDist = dist; bestIdx = p; }
            }

            _expectedColors[i] = paletteBytes[bestIdx];
            matchCounts[bestIdx]++;
        }

        // Log which palette colors are expected and how much of the surface they cover
        for (int p = 0; p < palette.colors.Length; p++)
        {
            if (matchCounts[p] > 0)
            {
                float pct = matchCounts[p] * 100f / cleanPixels.Length;
                Debug.Log($"{name}: zone mapped to palette[{p}] " +
                          $"RGB({paletteBytes[p][0]},{paletteBytes[p][1]},{paletteBytes[p][2]}) " +
                          $"covers {pct:0.0}% of surface");
            }
        }
    }

    // ── UV Mask ───────────────────────────────────────────────────────────

    void BakeUVMask(Mesh mesh)
    {
        int size = COVERAGE_SIZE;

        bool[]    mask = new bool[size * size];
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

        bool[]  reachable = new bool[size * size];
        Bounds  bounds    = GetComponent<Renderer>().bounds;
        Vector3 center    = bounds.center;
        float   radius    = bounds.extents.magnitude * 2f;
        int     spread    = Mathf.Max(1, size / 64);

        for (int i = 0; i < 1000; i++)
        {
            float t           = i / 1000f;
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

        _reachableMask    = reachable;
        _totalValidPixels = 0;
        foreach (bool b in reachable)
            if (b) _totalValidPixels++;

        Debug.Log($"{name}: {_totalValidPixels} reachable pixels ({_totalValidPixels * 100f / (size * size):0.0}%)");
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

            Graphics.Blit(_paintRT, _coverageRT);
            RenderTexture.active = _coverageRT;
            _readbackTex.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
            _readbackTex.Apply();
            RenderTexture.active = null;

            Color32[]  pixels    = _readbackTex.GetPixels32();
            bool[]     mask      = _reachableMask;
            int        total     = _totalValidPixels;
            Color32[]  initial   = _initialPixels;
            byte[][]   expected  = _expectedColors;
            int        tolerance = colorMatchTolerance;
            int        painted   = 0;

            Task countTask = Task.Run(() =>
            {
                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!mask[i]) continue;

                    // Step 1 — is this pixel painted at all?
                    int distFromInitial = initial != null
                        ? Mathf.Abs(pixels[i].r - initial[i].r) +
                          Mathf.Abs(pixels[i].g - initial[i].g) +
                          Mathf.Abs(pixels[i].b - initial[i].b)
                        : 255;

                    if (distFromInitial <= 38) continue; // not painted

                    // Step 2 — does it match the expected PALETTE color at this position?
                    if (expected != null)
                    {
                        int distFromExpected = Mathf.Abs(pixels[i].r - expected[i][0]) +
                                               Mathf.Abs(pixels[i].g - expected[i][1]) +
                                               Mathf.Abs(pixels[i].b - expected[i][2]);
                        if (distFromExpected <= tolerance) count++;
                    }
                    else
                    {
                        count++;
                    }
                }
                painted = count;
            });

            while (!countTask.IsCompleted)
                yield return null;

            if (countTask.IsFaulted)
                Debug.LogError($"Coverage task failed: {countTask.Exception}");

            _lastCoverage = total > 0 ? (float)painted / total : 0f;
            _isCounting   = false;

            Debug.Log($"{gameObject.name}: {_lastCoverage * 100f:0}% correctly painted");

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