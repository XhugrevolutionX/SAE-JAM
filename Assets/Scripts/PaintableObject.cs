using UnityEngine;
using UnityEngine.Experimental.Rendering;

[RequireComponent(typeof(MeshCollider))]
[RequireComponent(typeof(Renderer))]
public class PaintableObject : MonoBehaviour
{
    [Header("Settings")]
    public int textureSize = 1024;
    public Color baseColor = Color.white;

    private RenderTexture _paintRT;
    private RenderTexture _tempRT;
    private Material _objectMaterial;
    private Material _brushMaterial;

    void Awake()
    {
        // Create the two RTs
        _paintRT = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);
        _tempRT  = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_SRGB);

        // Fill with base color
        RenderTexture.active = _paintRT;
        GL.Clear(true, true, baseColor);
        RenderTexture.active = null;

        // Assign RT to the object's material
        _objectMaterial = GetComponent<Renderer>().material;
        _objectMaterial.SetTexture("_BaseMap", _paintRT);

        // Load brush material (we'll create this next)
        _brushMaterial = new Material(Shader.Find("Custom/PaintBrush"));
    }

    public void Paint(Vector2 uv, Color color, float brushSize, float hardness)
    {
        _brushMaterial.SetVector("_PaintPos", new Vector4(uv.x, uv.y, 0, 0));
        _brushMaterial.SetColor("_PaintColor", color);
        _brushMaterial.SetFloat("_BrushSize", brushSize);
        _brushMaterial.SetFloat("_Hardness", hardness);
        _brushMaterial.SetTexture("_MainTex", _paintRT);

        Graphics.Blit(_paintRT, _tempRT, _brushMaterial);  // apply brush stroke
        Graphics.Blit(_tempRT, _paintRT);                   // copy back
    }

    void OnDestroy()
    {
        _paintRT.Release();
        _tempRT.Release();
    }
    
}
