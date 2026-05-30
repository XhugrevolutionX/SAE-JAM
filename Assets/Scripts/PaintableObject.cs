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
    public int   textureSize          = 1024;
    public Color baseColor            = Color.white;
    public float coverageThreshold    = 0.9f;
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
    public KeyCode hintKey      = KeyCode.H;
    public float   hintDuration = 2f;

    [Header("Completion Animation")]
    public float revealDuration  = 2f;   // seconds to blend from paint to clean texture
    public float spinSpeed       = 180f; // degrees per second during reveal
    public float floatHeight     = 0.5f; // how high the object floats up during reveal
    public float settleDuration  = 0.5f; // seconds to settle back down after reveal
    public ObjectGrabber grabber;

    private const int COVERAGE_SIZE = 128;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private RenderTexture _coverageRT;
    private RenderTexture _hintRT;
    private RenderTexture _revealRT;     // blended result during reveal animation
    private Material      _brushMaterial;
    private Material      _blendMaterial; // lerps between paint and clean texture
    private Texture2D     _cachedColorTex;
    private Texture2D     _readbackTex;

    private bool  _isComplete     = false;
    private bool  _isDirty        = false;
    private bool  _isCounting     = false;
    private bool  _hintActive     = false;
    private float _lastCoverage   = 0f;
    private float _lastColorScore = 0f;

    private bool[]    _uvMask;
    private int       _uvPixelCount;
    private Color32[] _initialPixels;
    private byte[][]  _expectedColors;

    public System.Action<PaintableObject>        OnComplete;
    public System.Action<PaintableObject, float> OnCoverageChanged;
    public float LastCoverage   => _lastCoverage;
    public float LastColorScore => _lastColorScore;

    void Start()
    {
        _paintRT    = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _coverageRT = new RenderTexture(COVERAGE_SIZE, COVERAGE_SIZE, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _hintRT     = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _revealRT   = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _paintRT.Create();
        _tempRT.Create();
        _coverageRT.Create();
        _hintRT.Create();
        _revealRT.Create();

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

        // Blend material — uses the built-in Unlit shader to lerp two textures via _Color alpha
        // We drive this with Graphics.Blit and a custom lerp each frame
        _blendMaterial = new Material(Shader.Find("Hidden/BlitCopy"));
        if (_blendMaterial == null)
            _blendMaterial = new Material(brushShader); // fallback, won't blend but won't crash

        CaptureInitialPixels();
        BuildExpectedColors();
        BakeHintTexture();

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) { Debug.LogError($"{name}: No MeshFilter!"); return; }
        Mesh mesh = mf.sharedMesh;
        if (mesh.uv == null || mesh.uv.Length == 0) { Debug.LogError($"{name}: No UVs!"); return; }
        if (!mesh.isReadable)
        {
            Mesh meshCopy = Instantiate(mesh);
            mf.sharedMesh = meshCopy;
            GetComponent<MeshCollider>().sharedMesh = meshCopy;
            mesh = meshCopy;
        }

        BakeUVMask(mesh);
        StartCoroutine(CoverageLoop());
    }

    void Update()
    {
        if (Keyboard.current[Key.H].wasPressedThisFrame && !_hintActive)
            StartCoroutine(ShowHint());
    }

    // ── Hint ──────────────────────────────────────────────────────────────

    void BakeHintTexture()
    {
        if (_expectedColors == null || palette == null) return;

        Texture2D hintTex = new Texture2D(COVERAGE_SIZE, COVERAGE_SIZE, TextureFormat.RGBA32, false);
        Color32[] pixels  = new Color32[COVERAGE_SIZE * COVERAGE_SIZE];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = _expectedColors[i] != null
                ? new Color32(_expectedColors[i][0], _expectedColors[i][1], _expectedColors[i][2], 255)
                : new Color32(128, 128, 128, 255);
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
        GetComponent<Renderer>().material.SetTexture("_BaseMap", _hintRT);
        yield return new WaitForSeconds(hintDuration);
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
            paletteBytes[i] = new byte[] { (byte)(c.r*255), (byte)(c.g*255), (byte)(c.b*255) };
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
                Debug.Log($"{name}: palette[{p}] RGB({paletteBytes[p][0]},{paletteBytes[p][1]},{paletteBytes[p][2]}) covers {matchCounts[p]*100f/cleanPixels.Length:0.0}%");
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
                    if (distFromInitial > threshold)
                    {
                        countPainted++;
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

            _lastCoverage   = total > 0 ? (float)painted / total : 0f;
            _lastColorScore = painted > 0 ? (float)correct / painted : 0f;
            _isCounting     = false;

            Debug.Log($"{gameObject.name}: {_lastCoverage*100f:0}% coverage | {_lastColorScore*100f:0}% correct color (correct={correct}/{painted})");
            OnCoverageChanged?.Invoke(this, _lastCoverage);

            if (_lastCoverage >= coverageThreshold && _lastColorScore >= colorScoreThreshold)
                Complete();
        }
    }

    // ── Completion ────────────────────────────────────────────────────────

    void Complete()
    {
        _isComplete = true;
        OnComplete?.Invoke(this);
        Debug.Log($"{gameObject.name} complete! Final color score: {_lastColorScore*100f:0}%");
        StartCoroutine(RevealAnimation());
    }

    IEnumerator RevealAnimation()
    {
        if (grabber != null && grabber.IsHolding)
            grabber.Drop();
        
        yield return new WaitForSeconds(0.1f);
        
        Renderer rend    = GetComponent<Renderer>();
        Rigidbody rb     = GetComponent<Rigidbody>();
        Vector3 startPos = transform.position;
        Vector3 floatPos = startPos + Vector3.up * floatHeight;

        // Disable physics so the object floats freely
        if (rb != null) rb.isKinematic = true;

        // Disable painting and grabbing during animation
        GetComponent<MeshCollider>().enabled = false;

        // Point the renderer at the reveal RT (which we'll update each frame)
        Graphics.Blit(_paintRT, _revealRT);
        rend.material.SetTexture("_BaseMap", _revealRT);

        float elapsed = 0f;

        while (elapsed < revealDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / revealDuration);
            float smoothT = Mathf.SmoothStep(0f, 1f, t);

            // Spin the object
            transform.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);

            // Float upward during first half, stay at top during second half
            float heightT       = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2f));
            transform.position  = Vector3.Lerp(startPos, floatPos, heightT);

            // Blend texture: lerp each pixel from paint to clean using Graphics.Blit
            // We achieve this by blending on the CPU into a Texture2D and uploading.
            // Simple approach: lerp the _paintRT and cleanTexture using two blits.
            // We blit cleanTexture on top of paintRT copy with increasing alpha.
            if (cleanTexture != null)
            {
                // Use a temporary material that blends src over dst by alpha
                Material blendMat = new Material(Shader.Find("Sprites/Default"));
                blendMat.color    = new Color(1f, 1f, 1f, smoothT);
                Graphics.Blit(_paintRT,    _revealRT);           // base = current paint
                Graphics.Blit(cleanTexture, _revealRT, blendMat); // overlay clean with increasing alpha
                Destroy(blendMat);
            }

            yield return null;
        }

        // Snap to fully clean texture
        if (cleanTexture != null)
            Graphics.Blit(cleanTexture, _revealRT);

        // Settle back down to original position
        elapsed = 0f;
        Vector3 currentPos = transform.position;
        while (elapsed < settleDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / settleDuration);
            transform.position = Vector3.Lerp(currentPos, startPos, t);
            yield return null;
        }

        transform.position = startPos;

        // Re-enable collider so the object can be interacted with again
        GetComponent<MeshCollider>().enabled = true;
        if (rb != null) rb.isKinematic = false;

        Debug.Log($"{gameObject.name} reveal animation complete.");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    void OnDestroy()
    {
        _paintRT.Release();
        _tempRT.Release();
        _coverageRT.Release();
        _hintRT.Release();
        _revealRT.Release();
        Destroy(_cachedColorTex);
        Destroy(_readbackTex);
    }
}