using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Mouvement")]
    public float moveSpeed  = 5f;
    public float gravity    = -9.81f;
    public float jumpHeight = 1.2f; // Added: Adjustable height in meters

    [Header("Caméra")]
    public Transform cameraTransform;
    public float mouseSensitivity = 20f;

    [Header("References")]
    public ObjectGrabber grabber;

    private CharacterController controller;
    private Vector2 moveInput;
    private Vector2 lookInput;
    private Vector3 velocity;
    private bool    isGrounded;
    private float   xRotation = 0f;
    private bool    jumpInput; // Note: We use this to track if the button was pressed

    void Start()
    {
        controller       = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
    }

    void Update()
    {
        HandleMovement();
        HandleRotation();
    }

    private void HandleMovement()
    {
        isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0) 
        {
            velocity.y = -2f; // Keeps the player glued to the ground while moving down slopes
        }

        // Horizontal movement
        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * (moveSpeed * Time.deltaTime));

        // JUMP MECHANIC
        // Check if the player is grounded and the jump input was triggered this frame
        if (jumpInput && isGrounded)
        {
            // Physics formula to calculate initial velocity for a specific height: v = sqrt(h * -2 * g)
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
        
        // Reset the jump input flag so we don't accidentally double-jump if the button stays held down
        jumpInput = false;

        // Apply constant gravity
        velocity.y += gravity * Time.deltaTime;
        
        // Vertical movement
        controller.Move(velocity * Time.deltaTime);
    }

    private void HandleRotation()
    {
        // Lock camera only in paint mode
        if (grabber != null && grabber.IsPainting)
            return;

        float mouseX = lookInput.x * mouseSensitivity * Time.deltaTime;
        float mouseY = lookInput.y * mouseSensitivity * Time.deltaTime;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);
        cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        moveInput = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        // Discard look input only in paint mode
        if (grabber != null && grabber.IsPainting) return;
        lookInput = context.ReadValue<Vector2>();
    }
    
    public void OnJump(InputAction.CallbackContext context)
    {
        // We catch the "started" phase (button down) to make the jump instant and clean
        if (context.started)
        {
            jumpInput = true;
        }
    }
}