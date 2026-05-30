using UnityEngine;
using UnityEngine.InputSystem;

public class ObjectGrabber : MonoBehaviour
{
    [Header("Settings")]
    public float grabRange    = 5f;
    public float holdDistance = 2f;
    public float rotateSpeed = 25f;
    public float smoothSpeed  = 10f;

    [SerializeField] private Camera _cam;

    private PaintableObject _held;
    private Transform       _heldTransform;

    private Vector3    _originalPosition;
    private Quaternion _originalRotation;
    private Transform  _originalParent;
    private Quaternion _holdRotation = Quaternion.identity;

    private bool _paintMode = false;

    private bool _isRotating;
    private Vector2 _pointerDelta;

    public bool IsHolding   => _held != null;
    public bool IsPainting  => _paintMode;

    void Update()
    {
        if (_held != null)
        {
            HandleRotation();
            HoldObject();
        }
    }


    void TryGrabOrDrop()
    {
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

        ExitPaintMode();

        Debug.Log($"Grabbed {obj.name} — appuyez sur la touche Paint Mode pour peindre");
    }

    void Drop()
    {
        ExitPaintMode();

        //_heldTransform.position = _originalPosition;
        //_heldTransform.rotation = _originalRotation;
        _heldTransform.parent   = _originalParent;

        var rb = _heldTransform.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = false;

        _held          = null;
        _heldTransform = null;

        Debug.Log("Dropped object");
    }


    void TogglePaintMode()
    {
        if (_held == null) return;

        if (_paintMode) ExitPaintMode();
        else            EnterPaintMode();
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


    void HandleRotation()
    {
        if (!_paintMode || !_isRotating) return;

        float dt = Time.deltaTime;
        Vector3 upAxis    = Vector3.up;
        Vector3 rightAxis = _cam.transform.right;

        _holdRotation = Quaternion.AngleAxis(-_pointerDelta.x * rotateSpeed * dt, upAxis)    * _holdRotation;
        _holdRotation = Quaternion.AngleAxis( _pointerDelta.y * rotateSpeed * dt, rightAxis) * _holdRotation;
    }


    void HoldObject()
    {
        Vector3 targetPos    = _cam.transform.position + _cam.transform.forward * holdDistance;
        _heldTransform.position = Vector3.Lerp(_heldTransform.position, targetPos, Time.deltaTime * smoothSpeed);
        _heldTransform.rotation = Quaternion.Slerp(_heldTransform.rotation, _holdRotation, Time.deltaTime * smoothSpeed);
    }


    public void OnInteract(InputAction.CallbackContext context)
    {
        if (context.performed) TryGrabOrDrop();
    }

    public void OnTogglePaintMode(InputAction.CallbackContext context)
    {
        if (context.performed) TogglePaintMode();
    }

    public void OnRotate(InputAction.CallbackContext context)
    {
        _isRotating = context.ReadValueAsButton();
    }

    public void OnPointerDelta(InputAction.CallbackContext context)
    {
        _pointerDelta = context.ReadValue<Vector2>();
    }
}