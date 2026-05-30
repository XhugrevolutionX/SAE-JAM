using UnityEngine;
using UnityEngine.InputSystem;

public class Painter : MonoBehaviour
{
    [Header("Paint Settings")]
    public ColorPalette palette;
    public ObjectGrabber grabber;
    public float brushSize = 0.2f;
    public float hardness  = 0.8f;
    public float range     = 10f;
    public float paintRate = 0.05f;

    private Vector2 ScrollInput;
    private int   _colorIndex    = 0;
    private float _nextPaintTime;
    [SerializeField] private Camera _cam;

    public Color CurrentColor => palette.colors[_colorIndex];

    //void Awake() => _cam = GetComponent<Camera>();

    void Update()
    {
        HandleColorSwitch();

        bool rotating = Mouse.current.rightButton.isPressed;

        // Paint when:
        // - not rotating
        // - left click held
        // - either not holding anything (free aim) OR in paint mode (cursor mode)
        bool holdingObject = grabber != null && grabber.IsHolding;
        bool inPaintMode   = grabber != null && grabber.IsPainting;
        bool canPaint      = !holdingObject || inPaintMode;

        if (canPaint && !rotating && Mouse.current.leftButton.isPressed && Time.time >= _nextPaintTime)
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
        bool inPaintMode = grabber != null && grabber.IsPainting;

        // Paint mode = visible cursor → use mouse position
        // Free aim = locked cursor → raycast from screen center like a crosshair
        Vector2 screenPoint = inPaintMode
            ? Mouse.current.position.ReadValue()
            : new Vector2(Screen.width / 2f, Screen.height / 2f);

        Ray ray = _cam.ScreenPointToRay(screenPoint);
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