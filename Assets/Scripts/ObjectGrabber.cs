using UnityEngine;
using UnityEngine.InputSystem;

public class ObjectGrabber : MonoBehaviour
{
    [Header("Settings")]
    public float grabRange    = 5f;
    public float holdDistance = 2f;
    public float rotateSpeed  = 200f;
    public float smoothSpeed  = 10f;

    [Header("Keys")]
    public Key paintModeKey = Key.F; // hold F to enter paint mode

    private Camera          _cam;
    private PaintableObject _held;
    private Transform       _heldTransform;

    private Vector3    _originalPosition;
    private Quaternion _originalRotation;
    private Transform  _originalParent;
    private Quaternion _holdRotation = Quaternion.identity;

    private bool _paintMode = false;

    public bool IsHolding   => _held != null;
    public bool IsPainting  => _paintMode;

    void Awake() => _cam = GetComponent<Camera>();

    void Update()
    {
        HandleGrab();

        if (_held != null)
        {
            HandlePaintMode();
            HandleRotation();
            HoldObject();
        }
    }

    // ── Grab / Drop ───────────────────────────────────────────────────────

    void HandleGrab()
    {
        if (!Keyboard.current.eKey.wasPressedThisFrame) return;

        if (_held != null) { Drop(); return; }

        Ray ray = _cam.ScreenPointToRay(new Vector2(Screen.width / 2f, Screen.height / 2f));
        if (!Physics.Raycast(ray, out RaycastHit hit, grabRange)) return;

        PaintableObject paintable = hit.collider.GetComponent<PaintableObject>();
        if (paintable == null) return;

        Grab(paintable);
    }

    void Grab(PaintableObject obj)
    {
        _held             = obj;
        _heldTransform    = obj.transform;
        _originalPosition = _heldTransform.position;
        _originalRotation = _heldTransform.rotation;
        _originalParent   = _heldTransform.parent;
        _holdRotation     = _heldTransform.rotation;

        var rb = _heldTransform.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true;

        // Start in hold mode — camera free, cursor locked
        ExitPaintMode();

        Debug.Log($"Grabbed {obj.name} — press {paintModeKey} to enter paint mode");
    }

    void Drop()
    {
        ExitPaintMode();

        _heldTransform.position = _originalPosition;
        _heldTransform.rotation = _originalRotation;
        _heldTransform.parent   = _originalParent;

        var rb = _heldTransform.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = false;

        _held          = null;
        _heldTransform = null;

        Debug.Log("Dropped object");
    }

    // ── Paint Mode ────────────────────────────────────────────────────────

    void HandlePaintMode()
    {
        // Toggle paint mode with the paint key
        if (Keyboard.current[paintModeKey].wasPressedThisFrame)
        {
            if (_paintMode) ExitPaintMode();
            else            EnterPaintMode();
        }
    }

    void EnterPaintMode()
    {
        _paintMode       = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
        Debug.Log("Paint mode ON — camera locked, cursor free");
    }

    void ExitPaintMode()
    {
        _paintMode       = false;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
        Debug.Log("Paint mode OFF — camera free");
    }

    // ── Rotation ──────────────────────────────────────────────────────────

    void HandleRotation()
    {
        // Rotation only works in paint mode with right click
        if (!_paintMode) return;
        if (!Mouse.current.rightButton.isPressed) return;

        Vector2 delta     = Mouse.current.delta.ReadValue();
        float   dt        = Time.deltaTime;
        Vector3 upAxis    = Vector3.up;
        Vector3 rightAxis = _cam.transform.right;

        _holdRotation = Quaternion.AngleAxis(-delta.x * rotateSpeed * dt, upAxis)    * _holdRotation;
        _holdRotation = Quaternion.AngleAxis( delta.y * rotateSpeed * dt, rightAxis) * _holdRotation;
    }

    // ── Hold Position ─────────────────────────────────────────────────────

    void HoldObject()
    {
        Vector3 targetPos    = _cam.transform.position + _cam.transform.forward * holdDistance;
        _heldTransform.position = Vector3.Lerp(_heldTransform.position, targetPos, Time.deltaTime * smoothSpeed);
        _heldTransform.rotation = Quaternion.Slerp(_heldTransform.rotation, _holdRotation, Time.deltaTime * smoothSpeed);
    }
}