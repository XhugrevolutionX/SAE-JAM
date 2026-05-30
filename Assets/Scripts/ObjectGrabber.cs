using UnityEngine;
using UnityEngine.InputSystem;

public class ObjectGrabber : MonoBehaviour
{
    [Header("Settings")]
    public float grabRange       = 5f;
    public float holdDistance    = 2f;   // how far in front of the camera
    public float rotateSpeed     = 200f;
    public float smoothSpeed     = 10f;  // how snappy the object follows

    private Camera          _cam;
    private PaintableObject _held;
    private Transform       _heldTransform;

    // Store original state to restore on drop
    private Vector3    _originalPosition;
    private Quaternion _originalRotation;
    private Transform  _originalParent;

    // Rotation accumulated while holding
    private Quaternion _holdRotation = Quaternion.identity;

    void Awake() => _cam = GetComponent<Camera>();

    void Update()
    {
        HandleGrab();

        if (_held != null)
        {
            HandleRotation();
            HoldObject();
        }
    }

    // ── Grab / Drop ───────────────────────────────────────────────────────

    void HandleGrab()
    {
        if (!Keyboard.current.eKey.wasPressedThisFrame) return;

        if (_held != null)
        {
            Drop();
            return;
        }

        // Raycast from center of screen
        Ray ray = _cam.ScreenPointToRay(new Vector2(Screen.width / 2f, Screen.height / 2f));
        if (!Physics.Raycast(ray, out RaycastHit hit, grabRange)) return;

        PaintableObject paintable = hit.collider.GetComponent<PaintableObject>();
        if (paintable == null) return;

        Grab(paintable);
    }

    void Grab(PaintableObject obj)
    {
        _held          = obj;
        _heldTransform = obj.transform;

        // Save original state
        _originalPosition = _heldTransform.position;
        _originalRotation = _heldTransform.rotation;
        _originalParent   = _heldTransform.parent;

        // Start hold rotation from current rotation
        _holdRotation = _heldTransform.rotation;

        // Disable MeshCollider so raycasts hit the object properly while held
        // but physics doesn't push it around
        var rb = _heldTransform.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true;

        Debug.Log($"Grabbed {obj.name}");
    }

    void Drop()
    {
        // Restore original position and parent
        _heldTransform.position = _originalPosition;
        _heldTransform.rotation = _originalRotation;
        _heldTransform.parent   = _originalParent;

        var rb = _heldTransform.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = false;

        Debug.Log($"Dropped {_held.name}");

        _held          = null;
        _heldTransform = null;
    }

    // ── Rotation ──────────────────────────────────────────────────────────

    void HandleRotation()
    {
        if (!Mouse.current.rightButton.isPressed) return;

        Vector2 delta = Mouse.current.delta.ReadValue();

        // Rotate around world up and camera right axes
        // so dragging left/right spins horizontally
        // and dragging up/down tilts vertically
        float   dt      = Time.deltaTime;
        Vector3 upAxis   = Vector3.up;
        Vector3 rightAxis = _cam.transform.right;

        _holdRotation = Quaternion.AngleAxis(-delta.x * rotateSpeed * dt, upAxis)    * _holdRotation;
        _holdRotation = Quaternion.AngleAxis( delta.y * rotateSpeed * dt, rightAxis) * _holdRotation;
    }

    // ── Hold Position ─────────────────────────────────────────────────────

    void HoldObject()
    {
        // Target position = directly in front of camera
        Vector3 targetPos = _cam.transform.position + _cam.transform.forward * holdDistance;

        // Smoothly move and rotate toward target
        _heldTransform.position = Vector3.Lerp(_heldTransform.position, targetPos, Time.deltaTime * smoothSpeed);
        _heldTransform.rotation = Quaternion.Slerp(_heldTransform.rotation, _holdRotation, Time.deltaTime * smoothSpeed);
    }

    // ── Public API ────────────────────────────────────────────────────────

    public bool IsHolding => _held != null;
}