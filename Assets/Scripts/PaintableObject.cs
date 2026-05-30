using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine.InputSystem;

[RequireComponent(typeof(MeshCollider))]
[RequireComponent(typeof(Renderer))]
public class PaintableObject : MonoBehaviour
{
    [Header("Textures")]
    public Texture2D cleanTexture;
    public Texture2D initialTexture;
    public Shader    brushShader;

    [Header("Settings")]
    public int   textureSize       = 1024;
    public Color baseColor         = Color.white;
    public float coverageThreshold = 0.9f;
    public float colorScoreThreshold  = 0.65f;

    [Header("Color Matching")]
    public ColorPalette palette;
    [Range(0, 255)]
    public int colorMatchTolerance = 80;

    [Header("Coverage Tuning")]
    [Range(0, 200)]
    public int paintedThreshold = 60;

    [Header("Hint")]
    [Tooltip("Press this key while looking at / holding the object to flash the expected color map")]
    public KeyCode hintKey = KeyCode.H;
    public float   hintDuration = 2f; // seconds the hint stays visible

    private const int COVERAGE_SIZE = 128;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private RenderTexture _coverageRT;
    private RenderTexture _hintRT;       // shows expected palette colors per pixel
    private Material      _brushMaterial;
    private Texture2D     _cachedColorTex;
    private Texture2D     _readbackTex;

    private bool  _isComplete      = false;
    private bool  _isDirty         = false;
    private bool  _isCounting      = false;
    private bool  _hintActive      = false;
    private float _lastCoverage    = 0f;
    private float _lastColorScore  = 0f; // separate color correctness score

    private bool[]    _uvMask;
    private int       _uvPixelCount;
    private Color32[] _initialPixels;
    private byte[][]  _expectedColors;

    public System.Action<PaintableObject>         OnComplete;
    public System.Action<PaintableObject, float>  OnCoverageChanged;
    public float LastCoverage   => _lastCoverage;
    public float LastColorScore => _lastColorScore;

    void Start()
    {
        _paintRT    = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _coverageRT = new RenderTexture(COVERAGE_SIZE, COVERAGE_SIZE, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _hintRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();
        _coverageRT.Create();
        _hintRT.Create();

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
        BakeHintTexture();

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) { Debug.LogError($"{name}: No MeshFilter!"); return; }
        Mesh mesh = mf.sharedMesh;
        if (mesh.uv == null || mesh.uv.Length == 0) { Debug.LogError($"{name}: No UVs!"); return; }
        if (!mesh.isReadable)
        {
            // Copy the mesh so we can read it
            Mesh meshCopy = Instantiate(mesh);
            mf.sharedMesh = meshCopy;
            GetComponent<MeshCollider>().sharedMesh = meshCopy;
            mesh = meshCopy;
            Debug.Log($"{name}: copied mesh to make it readable");
        }
        
        BakeUVMask(mesh);
        StartCoroutine(CoverageLoop());
    }

    void Update()
    {
        if (Keyboard.current[Key.H].wasPressedThisFrame && !_hintActive)
            StartCoroutine(ShowHint());
    }

    // ── Hint Texture ──────────────────────────────────────────────────────
    // Bakes the expected palette colors into a full-res texture.
    // When H is pressed, this replaces the paint RT temporarily
    // so the player can see exactly which color goes where.

    void BakeHintTexture()
    {
        if (_expectedColors == null || palette == null) return;

        // Build a Texture2D from expected colors at COVERAGE_SIZE, then blit to full-res hint RT
        Texture2D hintTex = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        Color32[] pixels  = new Color32[COVERAGE_SIZE * COVERAGE_SIZE];

        for (int i = 0; i < pixels.Length; i++)
        {
            if (_expectedColors[i] != null)
                pixels[i] = new Color32(_expectedColors[i][0], _expectedColors[i][1], _expectedColors[i][2], 255);
            else
                pixels[i] = new Color32(128, 128, 128, 255);
        }

        hintTex.SetPixels32(pixels);
        hintTex.Apply();
        Graphics.Blit(hintTex, _hintRT);
        Destroy(hintTex);
    }

    IEnumerator ShowHint()
    {
        if (_hintRT == null || _expectedColors == null) yield break;
        _hintActive = true;

        // Swap to hint texture
        GetComponent<Renderer>().material.SetTexture("_BaseMap", _hintRT);
        Debug.Log($"{name}: showing color hint for {hintDuration}s — these are the expected palette colors");

        yield return new WaitForSeconds(hintDuration);

        // Restore paint RT
        GetComponent<Renderer>().material.SetTexture("_BaseMap", _paintRT);
        _hintActive = false;
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

    void BuildExpectedColors()
    {
        if (cleanTexture == null || palette == null || palette.colors.Length == 0)
        {
            _expectedColors = null;
            return;
        }

        Graphics.Blit(cleanTexture, _coverageRT);
        Texture2D capture = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        RenderTexture.active = _coverageRT;
        capture.ReadPixels(new Rect(0, 0, COVERAGE_SIZE, COVERAGE_SIZE), 0, 0);
        capture.Apply();
        RenderTexture.active = null;
        Color32[] cleanPixels = capture.GetPixels32();
        Destroy(capture);

        byte[][] paletteBytes = new byte[palette.colors.Length][];
        for (int i = 0; i < palette.colors.Length; i++)
        {
            Color c = palette.colors[i];
            paletteBytes[i] = new byte[] { (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255) };
        }

        _expectedColors   = new byte[cleanPixels.Length][];
        int[] matchCounts = new int[palette.colors.Length];

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

        for (int p = 0; p < palette.colors.Length; p++)
            if (matchCounts[p] > 0)
                Debug.Log($"{name}: palette[{p}] RGB({paletteBytes[p][0]},{paletteBytes[p][1]},{paletteBytes[p][2]}) covers {matchCounts[p] * 100f / cleanPixels.Length:0.0}%");
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
            Vector2 a = uvs[tris[i]], b = uvs[tris[i+1]], c = uvs[tris[i+2]];

            Vector2Int p0 = new Vector2Int(Mathf.Clamp((int)(a.x*size), 0, size-1), Mathf.Clamp((int)(a.y*size), 0, size-1));
            Vector2Int p1 = new Vector2Int(Mathf.Clamp((int)(b.x*size), 0, size-1), Mathf.Clamp((int)(b.y*size), 0, size-1));
            Vector2Int p2 = new Vector2Int(Mathf.Clamp((int)(c.x*size), 0, size-1), Mathf.Clamp((int)(c.y*size), 0, size-1));

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
        for (int y = 1; y < size-1; y++)
            for (int x = 1; x < size-1; x++)
            {
                if (!mask[y*size+x]) continue;
                if (mask[(y-1)*size+x] && mask[(y+1)*size+x] &&
                    mask[y*size+(x-1)] && mask[y*size+(x+1)])
                    eroded[y*size+x] = true;
            }

        _uvMask       = eroded;
        _uvPixelCount = 0;
        foreach (bool b in eroded) if (b) _uvPixelCount++;
        Debug.Log($"{name}: UV mask — {_uvPixelCount}/{size*size} pixels after erosion");
    }

    bool PointInTriangle(Vector2 p, Vector2Int a, Vector2Int b, Vector2Int c)
    {
        float d1 = Sign(p,a,b), d2 = Sign(p,b,c), d3 = Sign(p,c,a);
        return !((d1<0||d2<0||d3<0) && (d1>0||d2>0||d3>0));
    }

    float Sign(Vector2 p1, Vector2Int p2, Vector2Int p3)
        => (p1.x-p3.x)*(p2.y-p3.y)-(p2.x-p3.x)*(p1.y-p3.y);

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
            byte[][]  expected  = _expectedColors;
            bool[]    uvMask    = _uvMask;
            int       total     = _uvPixelCount;
            int       tolerance = colorMatchTolerance;
            int       threshold = paintedThreshold;
            int       painted   = 0;
            int       correct   = 0;

            Task countTask = Task.Run(() =>
            {
                int countPainted = 0;
                int countCorrect = 0;

                for (int i = 0; i < pixels.Length; i++)
                {
                    if (!uvMask[i]) continue;

                    int distFromInitial = initial != null
                        ? Mathf.Abs(pixels[i].r - initial[i].r) +
                          Mathf.Abs(pixels[i].g - initial[i].g) +
                          Mathf.Abs(pixels[i].b - initial[i].b)
                        : 255;

                    // Coverage — any paint counts
                    if (distFromInitial > threshold)
                    {
                        countPainted++;

                        // Color score — only counts if also the right color
                        if (expected != null)
                        {
                            int distFromExpected = Mathf.Abs(pixels[i].r - expected[i][0]) +
                                                   Mathf.Abs(pixels[i].g - expected[i][1]) +
                                                   Mathf.Abs(pixels[i].b - expected[i][2]);
                            if (distFromExpected <= tolerance) countCorrect++;
                        }
                        else countCorrect++;
                    }
                }

                painted = countPainted;
                correct = countCorrect;
            });

            while (!countTask.IsCompleted) yield return null;
            if (countTask.IsFaulted) Debug.LogError($"Coverage task failed: {countTask.Exception}");

            // Coverage = how much is painted (any color)
            _lastCoverage   = total > 0 ? (float)painted / total : 0f;
            // Color score = how much is painted correctly
            _lastColorScore = painted > 0 ? (float)correct / painted : 0f;
            _isCounting     = false;

            Debug.Log($"{gameObject.name}: {_lastCoverage*100f:0}% coverage | {_lastColorScore*100f:0}% correct color (correct={correct}/{painted})");

            OnCoverageChanged?.Invoke(this, _lastCoverage);

            // Complete when enough is painted — color score is bonus info
            if (_lastCoverage >= coverageThreshold && _lastColorScore >= colorScoreThreshold) Complete();
        }
    }

    void Complete()
    {
        _isComplete = true;
        if (cleanTexture != null) Graphics.Blit(cleanTexture, _paintRT);
        OnComplete?.Invoke(this);
        Debug.Log($"{gameObject.name} complete! Final color score: {_lastColorScore*100f:0}%");
    }

    void OnDestroy()
    {
        _paintRT.Release();
        _tempRT.Release();
        _coverageRT.Release();
        _hintRT.Release();
        Destroy(_cachedColorTex);
        Destroy(_readbackTex);
    }
}