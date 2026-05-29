using UnityEngine;

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

}
