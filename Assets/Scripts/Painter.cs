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

    private int   _colorIndex    = 0;
    private float _nextPaintTime;
    [SerializeField] private Camera _cam;

    [Header("Visuals")]
    [SerializeField] private Renderer _gunRenderer;
    
    private bool _isPainting;
    private bool _isRotating;
    private Vector2 _pointerPosition;

    public Color CurrentColor => palette.colors[_colorIndex];

    void Start()
    {
        UpdateGunVisual();
    }
    void Update()
    {
        
        bool holdingObject = grabber != null && grabber.IsHolding;
        bool inPaintMode   = grabber != null && grabber.IsPainting;
        bool canPaint      = !holdingObject || inPaintMode;

        if (canPaint && !_isRotating && _isPainting && Time.time >= _nextPaintTime)
        {
            TryPaint();
            _nextPaintTime = Time.time + paintRate;
        }
    }

    void UpdateGunVisual()
    {
        if (_gunRenderer != null && palette != null && palette.colors.Length > 0)
        {
            _gunRenderer.material.color = CurrentColor;
        }
    }
    
    void TryPaint()
    {
        bool inPaintMode = grabber != null && grabber.IsPainting;

        Vector2 screenPoint = inPaintMode
            ? _pointerPosition
            : new Vector2(Screen.width / 2f, Screen.height / 2f);

        Ray ray = _cam.ScreenPointToRay(screenPoint);
        if (!Physics.Raycast(ray, out RaycastHit hit, range)) return;

        PaintableObject paintable = hit.collider.GetComponent<PaintableObject>();
        if (paintable == null) return;

        paintable.Paint(hit.textureCoord, CurrentColor, brushSize, hardness);
    }
    
    public void OnFire(InputAction.CallbackContext context)
    {
        _isPainting = context.ReadValueAsButton();
    }

    public void OnRotate(InputAction.CallbackContext context)
    {
        _isRotating = context.ReadValueAsButton();
    }

    public void OnPointerMove(InputAction.CallbackContext context)
    {
        _pointerPosition = context.ReadValue<Vector2>();
    }

    public void OnScroll(InputAction.CallbackContext context)
    {
        Vector2 scrollInput = context.ReadValue<Vector2>();

        if (scrollInput.y != 0)
        {

            if (scrollInput.y > 0) 
            {
                _colorIndex = (_colorIndex + 1) % palette.colors.Length;
            }
            else if (scrollInput.y < 0) 
            {
                _colorIndex = (_colorIndex - 1 + palette.colors.Length) % palette.colors.Length;
            }
            UpdateGunVisual();
        }
    }
}