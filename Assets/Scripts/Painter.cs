using UnityEngine;
using UnityEngine.InputSystem;

public class Painter : MonoBehaviour
{
    [Header("Paint Settings")]
    public ColorPalette palette;
    public float brushSize = 0.2f;
    public float hardness  = 0.8f;
    public float range     = 10f;
    public float paintRate = 0.05f;

    private Vector2 ScrollInput;
    private int   _colorIndex    = 0;
    private float _nextPaintTime;
    private Camera _cam;

    public Color CurrentColor => palette.colors[_colorIndex];

    void Awake() => _cam = GetComponent<Camera>();

    void Update()
    {
        HandleColorSwitch();

        bool rotating = Mouse.current.rightButton.isPressed;
        
        if (!rotating && Mouse.current.leftButton.isPressed && Time.time >= _nextPaintTime)
        {
            TryPaint();
            _nextPaintTime = Time.time + paintRate;
        }
    }

    void HandleColorSwitch()
    {
        float scroll = Mouse.current.scroll.ReadValue().y;
        if (scroll > 0) _colorIndex = (_colorIndex + 1) % palette.colors.Length;
        if (scroll < 0) _colorIndex = (_colorIndex - 1 + palette.colors.Length) % palette.colors.Length;
    }

    void TryPaint()
    {
        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = _cam.ScreenPointToRay(mousePos);
        if (!Physics.Raycast(ray, out RaycastHit hit, range)) return;

        PaintableObject paintable = hit.collider.GetComponent<PaintableObject>();
        if (paintable == null) return;

        paintable.Paint(hit.textureCoord, CurrentColor, brushSize, hardness);
    }
    
    public void OnFire(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            Debug.Log("Tir effectué !");
        }
    }

    public void OnScroll(InputAction.CallbackContext context)
    {
        ScrollInput = context.ReadValue<Vector2>();
    }
}