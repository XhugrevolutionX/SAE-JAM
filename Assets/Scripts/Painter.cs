using UnityEngine;
using UnityEngine.InputSystem;

public class Painter : MonoBehaviour
{
    [Header("Paint Settings")]
    public Color paintColor = Color.red;
    public float brushSize  = 0.2f;
    public float hardness   = 0.8f;
    public float range      = 10f;
    public float paintRate  = 0.005f;

    private float _nextPaintTime;
    private Camera _cam;

    void Awake() => _cam = GetComponent<Camera>();

    void Update()
    {
        if (Mouse.current.leftButton.isPressed && Time.time >= _nextPaintTime)
        {
            TryPaint();
            _nextPaintTime = Time.time + paintRate;
        }
    }

    void TryPaint()
    {
        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = _cam.ScreenPointToRay(mousePos);
        if (!Physics.Raycast(ray, out RaycastHit hit, range)) return;

        PaintableObject paintable = hit.collider.GetComponent<PaintableObject>();
        if (paintable == null) return;

        paintable.Paint(hit.textureCoord, paintColor, brushSize, hardness);
    }
}