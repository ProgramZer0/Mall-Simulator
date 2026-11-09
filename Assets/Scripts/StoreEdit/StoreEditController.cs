using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Store edit mode (GDD 11b). Goes on the player prefab next to PlayerController.
///
/// 0          enter edit mode (standing in your own store) / save and leave
/// WASD / QE  pan / turn the top-down camera, mouse wheel zooms
/// RMB drag   rotate (orbit) the camera, or pan it (Right Drag setting); RMB click deselects
/// Click      select an item in your store; click and drag an item to slide it across the floor
///            click a storefront wall section, then pick a module (wall, window, doorway, ...)
/// X          cycle Move (arrows) -> Rotate (rings) -> no handles
/// R          straight to Rotate (rings) for the selected item; again to hide them
/// Drag       an arrow to slide along that axis (stops on contact), a ring to rotate
///            (snaps to 15 degrees; hold Ctrl to turn freely)
/// G          toggle gravity for the selected item (everything hangs in place otherwise)
/// N          windows and doors (WallPiecesMenu, or a built-in list): pick one, then click a spot on
///            one of your walls. Drag them along walls to move them, R turns them around, Delete
///            takes them out (part refund). New ones are paid for when edit mode is saved.
///            Removing a built wall (B, Shift+click) also removes everything in or on it (part refund).
/// Esc        cancel the current drag / deselect
///
/// The edit camera shows only your store: everything above it (Cutaway) and everything
/// beside it (Hide Neighbours, see StoreEditIsolation) is left out of the view.
///
/// Networking: entering edit mode, the host takes the store's items out of the world and
/// sends this player a list of them; this player edits local copies, so nothing goes over
/// the network while dragging. Leaving, the final positions go back to the host, which
/// checks each one and respawns the items (anything invalid returns to where it was).
/// Nobody else can see in during renovation, so they don't need live updates.
/// </summary>
public class StoreEditController : NetworkBehaviour
{
    private enum Tool { None, Move, Rotate }

    // Items per network message, to stay well under the transport's payload limit.
    private const int ChunkSize = 64;

    [Header("Keys")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Alpha0;
    [SerializeField] private KeyCode toolKey = KeyCode.X;
    [SerializeField] private KeyCode gravityKey = KeyCode.G;
    [Tooltip("Wall tool: build walls between two clicked points (needs BuiltWallSettings in the scene).")]
    [SerializeField] private KeyCode wallToolKey = KeyCode.B;
    [Tooltip("Wall tool: switch between full-height and half-height walls.")]
    [SerializeField] private KeyCode wallHeightKey = KeyCode.H;
    [Tooltip("Hides / shows the key list in the edit HUD (remembered between games). If it's the same key " +
             "as Wall Height Key, it does nothing in the wall tool (there it switches the height).")]
    [SerializeField] private KeyCode controlsKey = KeyCode.H;
    [Tooltip("Wall tool: switch between building and removing walls (holding Shift also removes).")]
    [SerializeField] private KeyCode wallEraseKey = KeyCode.E;
    [Tooltip("Opens the list of windows and doors to buy.")]
    [SerializeField] private KeyCode openingsKey = KeyCode.N;
    [Tooltip("Takes the selected window or door out of the store (part refund).")]
    [SerializeField] private KeyCode removeKey = KeyCode.Delete;
    [Tooltip("Log (on the host) which items couldn't keep their new spot when edit mode is saved, and why.")]
    [SerializeField] private bool logEditSave = true;
    [Tooltip("Wall items dragged onto a wall from the floor view hang this high (their middle), in meters above the floor.")]
    [SerializeField] private float wallHangHeight = 1.5f;
    [Tooltip("Dragging a wall item: the mouse within this many meters of a wall (on the floor) snaps it onto the wall.")]
    [SerializeField] private float wallSnapDistance = 0.6f;

    private enum RightDrag { Orbit, Pan }

    [Header("Camera")]
    [Tooltip("Starting down-angle of the camera, in degrees.")]
    [SerializeField] private float pitch = 55f;
    [Tooltip("What holding the right mouse button and moving the mouse does.")]
    [SerializeField] private RightDrag rightDrag = RightDrag.Orbit;
    [Tooltip("Degrees the camera turns per pixel of right-drag (Orbit).")]
    [SerializeField] private float orbitSensitivity = 0.25f;
    [SerializeField] private float minPitch = 25f;
    [SerializeField] private float maxPitch = 89f;
    [Tooltip("Pixels the mouse must move before a press counts as a drag rather than a click.")]
    [SerializeField] private float clickDragThreshold = 5f;
    [SerializeField] private float startDistance = 12f;
    [SerializeField] private float minDistance = 4f;
    [SerializeField] private float maxDistance = 30f;
    [SerializeField] private float panSpeed = 10f;
    [SerializeField] private float turnSpeed = 90f;
    [SerializeField] private float zoomStep = 1.5f;
    [Tooltip("How far past the store's edges the camera may pan.")]
    [SerializeField] private float panMargin = 2f;
    [Tooltip("Layers the edit camera never draws. Rarely needed now that the cutaway hides " +
             "everything above the store; use it for odd cases like a tall sign poking into view.")]
    [SerializeField] private LayerMask hiddenInEditView;

    [Header("Hide Neighbours")]
    [Tooltip("Hide everything outside your store in the edit camera (neighbouring stores, the " +
             "hallway, other players and NPCs). Walls and floors touching your store stay visible.")]
    [SerializeField] private bool hideOutsideStore = true;
    [Tooltip("Things within this many meters of your store's space still show (e.g. its walls).")]
    [SerializeField] private float outsideMargin = 0.3f;
    [Tooltip("Index (in the URP asset's Renderer List) of the renderer that has the " +
             "'MallSim/Edit Plot Clip' full-screen pass. It cuts walls and floors exactly at the " +
             "store's edge. -1 = use the default renderer (no exact cut, whole objects only).")]
    [SerializeField] private int editRendererIndex = -1;
    [Tooltip("Fill the hidden area with a plain colour instead of the sky.")]
    [SerializeField] private bool solidBackground = true;
    [SerializeField] private Color backgroundColor = new(0.08f, 0.09f, 0.11f, 1f);

    [Header("Cutaway")]
    [Tooltip("Hide everything above the store (floor above, upstairs players, NPCs, items) " +
             "in the edit camera only.")]
    [SerializeField] private bool cutaway = true;
    [Tooltip("Moves the cut up (+) or down (-) from the store's cut height, in meters.")]
    [SerializeField] private float cutOffset = 0f;

    [Header("Handles")]
    [Tooltip("How close (pixels) the mouse must be to an arrow or ring to grab it.")]
    [SerializeField] private float grabRadiusPixels = 14f;
    [Tooltip("Clicks this many times the grab radius from a handle are ignored instead of deselecting.")]
    [SerializeField] private float nearMissFactor = 2.5f;
    [SerializeField] private float rotateDegreesPerPixel = 0.5f;
    [Tooltip("Rotating with the rings snaps to this many degrees (hold Ctrl to turn freely). 0 = never snap.")]
    [SerializeField] private float snapAngle = 15f;
    [Tooltip("Selected item: jump straight to the rotate rings.")]
    [SerializeField] private KeyCode rotateKey = KeyCode.R;
    [Tooltip("Line thickness per meter of camera distance.")]
    [SerializeField] private float lineWidth = 0.006f;

    [Header("HUD")]
    [Tooltip("Draw the built-in (IMGUI) help box while editing. Only used when there's no StoreEditHud " +
             "in the scene; with one, the Canvas HUD shows instead.")]
    [SerializeField] private bool showHud = true;

    private static readonly Color BoxColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color BoxGravityColor = new(0.3f, 0.9f, 1f, 0.9f);
    private static readonly Color SlotColor = new(1f, 0.75f, 0.2f, 0.95f);

    // Storefront section being changed (-1 = none) and its on-screen module panel.
    private int selectedSlot = -1;
    private Rect slotPanelRect;
    private string slotMessage;

    private PlayerInteractInput interact;
    private Camera playerCam;
    private Camera editCam;
    private StoreEditIsolation isolation;
    private StoreEditHandles handles;
    private StorePlot editPlot;

    // Camera
    private Vector3 pivot;
    private float yaw;
    private float camPitch;
    private float distance;
    private bool rmbHeld, rmbDragging, rmbClicked;
    private Vector2 rmbPressPos, rmbLast;

    // Click-and-drag an item across the floor
    private bool freeDragPending, freeDragging;
    private Vector2 lmbPressPos;
    private Plane freePlane;
    private Vector3 freeOffset;

    // Local copies of the store's items (index = position in the host's session list)
    private readonly List<EditLocalItem> localItems = new();
    private int clientSessionId = -1;
    private bool finishing;
    private float finishStartedAt;

    // Selection and dragging
    private EditLocalItem selected;
    private Tool tool;
    private int hoverHandle = StoreEditHandles.None;
    private int dragHandle = StoreEditHandles.None;
    private Vector3 dragStartPos;
    private Quaternion dragStartRot;
    private Vector3 dragPivot;
    private float dragStartT;
    private float rotateRaw;
    private Vector2 dragTangent;
    private Vector2 lastMouse;
    private readonly Vector3[] ringBuffer = new Vector3[StoreEditHandles.RingSegments];

    // Server: final positions arriving from the editor
    private Vector3[] pendingPositions;
    private Quaternion[] pendingRotations;
    private int pendingSessionId = -1;
    private readonly List<StorePlot.NewEditItem> pendingAdded = new();

    // Windows and doors: bought this session, taken out this session, being placed now.
    private readonly List<EditLocalItem> addedItems = new();
    private EditLocalItem placing;
    private bool placingShown;
    private bool openingsPanel;
    private Rect openingsPanelRect;
    private Vector2 openingsScroll;
    private readonly List<ItemDef> openingDefs = new();
    private string openingInfo;

    /// <summary>True while this player is in edit mode. PlayerController / PlayerInteractInput pause on it.</summary>
    public bool IsEditing => editPlot != null;

    /* ========================= LIFECYCLE ========================= */

    private void Awake()
    {
        interact = GetComponent<PlayerInteractInput>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) playerCam = GetComponentInChildren<Camera>(true);
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && IsEditing) ExitView();
        ClearLocalItems();
    }

    public override void OnDestroy()
    {
        ClearLocalItems();
        if (editCam != null) Destroy(editCam.gameObject);
        if (handles != null) Destroy(handles.gameObject);
        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsOwner || !IsSpawned) return;

        // A menu is open: ignore edit-mode keys and mouse until it closes.
        bool menuOpen = UIState.IsMenuOpen;

        if (!menuOpen && !PriceScanner.LocalTyping && (Input.GetKeyDown(toggleKey) || (toggleKey == KeyCode.Alpha0 && Input.GetKeyDown(KeyCode.Keypad0))))
        {
            if (IsEditing) Finish();
            else TryStart();
        }

        SyncEditState();
        if (!IsEditing) return;

        if (!menuOpen)
        {
            UpdateCamera();
            if (!finishing && Input.GetKeyDown(controlsKey) && !(wallTool && controlsKey == wallHeightKey))
                ControlsHidden = !ControlsHidden;
            if (!finishing) UpdateTools();
        }
        if (!finishing) CheckOpeningsAfterWallChange();
        DrawHandles();
    }

    /* ========================= ENTER / EXIT ========================= */

    private void TryStart()
    {
        var mall = MallManager.Instance;
        if (mall == null)
        {
            Debug.Log("[StoreEdit] No MallManager in the scene, so there are no stores to edit.");
            return;
        }

        var plot = mall.GetPlotAt(transform.position);
        if (plot == null || !plot.IsAuthorized(OwnerClientId))
        {
            Debug.Log("[StoreEdit] Stand inside your own store to edit it.");
            return;
        }
        if (plot.IsRenovating && plot.EditorClientId != OwnerClientId)
        {
            Debug.Log("[StoreEdit] Someone else is already editing this store.");
            return;
        }

        RequestStartEditRpc();
    }

    /// <summary>Send every local copy's final position to the host, then ask it to finish.</summary>
    private void Finish()
    {
        // Pressing 0 again after a few seconds re-sends, in case the first attempt was lost.
        if (finishing && Time.time - finishStartedAt < 3f) return;
        CancelPlacing();
        if (editPlot != null && editPlot.HasStorefrontWalls && !LocalStorefrontHasDoor())
        {
            ShowEditMessage("Your storefront needs at least one door (N to buy one).");
            return;
        }
        finishing = true;
        finishStartedAt = Time.time;
        Deselect();
        selectedSlot = -1;

        int count = localItems.Count;
        for (int start = 0; start < count; start += ChunkSize)
        {
            int n = Mathf.Min(ChunkSize, count - start);
            var positions = new Vector3[n];
            var rotations = new Quaternion[n];
            for (int k = 0; k < n; k++)
            {
                var item = localItems[start + k];
                // A missing copy sends "no position", so the host keeps that item where it was.
                positions[k] = item != null ? item.transform.position : Vector3.positiveInfinity;
                rotations[k] = item != null ? item.transform.rotation : Quaternion.identity;
            }
            SubmitEditPosesRpc(clientSessionId, start, positions, rotations);
        }

        // Windows and doors taken out or bought this session.
        var newIds = new List<int>();
        var newPositions = new List<Vector3>();
        var newRotations = new List<Quaternion>();
        var newPaints = new List<uint>();
        foreach (var item in addedItems)
        {
            if (item == null) continue;
            newIds.Add(item.Def.id);
            newPositions.Add(item.transform.position);
            newRotations.Add(item.transform.rotation);
            newPaints.Add(item.Paint);
        }
        SubmitEditChangesRpc(clientSessionId, newIds.ToArray(), newPositions.ToArray(), newRotations.ToArray(),
                             newPaints.ToArray());

        FinishEditRpc(clientSessionId);
        // The local copies stay until the host's respawned items arrive (see SyncEditState).
    }

    /// <summary>Follows the host's decision: enter/leave the view when the synced edit state changes.</summary>
    private void SyncEditState()
    {
        var target = MallManager.Instance != null ? MallManager.Instance.GetPlotEditedBy(OwnerClientId) : null;
        if (target == editPlot) return;

        if (editPlot != null) ExitView();
        if (target != null) EnterView(target);
    }

    private void EnterView(StorePlot plot)
    {
        editPlot = plot;
        checkedWallVersion = seenWallVersion = CuttableWall.Version; // only check after walls change during this session
        finishing = false;
        if (editCam == null) CreateEditCamera();
        if (handles == null) handles = StoreEditHandles.Create();

        // Hide everything outside this store from the edit camera.
        isolation.Plot = plot;
        isolation.Margin = outsideMargin;
        isolation.AlwaysShow = new[] { handles.transform };
        isolation.Background = backgroundColor;
        isolation.enabled = hideOutsideStore;
        if (hideOutsideStore && solidBackground)
        {
            editCam.clearFlags = CameraClearFlags.SolidColor;
            editCam.backgroundColor = backgroundColor;
        }
        else if (playerCam != null)
        {
            editCam.clearFlags = playerCam.clearFlags;
            editCam.backgroundColor = playerCam.backgroundColor;
        }

        Bounds b = plot.GetWorldBounds();
        pivot = new Vector3(b.center.x, b.min.y, b.center.z);
        yaw = transform.eulerAngles.y;
        camPitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        distance = Mathf.Clamp(startDistance, minDistance, maxDistance);
        rmbHeld = rmbDragging = rmbClicked = false;
        ApplyCamera();

        if (playerCam != null) playerCam.enabled = false;
        editCam.enabled = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void ExitView()
    {
        SetWallTool(false);
        Deselect();
        selectedSlot = -1;
        ClearLocalItems();
        clientSessionId = -1;
        finishing = false;

        if (handles != null) handles.HideAll();
        if (isolation != null) isolation.enabled = false; // turns everything it hid back on
        if (editCam != null)
        {
            editCam.ResetProjectionMatrix();
            editCam.enabled = false;
        }
        cutActive = false;
        if (playerCam != null) playerCam.enabled = true;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        editPlot = null;
    }

    private void CreateEditCamera()
    {
        var go = new GameObject("[StoreEditCamera]");
        editCam = go.AddComponent<Camera>();
        if (playerCam != null) editCam.CopyFrom(playerCam);
        editCam.cullingMask &= ~hiddenInEditView.value;
        editCam.enabled = false;

        isolation = go.AddComponent<StoreEditIsolation>();
        isolation.enabled = false;

        // Match the player camera's URP settings (post-processing, anti-aliasing), and switch
        // to the edit renderer that holds the exact-edge cut if one is set.
        var editData = editCam.GetUniversalAdditionalCameraData();
        if (playerCam != null && playerCam.TryGetComponent<UniversalAdditionalCameraData>(out var playerData))
        {
            editData.renderPostProcessing = playerData.renderPostProcessing;
            editData.antialiasing = playerData.antialiasing;
            editData.antialiasingQuality = playerData.antialiasingQuality;
        }
        if (editRendererIndex >= 0) editData.SetRenderer(editRendererIndex);
    }

    private void ClearLocalItems()
    {
        foreach (var item in localItems)
            if (item != null) Destroy(item.gameObject);
        localItems.Clear();
        foreach (var item in addedItems)
            if (item != null) Destroy(item.gameObject);
        addedItems.Clear();
        if (placing != null) Destroy(placing.gameObject);
        placing = null;
        reopenOpeningsMenu = false;
        SetOpeningsMenu(false);
        openingInfo = null;
        selected = null;
    }

    /* ========================= CAMERA ========================= */

    private void UpdateCamera()
    {
        float dt = Time.deltaTime;
        Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
        Vector3 fwd = flat * Vector3.forward;
        Vector3 right = flat * Vector3.right;

        float x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float z = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float speed = panSpeed * (distance / Mathf.Max(0.01f, startDistance));
        pivot += (right * x + fwd * z) * speed * dt;

        if (Input.GetKey(KeyCode.Q)) yaw -= turnSpeed * dt;
        if (Input.GetKey(KeyCode.E)) yaw += turnSpeed * dt;

        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0f) distance = Mathf.Clamp(distance - scroll * zoomStep, minDistance, maxDistance);

        // Right mouse: drag moves the camera, a plain click (no drag) deselects.
        Vector2 mouse = Input.mousePosition;
        rmbClicked = false;
        if (Input.GetMouseButtonDown(1))
        {
            rmbHeld = true;
            rmbDragging = false;
            rmbPressPos = rmbLast = mouse;
        }
        if (rmbHeld && Input.GetMouseButton(1))
        {
            if (!rmbDragging && (mouse - rmbPressPos).sqrMagnitude > clickDragThreshold * clickDragThreshold)
                rmbDragging = true;

            if (rmbDragging)
            {
                Vector2 d = mouse - rmbLast;
                if (rightDrag == RightDrag.Orbit)
                {
                    yaw += d.x * orbitSensitivity;
                    camPitch = Mathf.Clamp(camPitch - d.y * orbitSensitivity, minPitch, maxPitch);
                }
                else
                {
                    // Grab-the-floor panning: the store moves with the mouse.
                    float metersPerPixel = 2f * distance * Mathf.Tan(editCam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                           / Mathf.Max(1, Screen.height);
                    pivot -= (right * d.x + fwd * d.y) * metersPerPixel;
                }
            }
            rmbLast = mouse;
        }
        if (rmbHeld && Input.GetMouseButtonUp(1))
        {
            rmbClicked = !rmbDragging;
            rmbHeld = rmbDragging = false;
        }

        Bounds b = editPlot.GetWorldBounds();
        pivot.x = Mathf.Clamp(pivot.x, b.min.x - panMargin, b.max.x + panMargin);
        pivot.z = Mathf.Clamp(pivot.z, b.min.z - panMargin, b.max.z + panMargin);

        ApplyCamera();
    }

    private void ApplyCamera()
    {
        Quaternion rot = Quaternion.Euler(camPitch, yaw, 0f);
        editCam.transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
        ApplyCutaway();
    }

    /* ========================= CUTAWAY ========================= */

    private bool cutActive;
    private float cutY;

    /// <summary>
    /// Replaces the edit camera's near clipping plane with a flat plane at the store's cut
    /// height, so nothing above it is drawn: the slab of the floor above, and anything
    /// standing on it. Only this camera is affected; nobody else's view changes.
    /// </summary>
    private void ApplyCutaway()
    {
        editCam.ResetProjectionMatrix();
        cutActive = false;
        if (!cutaway || editPlot == null) return;

        cutY = editPlot.GetEditCutHeight() + cutOffset;

        // Only works while the camera is above the cut. When zoomed in below it, the camera
        // looks down and can't see above the cut anyway, so the normal view is fine.
        if (editCam.transform.position.y <= cutY + 0.05f) return;

        // Plane facing down: keeps everything below cutY. Expressed in camera space.
        Matrix4x4 view = editCam.worldToCameraMatrix;
        Vector3 cpos = view.MultiplyPoint(new Vector3(0f, cutY, 0f));
        Vector3 cnormal = view.MultiplyVector(Vector3.down).normalized;
        var plane = new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));

        editCam.projectionMatrix = editCam.CalculateObliqueMatrix(plane);
        cutActive = true;
    }

    /// <summary>
    /// Mouse ray for clicking, started just below the cut so it can't hit hidden things
    /// above it (like the bottom of the floor slab).
    /// </summary>
    private Ray EditRay(Vector2 mouse)
    {
        Ray ray = editCam.ScreenPointToRay(mouse);
        float start = cutY - 0.01f;
        if (cutActive && ray.direction.y < -1e-4f && ray.origin.y > start)
            ray.origin += ray.direction * ((ray.origin.y - start) / -ray.direction.y);
        return ray;
    }

    /* ========================= TOOLS ========================= */

    private void UpdateTools()
    {
        Vector2 mouse = Input.mousePosition;
        Ray ray = EditRay(mouse);

        if (dragHandle != StoreEditHandles.None)
        {
            if (selected == null) dragHandle = StoreEditHandles.None;
            else if (Input.GetKeyDown(KeyCode.Escape)) CancelDrag();
            else if (Input.GetMouseButton(0)) ContinueDrag(ray, mouse);
            else EndDrag();

            lastMouse = mouse;
            return;
        }

        if (freeDragPending || freeDragging)
        {
            UpdateFreeDrag(ray, mouse);
            lastMouse = mouse;
            return;
        }

        if (placing != null)
        {
            UpdatePlacing(ray, mouse);
            lastMouse = mouse;
            return;
        }

        if (Input.GetKeyDown(openingsKey)) SetOpeningsMenu(!OpeningsMenuOpen);

        if (Input.GetKeyDown(wallToolKey)) { SetWallTool(!wallTool); if (wallTool) SetOpeningsMenu(false); }
        if (wallTool)
        {
            UpdateWallTool(ray, mouse);
            lastMouse = mouse;
            return;
        }

        if (selected != null && selected.Opening != null)
        {
            // Windows and doors stay in their wall: no arrows, rings or gravity.
            tool = Tool.None;
            if (Input.GetKeyDown(rotateKey)) FlipOpening(selected);
            if (Input.GetKeyDown(removeKey) || Input.GetKeyDown(KeyCode.Backspace)) RemoveEditItems(new List<EditLocalItem> { selected }, null);
            else if (Input.GetKeyDown(KeyCode.Escape) || rmbClicked) Deselect();
        }
        else if (selected != null)
        {
            if (Input.GetKeyDown(removeKey))
                ShowEditMessage("Only windows and doors can be removed here.");
            if (Input.GetKeyDown(toolKey))
                tool = tool == Tool.None ? Tool.Move : tool == Tool.Move ? Tool.Rotate : Tool.None; // None -> Move -> Rotate -> None
            if (Input.GetKeyDown(rotateKey))
                tool = tool == Tool.Rotate ? Tool.None : Tool.Rotate;

            if (Input.GetKeyDown(gravityKey))
                selected.SetGravity(!selected.Gravity);

            if (Input.GetKeyDown(KeyCode.Escape) || rmbClicked)
                Deselect();
        }

        hoverHandle = PickHandle(mouse, grabRadiusPixels);

        if (selectedSlot >= 0 && (Input.GetKeyDown(KeyCode.Escape) || rmbClicked))
            selectedSlot = -1;

        if (Input.GetMouseButtonDown(0) && !IsMouseOverSlotPanel(mouse) && !IsMouseOverOpeningsPanel(mouse))
        {
            if (hoverHandle != StoreEditHandles.None)
                BeginDrag(hoverHandle, ray, mouse);
            else if (PickHandle(mouse, grabRadiusPixels * nearMissFactor) == StoreEditHandles.None)
                TrySelect(ray, mouse); // a near-miss on a handle keeps the selection instead
        }

        lastMouse = mouse;
    }

    private void TrySelect(Ray ray, Vector2 mouse)
    {
        int mask = interact != null ? interact.PickupMask.value : Physics.DefaultRaycastLayers;
        if (Physics.Raycast(ray, out RaycastHit hit, 500f, mask, QueryTriggerInteraction.Ignore))
        {
            var item = hit.collider.GetComponentInParent<EditLocalItem>();
            if (item != null)
            {
                if (item != selected) Select(item);

                // Get ready to slide it if the mouse moves before the button is released.
                freeDragPending = true;
                lmbPressPos = mouse;
                freePlane = new Plane(Vector3.up, hit.point);
                freeOffset = item.transform.position - hit.point;
                freeOffset.y = 0f;
                return;
            }
        }

        if (TryPickFrontSlot(ray, out int slot))
        {
            Deselect();
            selectedSlot = slot;
            slotMessage = null;
            return;
        }

        Deselect();
    }

    /* ========================= STOREFRONT ========================= */

    /// <summary>
    /// Did the click land on one of this store's storefront sections? The renovation wall in
    /// front of it is looked through; anything else in the way blocks the click.
    /// </summary>
    private bool TryPickFrontSlot(Ray ray, out int slot)
    {
        slot = -1;
        var hits = Physics.RaycastAll(ray, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (editPlot.IsStorefrontCover(hit.collider.transform)) continue;
            return editPlot.TryGetFrontSlot(hit.collider.transform, out slot);
        }
        return false;
    }

    // Things hung on a storefront section (as edited here). Swapping the section would leave them
    // floating, so they have to come off first. Windows and doors in it are checked after the swap:
    // any that no longer fit are taken out and refunded.
    private int CountHungOnFrontSlot(int index)
    {
        var shown = editPlot != null ? editPlot.GetFrontSlotShown(index) : null;
        if (shown == null) return 0;
        bool IsSection(Collider c) => c.transform.IsChildOf(shown.transform);
        float floor = editPlot.FloorHeight;
        int n = 0;
        void Check(EditLocalItem item)
        {
            if (item == null || !item.gameObject.activeInHierarchy || item.Opening != null) return;
            Transform t = item.transform;
            if (PlacementCheck.IsHungAgainst(item.Def, t.position, t.rotation, floor, IsSection)) n++;
        }
        foreach (var item in localItems) Check(item);
        foreach (var item in addedItems) Check(item);
        return n;
    }

    private bool IsMouseOverSlotPanel(Vector2 mouse)
    {
        if (selectedSlot < 0) return false;
        // GUI rects count y from the top; mouse positions from the bottom.
        return slotPanelRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
    }

    private void DrawSlotPanel()
    {
        var catalog = FrontWallCatalog.Instance;
        if (selectedSlot < 0 || editPlot == null || selectedSlot >= editPlot.FrontSlotCount) return;

        int count = catalog != null ? catalog.Modules.Count : 0;
        float width = Mathf.Max(360f, 20f + count * 110f);
        slotPanelRect = new Rect((Screen.width - width) * 0.5f, Screen.height - 110f, width, 96f);

        GUI.Box(slotPanelRect, GUIContent.none);
        GUI.Label(new Rect(slotPanelRect.x + 10, slotPanelRect.y + 6, width - 20, 20),
            $"Storefront section {selectedSlot + 1}/{editPlot.FrontSlotCount}: {editPlot.GetFrontModuleName(selectedSlot)}" +
            "   (Esc / RMB click to close)");

        if (catalog == null || count == 0)
        {
            GUI.Label(new Rect(slotPanelRect.x + 10, slotPanelRect.y + 32, width - 20, 40),
                "No FrontWallCatalog in the scene (or it's empty), so there's nothing to choose from.");
            return;
        }

        int current = editPlot.GetFrontModuleId(selectedSlot);
        for (int i = 0; i < count; i++)
        {
            var m = catalog.Modules[i];
            if (m == null) continue;

            var r = new Rect(slotPanelRect.x + 10 + i * 110f, slotPanelRect.y + 30, 100, 32);
            bool isCurrent = m.ModuleId == current;
            GUI.enabled = !isCurrent;
            if (GUI.Button(r, isCurrent ? $"[{m.DisplayName}]" : m.DisplayName))
            {
                int hung = CountHungOnFrontSlot(selectedSlot);
                if (editPlot.WouldRemoveLastEntrance(selectedSlot, m.ModuleId))
                    slotMessage = "Your store needs at least one doorway.";
                else if (hung > 0)
                    slotMessage = $"Take the {hung} thing{(hung == 1 ? "" : "s")} hung on this section off first.";
                else
                {
                    slotMessage = null;
                    RequestSetFrontModuleRpc(selectedSlot, m.ModuleId);
                }
            }
            GUI.enabled = true;
        }

        if (!string.IsNullOrEmpty(slotMessage))
            GUI.Label(new Rect(slotPanelRect.x + 10, slotPanelRect.y + 68, width - 20, 20), slotMessage);
    }

    /* ========================= CLICK-AND-DRAG ========================= */

    private void UpdateFreeDrag(Ray ray, Vector2 mouse)
    {
        if (selected == null)
        {
            freeDragPending = freeDragging = false;
            return;
        }

        if (freeDragPending)
        {
            if (!Input.GetMouseButton(0)) { freeDragPending = false; return; } // just a click
            if ((mouse - lmbPressPos).sqrMagnitude < clickDragThreshold * clickDragThreshold) return;

            freeDragPending = false;
            freeDragging = true;
            dragStartPos = selected.transform.position;
            dragStartRot = selected.transform.rotation;
            selected.SetDragging(true);
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            freeDragging = false;
            selected.SetPose(dragStartPos, dragStartRot);
            selected.SetFaded(false);
            selected.SetDragging(false);
            return;
        }
        if (!Input.GetMouseButton(0))
        {
            freeDragging = false;
            SettleOpening(selected, dragStartPos, dragStartRot);
            selected.SetDragging(false);
            return;
        }

        // Wall items follow the mouse onto walls (and FloorOrWall items onto floors too).
        if (DragOnSurfaces(ray)) return;

        if (!freePlane.Raycast(ray, out float enter)) return;

        Vector3 pos = selected.transform.position;
        Vector3 target = ray.GetPoint(enter) + freeOffset;
        target.y = pos.y;

        // Slide toward the mouse; on contact, keep sliding along whichever side is free.
        Vector3 reached = Step(pos, target - pos);
        Vector3 rest = target - reached;
        if (rest.sqrMagnitude > 1e-6f)
        {
            reached = Step(reached, new Vector3(rest.x, 0f, 0f));
            reached = Step(reached, new Vector3(0f, 0f, rest.z));
        }

        if ((reached - pos).sqrMagnitude > 1e-10f)
            selected.SetPose(reached, selected.transform.rotation);
    }

    private static readonly RaycastHit[] SurfaceHits = new RaycastHit[16];

    /// <summary>
    /// For items that can hang on walls: put the item where the mouse points. On a wall it hangs
    /// there (back flat on the wall, upright); on a floor or shelf top it stands there if it's a
    /// FloorOrWall item. Spots that overlap something or leave the store are skipped. Returns false
    /// for ordinary floor items, which slide as before.
    /// </summary>
    private bool DragOnSurfaces(Ray ray)
    {
        if (selected.Opening != null)
        {
            DragOpening(selected, ray);
            return true;
        }

        var def = selected.Def;
        var mounting = PlacementCheck.GetMounting(def);
        if (mounting == ItemPickupNet.Mounting.Floor) return false;

        int mask = interact != null ? interact.PlacementMask.value : Physics.DefaultRaycastLayers;
        int n = Physics.RaycastNonAlloc(ray, SurfaceHits, 500f, mask, QueryTriggerInteraction.Ignore);

        // Nearest hit that isn't the item itself.
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            if (SurfaceHits[i].collider.transform.IsChildOf(selected.transform)) continue;
            if (best < 0 || SurfaceHits[i].distance < SurfaceHits[best].distance) best = i;
        }
        if (best < 0) return true;
        var hit = SurfaceHits[best];

        Vector3 pos;
        Quaternion rot;
        if (PlacementCheck.IsWallNormal(hit.normal) && PlacementCheck.IsWallLayer(hit.collider, WallMask()))
        {
            PlacementCheck.WallPose(def, hit.point, hit.normal, false, out pos, out rot);
        }
        else if (TryNearbyWall(def, hit.point, out Vector3 wallPoint, out Vector3 wallNormal))
        {
            // From above you mostly see floor, so a wall near the mouse counts: the item stays on it
            // (at its current height) instead of dropping to the floor the moment the mouse slips off.
            PlacementCheck.WallPose(def, wallPoint, wallNormal, false, out pos, out rot);
        }
        else if (mounting == ItemPickupNet.Mounting.FloorOrWall && hit.normal.y > 0.7f)
        {
            // Off the wall onto the floor: standing the way its prefab does, facing the way it faced.
            rot = PlacementCheck.Upright(def, PlacementCheck.YawOf(def, selected.transform.rotation));
            pos = hit.point + Vector3.up * PlacementCheck.PivotHeightAboveBase(def, rot);
        }
        else return true; // wall-only item over a floor: stay where it is

        if (editPlot.ContainsItem(def, pos, rot) && PlacementCheck.IsClear(def, pos, rot, EditMask(), selected.transform))
            selected.SetPose(pos, rot);
        return true;
    }

    private static readonly Vector3[] SnapDirs =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
        new(0.7071f, 0, 0.7071f), new(-0.7071f, 0, 0.7071f), new(0.7071f, 0, -0.7071f), new(-0.7071f, 0, -0.7071f),
    };

    /// <summary>
    /// A wall within Wall Snap Distance of a floor point, found at hanging height: the selected item's
    /// current middle if it's already on a wall, otherwise Wall Hang Height above the floor.
    /// </summary>
    private bool TryNearbyWall(ItemDef def, Vector3 floorPoint, out Vector3 wallPoint, out Vector3 wallNormal)
    {
        wallPoint = wallNormal = default;
        float hangY = editPlot.FloorHeight + wallHangHeight;
        if (PlacementCheck.HasWallBehind(def, selected.transform.position, selected.transform.rotation, WallMask()) &&
            PlacementCheck.GetTestBox(def, selected.transform.position, selected.transform.rotation, out Vector3 c, out _))
            hangY = c.y;

        Vector3 from = new(floorPoint.x, hangY, floorPoint.z);
        float best = float.MaxValue;
        foreach (var dir in SnapDirs)
        {
            if (!Physics.Raycast(from, dir, out RaycastHit h, wallSnapDistance, WallMask(), QueryTriggerInteraction.Ignore)) continue;
            if (!PlacementCheck.IsWallNormal(h.normal) || h.collider.transform.IsChildOf(selected.transform) || h.distance >= best) continue;
            best = h.distance;
            wallPoint = h.point;
            wallNormal = h.normal;
        }
        return best < float.MaxValue;
    }

    /// <summary>Move the selected item by delta, stopping at the first thing it touches.</summary>
    private Vector3 Step(Vector3 from, Vector3 delta)
    {
        float dist = delta.magnitude;
        if (dist < 1e-5f) return from;

        Quaternion rot = selected.transform.rotation;
        Vector3 dir = delta / dist;
        float allowed = PlacementCheck.CastDistance(selected.Def, from, rot, dir, dist, EditMask(), selected.transform);
        Vector3 next = from + dir * allowed;
        return editPlot.ContainsItem(selected.Def, next, rot) ? next : from;
    }

    private void Select(EditLocalItem item)
    {
        Deselect();
        selectedSlot = -1;
        selected = item;
        tool = Tool.None;
    }

    private void Deselect()
    {
        if (dragHandle != StoreEditHandles.None) CancelDrag();
        if (freeDragging && selected != null) selected.SetDragging(false);
        freeDragPending = freeDragging = false;
        hoverHandle = StoreEditHandles.None;
        selected = null;
        tool = Tool.None;
        if (handles != null) handles.HideAll();
    }

    /* ========================= DRAGGING ========================= */

    private void BeginDrag(int handle, Ray ray, Vector2 mouse)
    {
        GetHandleGeometry(out Vector3 center, out _, out float radius);
        Vector3 axis = StoreEditHandles.Axes[handle];

        dragStartPos = selected.transform.position;
        dragStartRot = selected.transform.rotation;
        dragPivot = center;
        lastMouse = mouse;

        if (tool == Tool.Move)
        {
            dragStartT = AxisParam(ray, dragPivot, axis);
            if (float.IsNaN(dragStartT)) return; // looking straight down the axis
        }
        else
        {
            rotateRaw = 0f;

            // Turn the ring's on-screen direction at the grabbed spot into the drag direction.
            StoreEditHandles.FillRing(center, axis, radius, ringBuffer);
            Vector3 grab = ringBuffer[0];
            float best = float.MaxValue;
            foreach (var p in ringBuffer)
            {
                if (!ToScreen(p, out Vector2 s)) continue;
                float d = (s - mouse).sqrMagnitude;
                if (d < best) { best = d; grab = p; }
            }

            Vector3 tangent = Vector3.Cross(axis, grab - center).normalized;
            dragTangent = Vector2.right;
            if (ToScreen(grab, out Vector2 a) && ToScreen(grab + tangent * 0.25f, out Vector2 b) &&
                (b - a).sqrMagnitude > 1e-4f)
                dragTangent = (b - a).normalized;
        }

        dragHandle = handle;
        selected.SetDragging(true);
    }

    private void ContinueDrag(Ray ray, Vector2 mouse)
    {
        Vector3 axis = StoreEditHandles.Axes[dragHandle];
        Vector3 pos = selected.transform.position;
        Quaternion rot = selected.transform.rotation;
        int mask = EditMask();

        if (tool == Tool.Move)
        {
            float t = AxisParam(ray, dragPivot, axis);
            if (float.IsNaN(t)) return;

            // Slide from where the item is now toward the mouse, stopping at the first contact.
            Vector3 target = dragStartPos + axis * (t - dragStartT);
            Vector3 delta = target - pos;
            float dist = delta.magnitude;
            if (dist < 1e-5f) return;

            Vector3 dir = delta / dist;
            float allowed = PlacementCheck.CastDistance(selected.Def, pos, rot, dir, dist, mask, selected.transform);
            Vector3 next = pos + dir * allowed;
            if (editPlot.ContainsItem(selected.Def, next, rot)) selected.SetPose(next, rot);
        }
        else
        {
            rotateRaw += Vector2.Dot(mouse - lastMouse, dragTangent) * rotateDegreesPerPixel;
            bool snap = !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)); // Ctrl = free
            float angle = snap && snapAngle > 0f ? Mathf.Round(rotateRaw / snapAngle) * snapAngle : rotateRaw;

            Quaternion q = Quaternion.AngleAxis(angle, axis);
            Quaternion nextRot = q * dragStartRot;
            Vector3 nextPos = dragPivot + q * (dragStartPos - dragPivot); // spin around the item's middle

            // Rotations that would push into something are skipped, so nothing ends up overlapping.
            if (PlacementCheck.IsClear(selected.Def, nextPos, nextRot, mask, selected.transform) &&
                editPlot.ContainsItem(selected.Def, nextPos, nextRot))
                selected.SetPose(nextPos, nextRot);
        }
    }

    private void EndDrag()
    {
        dragHandle = StoreEditHandles.None;
        if (selected != null) selected.SetDragging(false);
    }

    private void CancelDrag()
    {
        dragHandle = StoreEditHandles.None;
        if (selected == null) return;
        selected.SetPose(dragStartPos, dragStartRot);
        selected.SetDragging(false);
    }

    /* ========================= HANDLES ========================= */

    private void DrawHandles()
    {
        if (handles == null) return;

        // A storefront section is selected: outline it.
        if (selected == null && selectedSlot >= 0 && !finishing && selectedSlot < editPlot.FrontSlotCount)
        {
            Bounds sb = editPlot.GetFrontSlotBounds(selectedSlot);
            float w = Vector3.Distance(editCam.transform.position, sb.center) * lineWidth;
            handles.HideArrows();
            handles.HideRings();
            handles.DrawBox(sb.center, sb.extents, Quaternion.identity, SlotColor, w * 0.8f);
            return;
        }

        if (selected == null || finishing) { handles.HideAll(); return; }

        Vector3 pos = selected.transform.position;
        Quaternion rot = selected.transform.rotation;
        GetHandleGeometry(out Vector3 center, out float length, out float radius);
        float width = Vector3.Distance(editCam.transform.position, center) * lineWidth;

        if (PlacementCheck.GetTestBox(selected.Def, pos, rot, out Vector3 boxCenter, out Vector3 half))
            handles.DrawBox(boxCenter, half, rot, selected.Gravity ? BoxGravityColor : BoxColor, width * 0.6f);
        else
            handles.HideBox();

        switch (tool)
        {
            case Tool.Move:
                handles.DrawArrows(center, length, editCam.transform.forward, hoverHandle, dragHandle, width);
                handles.HideRings();
                break;
            case Tool.Rotate:
                handles.DrawRings(center, radius, hoverHandle, dragHandle, width);
                handles.HideArrows();
                break;
            default:
                handles.HideArrows();
                handles.HideRings();
                break;
        }
    }

    private void GetHandleGeometry(out Vector3 center, out float arrowLength, out float ringRadius)
    {
        float size = 0.5f;
        center = selected.transform.position;
        if (PlacementCheck.GetTestBox(selected.Def, selected.transform.position, selected.transform.rotation,
                                      out Vector3 c, out Vector3 half))
        {
            center = c;
            size = Mathf.Max(half.x, Mathf.Max(half.y, half.z));
        }
        arrowLength = size + 0.6f;
        ringRadius = size * 1.15f + 0.3f;
    }

    private int PickHandle(Vector2 mouse, float radiusPixels)
    {
        if (selected == null || tool == Tool.None) return StoreEditHandles.None;

        GetHandleGeometry(out Vector3 center, out float length, out float radius);
        int best = StoreEditHandles.None;
        float bestDist = radiusPixels;

        for (int i = 0; i < 3; i++)
        {
            Vector3 axis = StoreEditHandles.Axes[i];
            float d = float.MaxValue;

            if (tool == Tool.Move)
            {
                if (ToScreen(center, out Vector2 a) && ToScreen(center + axis * length, out Vector2 b))
                    d = DistanceToSegment(mouse, a, b);
            }
            else
            {
                StoreEditHandles.FillRing(center, axis, radius, ringBuffer);
                for (int k = 0; k < ringBuffer.Length; k++)
                {
                    if (ToScreen(ringBuffer[k], out Vector2 a) &&
                        ToScreen(ringBuffer[(k + 1) % ringBuffer.Length], out Vector2 b))
                        d = Mathf.Min(d, DistanceToSegment(mouse, a, b));
                }
            }

            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /* ========================= WINDOWS AND DOORS ========================= */

    private bool OpeningsMenuOpen =>
        WallPiecesMenu.Instance != null ? WallPiecesMenu.Instance.IsOpen : openingsPanel;

    /// <summary>The list of windows and doors: the Canvas menu if there is one, the built-in one otherwise.</summary>
    private void SetOpeningsMenu(bool open)
    {
        var menu = WallPiecesMenu.Instance;
        if (open) SetWallTool(false);
        if (menu != null)
        {
            if (open) menu.Open(StartPlacing, () => MoneyLeft(), PendingCost);
            else if (menu.IsOpen) menu.Close();
            openingsPanel = false;
            return;
        }
        openingsPanel = open;
        if (open) WallPiecesMenu.FindPieces(openingDefs);
    }

    /// <summary>Money left after paying for what's been placed this session.</summary>
    private int MoneyLeft()
    {
        var econ = EconomyManager.Instance;
        if (econ == null || !econ.IsSpawned) return int.MaxValue;
        return econ.GetMoneyOf(OwnerClientId) - PendingCost();
    }

    /// <summary>What the windows and doors bought this session will cost when it's saved.</summary>
    private int PendingCost()
    {
        int cost = 0;
        foreach (var item in addedItems)
            if (item != null) cost += MarketModifiers.OrderCost(item.Def);
        return cost;
    }

    private bool IsMouseOverOpeningsPanel(Vector2 mouse)
    {
        if (WallPiecesMenu.Instance != null && WallPiecesMenu.Instance.IsOpen)
            return UnityEngine.EventSystems.EventSystem.current != null &&
                   UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        return openingsPanel && openingsPanelRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
    }

    private void DrawOpeningsPanel()
    {
        if (!openingsPanel) return;

        const float rowHeight = 34f;
        float w = 280f;
        float h = Mathf.Clamp(84f + openingDefs.Count * rowHeight, 110f, Mathf.Max(110f, Screen.height - 230f));
        openingsPanelRect = new Rect(Screen.width - w - 10f, 10f, w, h);
        float x = openingsPanelRect.x, y = openingsPanelRect.y;

        GUI.Box(openingsPanelRect, GUIContent.none);
        GUI.Label(new Rect(x + 10, y + 6, w - 20, 20), $"WINDOWS & DOORS   ({openingsKey} to close)");

        if (openingDefs.Count == 0)
        {
            GUI.Label(new Rect(x + 10, y + 30, w - 20, 60),
                "None in the catalog. Add a WallOpening to an item's world prefab.");
            return;
        }

        int pending = PendingCost();
        int left = MoneyLeft();

        var view = new Rect(0f, 0f, w - 40f, openingDefs.Count * rowHeight);
        openingsScroll = GUI.BeginScrollView(new Rect(x + 8, y + 30, w - 16, h - 60), openingsScroll, view);
        for (int i = 0; i < openingDefs.Count; i++)
        {
            var def = openingDefs[i];
            int cost = MarketModifiers.OrderCost(def);
            bool door = WallOpening.TryGet(def, out var o) && o.IsDoor;
            GUI.enabled = cost <= left;
            if (GUI.Button(new Rect(0f, i * rowHeight, w - 40f, rowHeight - 4f), $"{def.displayName}{(door ? " (door)" : "")}   ${cost}"))
                StartPlacing(def);
            GUI.enabled = true;
        }
        GUI.EndScrollView();

        GUI.Label(new Rect(x + 10, y + h - 26, w - 20, 20), $"Paid when you save: ${pending}");
    }

    // The windows & doors list was open when placing started: it comes back when placing stops.
    private bool reopenOpeningsMenu;

    /// <summary>
    /// A piece was picked from the list: the list hides and that piece follows the mouse along the walls.
    /// Every click places one and the next one is ready straight away; RMB or Esc stops and brings the
    /// list back.
    /// </summary>
    private void StartPlacing(ItemDef def)
    {
        if (OpeningsMenuOpen)
        {
            reopenOpeningsMenu = true;
            SetOpeningsMenu(false);
        }
        CancelPlacing();
        Deselect();
        selectedSlot = -1;
        // Hidden until it's over a wall it fits in.
        placing = EditLocalItem.Create(def, -1, new Vector3(0f, -10000f, 0f), Quaternion.identity);
        placing.gameObject.SetActive(false);
        openingInfo = "Point at one of your store's walls";
    }

    private void CancelPlacing()
    {
        if (placing != null) Destroy(placing.gameObject);
        placing = null;
    }

    /// <summary>A window or door from the list follows the mouse along walls; click to put it there.</summary>
    private void UpdatePlacing(Ray ray, Vector2 mouse)
    {
        if (Input.GetKeyDown(KeyCode.Escape) || rmbClicked)
        {
            StopPlacing(null);
            return;
        }

        bool valid = DragOpening(placing, ray);
        if (!Input.GetMouseButtonDown(0) || !valid || !placing.gameObject.activeSelf) return;

        // Placed: it's bought on save. The next one of the same piece is ready right away.
        var item = placing;
        placing = null;
        item.SetFaded(false);
        addedItems.Add(item);
        var def = item.Def;
        int cost = MarketModifiers.OrderCost(def);
        if (MoneyLeft() >= cost)
        {
            StartPlacing(def);
            openingInfo = $"Placed (${cost} on save). Click to place another, RMB / Esc to stop";
        }
        else StopPlacing("Not enough money for another");
    }

    /// <summary>Stop placing (RMB / Esc, or out of money) and bring the list back if it was open.</summary>
    private void StopPlacing(string message)
    {
        CancelPlacing();
        openingInfo = null;
        if (!string.IsNullOrEmpty(message)) ShowEditMessage(message);
        if (reopenOpeningsMenu)
        {
            reopenOpeningsMenu = false;
            SetOpeningsMenu(true);
        }
    }

    /// <summary>
    /// Move a window or door to the wall spot under the mouse, if it fits there. It keeps facing the
    /// same way on the wall it's in; on a new wall it faces the camera (R turns it around).
    /// </summary>
    private bool DragOpening(EditLocalItem item, Ray ray)
    {
        var o = item.Opening;
        if (!PickWallSpot(ray, item, out var wall, out float u, out float v))
        {
            openingInfo = "Point at one of your store's walls";
            item.SetFaded(true);
            return false;
        }

        var settings = BuiltWallSettings.Instance;
        float step = settings != null ? settings.OpeningSnap : 0.25f;
        bool free = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (!free && step > 0f)
        {
            u = Mathf.Round(u / step) * step;
            v = Mathf.Round(v / step) * step;
        }

        bool onThisWall = item.gameObject.activeSelf && CuttableWall.HostOf(o) == wall;
        bool flip = onThisWall
            ? Vector3.Dot(o.Current.forward, wall.Normal) < 0f
            : Vector3.Dot(editCam.transform.forward, wall.Normal) > 0f;

        bool fits = wall.TryFit(o, u, v, flip, false, out Vector3 pos, out Quaternion rot, out string reason);
        if (fits && PlacementCheck.FindOpeningBlocker(item.Def, pos, rot, EditMask(), item.transform, false, editPlot) != null)
        {
            fits = false;
            reason = "Something is in the way";
        }

        // Shown where the mouse is either way; faded where it can't go. Letting go there puts it
        // back at the last spot that worked.
        if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);
        item.SetPose(pos, rot);
        item.SetFaded(!fits);
        openingInfo = fits ? null : reason;
        if (fits)
        {
            lastValidOpening = item;
            lastValidOpeningPos = pos;
            lastValidOpeningRot = rot;
        }
        return fits;
    }

    // The last spot that worked for the window or door being moved (to go back to if dropped where it can't go).
    private EditLocalItem lastValidOpening;
    private Vector3 lastValidOpeningPos;
    private Quaternion lastValidOpeningRot;

    /// <summary>A dragged window or door let go where it can't go: back to its last good spot.</summary>
    private void SettleOpening(EditLocalItem item, Vector3 fallbackPos, Quaternion fallbackRot)
    {
        if (item == null || item.Opening == null) return;
        if (item.Faded)
        {
            if (lastValidOpening == item) item.SetPose(lastValidOpeningPos, lastValidOpeningRot);
            else item.SetPose(fallbackPos, fallbackRot);
            openingInfo = "It can't go there";
        }
        item.SetFaded(false);
    }

    /// <summary>
    /// The spot on one of this store's walls under the mouse (u along it, v up it). From above you
    /// mostly see floor, so a wall near the mouse on the floor counts too.
    /// </summary>
    private bool PickWallSpot(Ray ray, EditLocalItem item, out CuttableWall wall, out float u, out float v)
    {
        wall = null;
        u = v = 0f;
        float best = float.MaxValue;
        foreach (var w in CuttableWall.All)
        {
            if (w == null || w.Plot != editPlot) continue;
            if (w.Raycast(ray, out float wu, out float wv, out float d) && d < best)
            {
                best = d;
                wall = w;
                u = wu;
                v = wv;
            }
        }
        if (wall != null) return true;

        if (!new Plane(Vector3.up, new Vector3(0f, editPlot.FloorHeight, 0f)).Raycast(ray, out float enter)) return false;
        Vector3 floorPoint = ray.GetPoint(enter);
        best = wallSnapDistance;
        foreach (var w in CuttableWall.All)
        {
            if (w == null || w.Plot != editPlot) continue;
            Vector3 c = w.ToWall(floorPoint);
            if (c.x < 0f || c.x > w.Length) continue;
            float d = Mathf.Abs(c.z) - w.Thickness * 0.5f;
            if (d < best)
            {
                best = d;
                wall = w;
                u = c.x;
            }
        }
        if (wall == null) return false;

        // Keep its height if it's already in this wall; otherwise hanging height.
        var host = item.gameObject.activeSelf ? CuttableWall.HostOf(item.Opening) : null;
        v = host == wall ? wall.ToWall(item.Opening.Current.center).y : wall.FloorV + wallHangHeight;
        return true;
    }

    /// <summary>R: turn a window or door to face the other side of its wall.</summary>
    private void FlipOpening(EditLocalItem item)
    {
        var o = item.Opening;
        var wall = CuttableWall.HostOf(o);
        if (wall == null) return;

        Vector3 c = wall.ToWall(o.Current.center);
        bool flip = Vector3.Dot(o.Current.forward, wall.Normal) >= 0f; // facing out of Normal now: turn it
        if (wall.TryFit(o, c.x, c.y, flip, false, out Vector3 pos, out Quaternion rot, out string reason) &&
            PlacementCheck.FindOpeningBlocker(item.Def, pos, rot, EditMask(), item.transform, false, editPlot) == null)
        {
            item.SetPose(pos, rot);
            openingInfo = null;
        }
        else openingInfo = reason ?? "Something is in the way";
    }

    /// <summary>
    /// Take items out of the store: ones placed this session are just cancelled; the rest are sold back
    /// to the host straight away (part refund), then the wall (if any) comes down.
    /// </summary>
    private void RemoveEditItems(List<EditLocalItem> items, int? wallId)
    {
        Deselect();
        var indices = new List<int>();
        int refund = 0, count = 0;
        string firstName = null;
        foreach (var item in items)
        {
            if (item == null) continue;
            count++;
            firstName ??= item.Def.displayName;
            if (!addedItems.Remove(item) && item.Index >= 0)
            {
                indices.Add(item.Index);
                refund += StorePlot.RemovalRefund(item.Def);
                if (item.Index < localItems.Count) localItems[item.Index] = null;
            }
            Destroy(item.gameObject);
        }

        if (indices.Count > 0) RequestRemoveEditItemsRpc(clientSessionId, indices.ToArray());
        if (wallId.HasValue) RequestRemoveWallRpc(wallId.Value);

        openingInfo = null;
        if (refund > 0)
            ShowEditMessage(count == 1 && !wallId.HasValue
                ? $"{firstName} removed: ${refund} back."
                : $"Removed {count} item{(count == 1 ? "" : "s")}: ${refund} back.");
    }

    /// <summary>
    /// Everything in or on a built wall: windows and doors in it, and wall items (pictures, signs) hung
    /// on it. Things standing on the floor against it don't count.
    /// </summary>
    private List<EditLocalItem> ItemsOnWall(BuiltWallPiece piece)
    {
        var result = new List<EditLocalItem>();
        var cuttable = piece.GetComponent<CuttableWall>();
        float floor = editPlot.FloorHeight;
        bool IsThisWall(Collider c) => c.GetComponentInParent<BuiltWallPiece>() == piece;

        void Check(EditLocalItem item)
        {
            if (item == null || !item.gameObject.activeInHierarchy) return;
            if (item.Opening != null)
            {
                if (cuttable != null && CuttableWall.HostOf(item.Opening) == cuttable) result.Add(item);
                return;
            }
            // Hung on it (whichever way the model is built): its box touches this wall, off the floor.
            Transform t = item.transform;
            if (PlacementCheck.IsHungAgainst(item.Def, t.position, t.rotation, floor, IsThisWall)) result.Add(item);
        }

        foreach (var item in localItems) Check(item);
        foreach (var item in addedItems) Check(item);
        return result;
    }

    /// <summary>Is there a door in this store's front wall (as edited here)?</summary>
    private bool LocalStorefrontHasDoor()
    {
        bool IsFrontDoor(WallOpening o)
        {
            if (o == null || !o.IsDoor || !o.isActiveAndEnabled) return false;
            var host = CuttableWall.HostOf(o);
            return host != null && host.Plot == editPlot && host.IsStorefront;
        }

        foreach (var item in localItems) if (item != null && IsFrontDoor(item.Opening)) return true;
        foreach (var item in addedItems) if (item != null && IsFrontDoor(item.Opening)) return true;
        // Doors that weren't part of the session (the real items are gone while editing, so only these).
        foreach (var o in WallOpening.All) if (!o.IsEditCopy && IsFrontDoor(o)) return true;
        return false;
    }

    /* ========================= WALL TOOL ========================= */

    private bool wallTool;
    private bool hasWallStart;
    private Vector3 wallStart;
    private bool wallHalf;
    private string wallInfo = "";
    private GameObject wallGhost;
    private GameObject wallGhostTemplate;
    private readonly List<Material> wallGhostMats = new();
    private bool? wallGhostShownValid;
    private static readonly Collider[] WallHits = new Collider[32];
    private static readonly Color WallGhostValid = new(0.6f, 0.9f, 1f, 0.45f);
    private static readonly Color WallGhostInvalid = new(1f, 0.3f, 0.3f, 0.35f);

    private void SetWallTool(bool on)
    {
        wallTool = on;
        hasWallStart = false;
        erasing = false;
        wallInfo = "";
        if (on)
        {
            Deselect();
            selectedSlot = -1;
        }
        else
        {
            DestroyWallGhost();
            DestroyEraseGhosts();
        }
    }

    /// <summary>
    /// Click a point, then another: a wall goes between them (ends on the grid, straight or 45
    /// degrees). The next wall starts where the last one ended. Shift+click a built wall removes it.
    /// </summary>
    private void UpdateWallTool(Ray ray, Vector2 mouse)
    {
        var settings = BuiltWallSettings.Instance;
        if (settings == null || settings.WallPrefab == null)
        {
            wallInfo = "Add BuiltWallSettings (with a Wall Prefab) to the MallManager object.";
            if (Input.GetKeyDown(KeyCode.Escape) || rmbClicked) SetWallTool(false);
            return;
        }

        if (Input.GetKeyDown(wallHeightKey)) wallHalf = !wallHalf;
        if (Input.GetKeyDown(wallEraseKey))
        {
            eraseMode = !eraseMode;
            erasing = false;
            hasWallStart = false;
            HideEraseGhosts();
        }

        bool cancel = Input.GetKeyDown(KeyCode.Escape) || rmbClicked;
        if (erasing && cancel)
        {
            erasing = false; // drop the erase, stay in the tool
            HideEraseGhosts();
            wallInfo = "";
            return;
        }
        if (eraseMode && cancel)
        {
            eraseMode = false; // back to building
            HideEraseGhosts();
            wallInfo = "";
            return;
        }
        if (cancel)
        {
            if (hasWallStart) hasWallStart = false; // stop the chain
            else { SetWallTool(false); return; }
        }

        bool click = Input.GetMouseButtonDown(0) && !IsMouseOverSlotPanel(mouse);

        // Remove mode (E, or hold Shift): see UpdateErase.
        if (erasing || eraseMode || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            DestroyWallGhost();
            UpdateErase(ray, click);
            return;
        }
        HideEraseGhosts();

        // The point under the mouse on the store's floor, snapped to the grid.
        float floorY = editPlot.FloorHeight;
        if (!new Plane(Vector3.up, new Vector3(0f, floorY, 0f)).Raycast(ray, out float enter)) return;
        Vector3 point = settings.SnapToGrid(ray.GetPoint(enter));
        point.y = floorY;

        float height = wallHalf ? Mathf.Min(settings.HalfHeight, editPlot.BuiltWallFullHeight) : editPlot.BuiltWallFullHeight;
        string heightName = wallHalf ? "half" : "full";

        if (!hasWallStart)
        {
            // A post where the wall will start. Just outside the edge is fine: the wall gets slid onto it.
            bool inside = editPlot.IsInsideOrNear(point + Vector3.up * 0.1f, settings.GridSize * 0.5f + 0.01f);
            ShowWallGhost(point, point, height, inside);
            wallInfo = inside ? $"Click to start a {heightName} wall" : "Start inside your store";
            if (click && inside)
            {
                wallStart = point;
                hasWallStart = true;
            }
            return;
        }

        Vector3 end = settings.SnapEnd(wallStart, point);
        // Ends right by the store's edge reach it (the host does the same).
        Vector3 fitA = wallStart, fitB = end;
        editPlot.FitWallToEdges(ref fitA, ref fitB);
        float length = Vector3.Distance(fitA, fitB);

        // Drawn over or into walls on the same line: they join into one wall (only new length is paid).
        bool valid = editPlot.CanBuildWall(fitA, fitB, out string reason);
        StorePlot.WallPlan plan = default;
        if (valid) valid = editPlot.PlanWall(fitA, fitB, height, out plan, out reason);
        int cost = valid ? settings.GetCost(plan.addedLength, wallHalf) : settings.GetCost(length, wallHalf);
        if (valid && !CanAffordWall(cost)) { valid = false; reason = "Not enough money"; }
        if (valid && WallHitsEditItems(settings, fitA, fitB, height, plan.joins)) { valid = false; reason = "An item is in the way"; }

        // Show the whole wall it'll make.
        if (valid) ShowWallGhost(plan.a, plan.b, height, true);
        else ShowWallGhost(fitA, fitB, height, false);
        string joins = valid && plan.joins.Count > 0
            ? $", joins {plan.joins.Count} wall{(plan.joins.Count == 1 ? "" : "s")}"
            : "";
        wallInfo = valid
            ? $"{plan.addedLength:0.0} m {heightName} wall{joins}: ${cost}   (click to build)"
            : $"{length:0.0} m: {reason}";

        if (click && valid)
        {
            RequestBuildWallRpc(wallStart, end, wallHalf);
            wallStart = end; // chain: the next wall starts here
        }
    }

    /* ---------- Removing walls ---------- */

    private bool eraseMode;       // E: the wall tool removes instead of builds
    private bool erasing;         // first spot chosen
    private bool eraseDragging;   // mouse still held since the first spot
    private Vector2 erasePressMouse;
    private Vector3 eraseStart;
    private BuiltWall eraseLine;
    private readonly List<StorePlot.WallSpan> eraseSpans = new();
    private readonly List<GameObject> eraseGhosts = new();
    private readonly List<Material> eraseGhostMats = new();
    private GameObject eraseGhostTemplate;

    /// <summary>
    /// Removing walls (E toggles it; holding Shift works too). The wall under the mouse shows red.
    /// Click a spot on a wall to start, then click a second spot along the same line: everything
    /// between them shows red and is removed, leaving the rest of each wall standing. Clicking the same
    /// spot twice removes that whole wall. Pressing and dragging works as well. Spots slide along the
    /// wall in grid steps, and pointing at the wall itself or the floor beside it both work. Windows,
    /// doors and wall items on what's removed go too. Everything is part refunded.
    /// </summary>
    private void UpdateErase(Ray ray, bool click)
    {
        var settings = BuiltWallSettings.Instance;
        Vector2 mouse = Input.mousePosition;

        if (!erasing)
        {
            eraseSpans.Clear();
            bool onWall = TryEraseSpot(ray, null, out Vector3 spot, out BuiltWall wall);
            if (onWall) eraseSpans.Add(StorePlot.WallSpan.Whole(wall));
            ShowEraseGhosts(eraseSpans);
            wallInfo = onWall
                ? "Click a spot on this wall, then a second one: what's between goes. Same spot twice: the whole wall"
                : "Point at a built wall (or the floor beside it)";
            if (click && onWall)
            {
                erasing = true;
                eraseStart = spot;
                eraseLine = wall;
                erasePressMouse = mouse;
                eraseDragging = true;
            }
            return;
        }

        if (!TryEraseSpot(ray, eraseLine, out Vector3 end, out _)) end = eraseStart;
        bool sameSpot = (end - eraseStart).sqrMagnitude < 1e-4f;
        eraseSpans.Clear();
        if (!sameSpot) editPlot.GetEraseSpans(eraseStart, end, eraseSpans);
        else if (editPlot.TryGetBuiltWallById(eraseLine.id, out var whole)) eraseSpans.Add(StorePlot.WallSpan.Whole(whole));

        var items = ItemsInSpans(eraseSpans);

        // Things hung on the wall have to come off first (windows and doors in it go with it).
        int hung = 0;
        foreach (var item in items) if (item.Opening == null) hung++;
        if (hung > 0)
        {
            ShowEraseGhosts(eraseSpans);
            wallInfo = $"{hung} thing{(hung == 1 ? " is" : "s are")} hung on this wall: take {(hung == 1 ? "it" : "them")} off first   (Esc: cancel)";
            // Same timing as removing: a second click, or letting go after a drag, ends it (removing nothing).
            bool letGo = eraseDragging && !Input.GetMouseButton(0);
            if (letGo)
            {
                eraseDragging = false;
                if ((mouse - erasePressMouse).sqrMagnitude <= 12f * 12f) return; // it was a click: wait for the second one
            }
            else if (!click || eraseDragging) return;
            erasing = false;
            HideEraseGhosts();
            ShowEditMessage("Take what's hung on that wall off first.");
            return;
        }

        int refund = 0;
        float meters = 0f;
        float full = editPlot.BuiltWallFullHeight;
        foreach (var span in eraseSpans)
        {
            meters += span.Length;
            refund += settings.GetRefund(span.Length, span.wall.height < full - 0.01f);
        }
        foreach (var item in items) if (!addedItems.Contains(item)) refund += StorePlot.RemovalRefund(item.Def);

        ShowEraseGhosts(eraseSpans);
        string what = $"{meters:0.0} m of wall" +
                      (items.Count > 0 ? $" and {items.Count} thing{(items.Count == 1 ? "" : "s")} on it" : "") + $" (${refund} back)";
        wallInfo = sameSpot
            ? $"Click here again to remove the whole wall: {what}   (Esc: cancel)"
            : $"Click to remove {what}   (Esc: cancel)";

        // Done by a second click, or by letting go after dragging.
        bool released = eraseDragging && !Input.GetMouseButton(0);
        if (released)
        {
            eraseDragging = false;
            bool dragged = (mouse - erasePressMouse).sqrMagnitude > 12f * 12f;
            if (!dragged) return; // it was a click: wait for the second one
        }
        else if (!click || eraseDragging) return;

        erasing = false;
        HideEraseGhosts();
        if (eraseSpans.Count == 0) return;
        RemoveEditItems(items, null);
        if (sameSpot) RequestRemoveWallRpc(eraseLine.id);
        else RequestEraseWallsRpc(eraseStart, end);
        ShowEditMessage($"Removed {what}.");
    }

    /// <summary>
    /// The spot on a built wall under the mouse, slid along the wall to the nearest grid step. Line:
    /// stay on this wall's line (the second spot); otherwise the wall hit by the mouse, or the closest
    /// one to where the mouse meets the floor.
    /// </summary>
    private bool TryEraseSpot(Ray ray, BuiltWall? line, out Vector3 spot, out BuiltWall wall)
    {
        spot = default;
        wall = default;
        var settings = BuiltWallSettings.Instance;
        float floorY = editPlot.FloorHeight;

        Vector3 raw;
        bool found = false;
        var piece = PieceUnderMouse(ray, out RaycastHit hit);
        if (line.HasValue)
        {
            wall = line.Value;
            found = true;
            if (piece != null && piece.WallId == wall.id) raw = hit.point;
            else if (!new Plane(Vector3.up, new Vector3(0f, floorY, 0f)).Raycast(ray, out float e)) return false;
            else raw = ray.GetPoint(e);
            // Past the wall's end onto the next wall in line: aim at that wall's face.
            if (piece != null && piece.WallId != wall.id) raw = hit.point;
        }
        else if (piece != null && editPlot.TryGetBuiltWallById(piece.WallId, out wall))
        {
            raw = hit.point;
            found = true;
        }
        else
        {
            if (!new Plane(Vector3.up, new Vector3(0f, floorY, 0f)).Raycast(ray, out float e)) return false;
            raw = ray.GetPoint(e);
            float best = settings.Thickness * 0.5f + 0.4f;
            for (int i = 0; i < editPlot.BuiltWallCount; i++)
            {
                var w = editPlot.GetBuiltWall(i);
                float d = DistanceToSegmentFlat(raw, w.a, w.b);
                if (d < best) { best = d; wall = w; found = true; }
            }
        }
        if (!found) return false;

        // Slide along the wall's line in grid steps (a diagonal's grid points are a diagonal apart).
        Vector3 dir = wall.b - wall.a;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return false;
        dir.Normalize();
        bool diagonal = Mathf.Abs(dir.x) > 0.1f && Mathf.Abs(dir.z) > 0.1f;
        float step = settings.GridSize * (diagonal ? Mathf.Sqrt(2f) : 1f);
        // Steps count from an end that's on the grid (an end slid onto the store's edge isn't).
        Vector3 basePoint = OnGrid(settings, wall.a) ? wall.a : OnGrid(settings, wall.b) ? wall.b : wall.a;
        float baseT = Vector3.Dot(Flat(basePoint - wall.a), dir);
        float t = Vector3.Dot(Flat(raw - wall.a), dir);
        if (!line.HasValue) t = Mathf.Clamp(t, 0f, wall.Length);
        t = baseT + Mathf.Round((t - baseT) / step) * step;
        // The wall's own ends count as spots too.
        float length = wall.Length;
        float tRaw = Vector3.Dot(Flat(raw - wall.a), dir);
        if (Mathf.Abs(tRaw) < step * 0.5f) t = 0f;
        else if (Mathf.Abs(tRaw - length) < step * 0.5f) t = length;
        spot = wall.a + dir * t;
        spot.y = floorY;
        return true;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    private static bool OnGrid(BuiltWallSettings settings, Vector3 p)
    {
        Vector3 g = settings.SnapToGrid(p);
        return Mathf.Abs(g.x - p.x) < 0.005f && Mathf.Abs(g.z - p.z) < 0.005f;
    }

    private static float DistanceToSegmentFlat(Vector3 p, Vector3 a, Vector3 b)
    {
        p.y = a.y = b.y = 0f;
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector3.Distance(p, a + ab * t);
    }

    private BuiltWallPiece PieceUnderMouse(Ray ray) => PieceUnderMouse(ray, out _);

    private BuiltWallPiece PieceUnderMouse(Ray ray, out RaycastHit hit)
    {
        if (!Physics.Raycast(ray, out hit, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return null;
        var piece = hit.collider.GetComponentInParent<BuiltWallPiece>();
        return piece != null && piece.Plot == editPlot ? piece : null;
    }


    private BuiltWallPiece FindPiece(int wallId)
    {
        foreach (var w in CuttableWall.All)
            if (w != null && w.TryGetComponent<BuiltWallPiece>(out var p) && p.Plot == editPlot && p.WallId == wallId) return p;
        return null;
    }

    /// <summary>Windows, doors and wall items on the parts of walls about to be removed.</summary>
    private List<EditLocalItem> ItemsInSpans(List<StorePlot.WallSpan> spans)
    {
        var result = new List<EditLocalItem>();
        foreach (var span in spans)
        {
            var piece = FindPiece(span.wall.id);
            if (piece == null) continue;
            Vector3 d = span.Dir;
            foreach (var item in ItemsOnWall(piece))
            {
                if (result.Contains(item)) continue;
                Vector3 c;
                float reach;
                if (item.Opening != null)
                {
                    var shape = item.Opening.Current;
                    c = shape.center;
                    reach = shape.size.x * 0.5f;
                }
                else if (PlacementCheck.GetTestBox(item.Def, item.transform.position, item.transform.rotation, out c, out Vector3 half))
                    reach = Mathf.Max(half.x, half.z);
                else continue;

                // Windows and doors need the edge margin of wall beside them too, so one left right at the
                // new end of a wall (barely held) goes as well.
                float margin = item.Opening != null && BuiltWallSettings.Instance != null ? BuiltWallSettings.Instance.OpeningEdgeMargin : 0f;
                float t = Vector3.Dot(c - span.wall.a, d); // d is flat, so height doesn't count
                if (t + reach + margin > span.from && t - reach - margin < span.to) result.Add(item);
            }
        }
        return result;
    }

    private void ShowEraseGhosts(List<StorePlot.WallSpan> spans)
    {
        var settings = BuiltWallSettings.Instance;
        if (settings == null || settings.WallPrefab == null) return;
        if (eraseGhostTemplate == null) eraseGhostTemplate = GhostBuilder.BuildTemplate(settings.WallPrefab, WallGhostInvalid.a);

        while (eraseGhosts.Count < spans.Count)
        {
            var g = Instantiate(eraseGhostTemplate);
            g.SetActive(true);
            foreach (var c in g.GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (var r in g.GetComponentsInChildren<Renderer>())
                foreach (var m in r.materials)
                {
                    GhostBuilder.SetColor(m, WallGhostInvalid);
                    eraseGhostMats.Add(m);
                }
            eraseGhosts.Add(g);
        }

        for (int i = 0; i < eraseGhosts.Count; i++)
        {
            var g = eraseGhosts[i];
            bool on = i < spans.Count;
            g.SetActive(on);
            if (!on) continue;
            var span = spans[i];
            // A touch bigger than the wall so it shows over it.
            settings.GetWallPose(span.From, span.To, span.wall.height + 0.02f, 0.01f, 0.01f,
                                 out Vector3 pos, out Quaternion rot, out Vector3 scale);
            g.transform.SetPositionAndRotation(pos - Vector3.up * 0.01f, rot);
            g.transform.localScale = new Vector3(scale.x + 0.04f, scale.y, scale.z);
        }
    }

    private void HideEraseGhosts()
    {
        foreach (var g in eraseGhosts) if (g != null) g.SetActive(false);
    }

    private void DestroyEraseGhosts()
    {
        foreach (var m in eraseGhostMats) if (m != null) Destroy(m);
        eraseGhostMats.Clear();
        foreach (var g in eraseGhosts) if (g != null) Destroy(g);
        eraseGhosts.Clear();
    }

    [Rpc(SendTo.Server)]
    private void RequestEraseWallsRpc(Vector3 a, Vector3 b, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        var settings = BuiltWallSettings.Instance;
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotEditedBy(OwnerClientId) : null;
        if (settings == null || plot == null) return;

        // The client already put both spots on a wall's line in grid steps; only what's there is removed.
        a.y = b.y = plot.FloorHeight;

        plot.ServerEraseWalls(a, b, out int refund);
        var econ = EconomyManager.Instance;
        if (econ != null && refund > 0) econ.ServerAddMoney(MallManager.GetTeamOf(OwnerClientId), refund);
    }

    private bool CanAffordWall(int cost)
    {
        var econ = EconomyManager.Instance;
        return econ == null || cost <= 0 || econ.GetMoneyOf(OwnerClientId) >= cost;
    }

    // The store's items are local copies while editing, so only this machine can check them.
    /// <summary>
    /// Would a wall from a to b go through an item? Windows and doors already in a wall it joins
    /// (Joins: wall ids) don't count; they stay in the joined wall.
    /// </summary>
    private bool WallHitsEditItems(BuiltWallSettings settings, Vector3 a, Vector3 b, float height, List<int> joins = null)
    {
        settings.GetWallPose(a, b, height, out Vector3 pos, out Quaternion rot, out Vector3 scale);
        Vector3 half = Vector3.Max(scale * 0.5f - Vector3.one * 0.02f, Vector3.one * 0.001f);
        int n = Physics.OverlapBoxNonAlloc(pos, half, WallHits, rot, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var item = WallHits[i].GetComponentInParent<EditLocalItem>();
            if (item == null) continue;
            if (joins != null && item.Opening != null)
            {
                var host = CuttableWall.HostOf(item.Opening);
                var piece = host != null ? host.GetComponent<BuiltWallPiece>() : null;
                if (piece != null && joins.Contains(piece.WallId)) continue;
            }
            return true;
        }
        return false;
    }

    /* ---------- Keeping windows and doors valid ---------- */

    private int checkedWallVersion = -1;
    private int seenWallVersion = -1;
    private float wallsChangedAt;

    /// <summary>
    /// When walls are rebuilt (built, joined, cut or removed), every window and door being edited must
    /// still sit in one of this store's walls with room around it. Any that don't are taken out and
    /// refunded rather than left floating.
    /// </summary>
    private void CheckOpeningsAfterWallChange()
    {
        // Wait until the walls have settled (one change can arrive as several: old walls out, new in).
        if (seenWallVersion != CuttableWall.Version)
        {
            seenWallVersion = CuttableWall.Version;
            wallsChangedAt = Time.time;
        }
        if (checkedWallVersion == CuttableWall.Version || Time.time - wallsChangedAt < 0.3f) return;
        checkedWallVersion = CuttableWall.Version;

        var bad = new List<EditLocalItem>();
        void Check(EditLocalItem item)
        {
            if (item == null || item.Opening == null || !item.gameObject.activeInHierarchy) return;
            var o = item.Opening;
            var host = CuttableWall.HostOf(o);
            if (host == null || host.Plot != editPlot) { bad.Add(item); return; }
            var shape = o.Current;
            Vector3 c = host.ToWall(shape.center);
            var hole = new Rect(c.x - shape.size.x * 0.5f, c.y - shape.size.y * 0.5f, shape.size.x, shape.size.y);
            if (!host.CanFit(hole, o, false, out _)) bad.Add(item);
        }
        foreach (var item in localItems) Check(item);
        foreach (var item in addedItems) Check(item);
        if (bad.Count == 0) return;

        RemoveEditItems(bad, null);
        ShowEditMessage($"{bad.Count} window{(bad.Count == 1 ? "" : "s")}/door{(bad.Count == 1 ? "" : "s")} had no room in the changed walls: removed and refunded.");
    }

    private void ShowWallGhost(Vector3 a, Vector3 b, float height, bool valid)
    {
        var settings = BuiltWallSettings.Instance;
        if (wallGhost == null)
        {
            if (wallGhostTemplate == null) wallGhostTemplate = GhostBuilder.BuildTemplate(settings.WallPrefab, WallGhostValid.a);
            wallGhost = Instantiate(wallGhostTemplate);
            wallGhost.SetActive(true);
            foreach (var c in wallGhost.GetComponentsInChildren<Collider>()) c.enabled = false;
            wallGhostMats.Clear();
            foreach (var r in wallGhost.GetComponentsInChildren<Renderer>()) wallGhostMats.AddRange(r.materials);
            wallGhostShownValid = null;
        }

        settings.GetWallPose(a, b, height, out Vector3 pos, out Quaternion rot, out Vector3 scale);
        wallGhost.transform.SetPositionAndRotation(pos, rot);
        wallGhost.transform.localScale = scale;

        if (wallGhostShownValid == valid) return;
        wallGhostShownValid = valid;
        foreach (var m in wallGhostMats) if (m != null) GhostBuilder.SetColor(m, valid ? WallGhostValid : WallGhostInvalid);
    }

    private void DestroyWallGhost()
    {
        foreach (var m in wallGhostMats) if (m != null) Destroy(m);
        wallGhostMats.Clear();
        if (wallGhost != null) Destroy(wallGhost);
        wallGhost = null;
    }

    [Rpc(SendTo.Server)]
    private void RequestBuildWallRpc(Vector3 a, Vector3 b, bool half, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        var settings = BuiltWallSettings.Instance;
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotEditedBy(OwnerClientId) : null;
        if (settings == null || plot == null) return;

        // Same snapping the client used, so the wall can't be bent off the grid.
        a = settings.SnapToGrid(a);
        a.y = plot.FloorHeight;
        b = settings.SnapEnd(a, b);
        b.y = plot.FloorHeight;
        plot.FitWallToEdges(ref a, ref b);
        if (!plot.CanBuildWall(a, b, out _)) return;

        float height = half ? Mathf.Min(settings.HalfHeight, plot.BuiltWallFullHeight) : plot.BuiltWallFullHeight;
        if (!plot.PlanWall(a, b, height, out var plan, out _)) return;
        int cost = settings.GetCost(plan.addedLength, half);
        var econ = EconomyManager.Instance;
        if (econ != null && !econ.ServerTrySpend(MallManager.GetTeamOf(OwnerClientId), cost)) return;

        plot.ServerBuildPlannedWall(plan, height);
    }

    [Rpc(SendTo.Server)]
    private void RequestRemoveWallRpc(int wallId, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        var settings = BuiltWallSettings.Instance;
        var plot = MallManager.Instance != null ? MallManager.Instance.GetPlotEditedBy(OwnerClientId) : null;
        if (settings == null || plot == null || !plot.ServerRemoveWall(wallId, out var wall)) return;

        bool half = wall.height < plot.BuiltWallFullHeight - 0.01f;
        var econ = EconomyManager.Instance;
        if (econ != null) econ.ServerAddMoney(MallManager.GetTeamOf(OwnerClientId), settings.GetRefund(wall.Length, half));
    }

    /* ========================= HUD ========================= */

    private string refusalMessage;
    private float refusalUntil;

    /// <summary>The host said no to edit mode; show why for a few seconds.</summary>
    [Rpc(SendTo.Owner)]
    private void EditRefusedRpc(string reason)
    {
        finishing = false; // e.g. a save the host turned down: keep editing
        ShowEditMessage(reason);
    }

    private void ShowEditMessage(string message)
    {
        refusalMessage = message;
        refusalUntil = Time.time + 4f;
        if (StoreEditHud.Instance != null) StoreEditHud.Instance.ShowMessage(message);
    }

    /* ========================= HUD ========================= */

    private string hudTitle, hudStatus, hudControls, hudInfo;

    // Keys in bold (TMP rich text); the built-in HUD strips the tags.
    private static string K(KeyCode key) => $"<b>{KeyName(key)}</b>";
    private static string K(string key) => $"<b>{key}</b>";

    private static string KeyName(KeyCode key) => key switch
    {
        KeyCode.Alpha0 => "0", KeyCode.Alpha1 => "1", KeyCode.Alpha2 => "2", KeyCode.Alpha3 => "3",
        KeyCode.Alpha4 => "4", KeyCode.Alpha5 => "5", KeyCode.Alpha6 => "6", KeyCode.Alpha7 => "7",
        KeyCode.Alpha8 => "8", KeyCode.Alpha9 => "9", KeyCode.Escape => "Esc",
        _ => key.ToString(),
    };

    /// <summary>What the HUD says right now: the mode, its state, the keys for it, and live feedback.</summary>
    private const string ControlsHiddenPref = "MallSim.EditControlsHidden";

    /// <summary>Is the key list hidden (only a small "show controls" hint)? Remembered on this computer.</summary>
    public bool ControlsHidden
    {
        get => PlayerPrefs.GetInt(ControlsHiddenPref, 0) == 1;
        set => PlayerPrefs.SetInt(ControlsHiddenPref, value ? 1 : 0);
    }

    private void BuildHud()
    {
        BuildHudTexts();
        string key = K(controlsKey);
        bool keyTaken = wallTool && controlsKey == wallHeightKey; // in the wall tool that key switches the height
        if (ControlsHidden)
            hudControls = keyTaken
                ? $"<size=60%>Controls hidden ({key} outside the wall tool to show them)</size>"
                : $"<size=60%>{key}: show controls</size>";
        else if (!keyTaken)
            hudControls += $"\n<size=70%>{key}: hide controls</size>";
    }

    private void BuildHudTexts()
    {
        if (wallTool)
        {
            hudTitle = eraseMode ? "WALL TOOL - REMOVING" : "WALL TOOL";
            hudStatus = $"Height: {(wallHalf ? "half" : "full")}";
            hudControls = eraseMode
                ? $"{K("Click")} a spot on a wall, then a second: remove what's between\n" +
                  $"Same spot twice: the whole wall   {K("Drag")}: works too\n" +
                  $"{K(wallEraseKey)} / {K("Esc")}: back to building   {K(wallToolKey)}: back to items"
                : $"{K("Click")}: start, {K("click")} again: build (keeps going from the end)\n" +
                  $"{K("RMB")} / {K("Esc")}: stop the chain (again: leave the tool)\n" +
                  $"{K(wallHeightKey)}: full / half height   {K(wallEraseKey)} (or hold {K("Shift")}): remove walls\n" +
                  $"{K(wallToolKey)}: back to items";
            hudInfo = wallInfo;
            return;
        }

        hudTitle = finishing ? "STORE EDIT MODE - saving..." : "STORE EDIT MODE";

        if (placing != null)
        {
            hudStatus = $"Placing: {placing.Def.displayName}";
            hudControls = $"{K("Click")} a wall to place one (keeps placing)   {K("Ctrl")}: no snap\n" +
                          $"{K("RMB")} / {K("Esc")}: stop placing (back to the list)";
            hudInfo = openingInfo;
            return;
        }

        string itemName = selected == null ? "nothing (click an item)" : selected.Def.displayName;
        if (selected != null && selected.Opening != null)
        {
            hudStatus = $"Selected: {itemName}";
            hudControls = $"{K("Drag")} along a wall to move   {K("Ctrl")}: no snap\n" +
                          $"{K(rotateKey)}: turn around   {K(removeKey)}: remove (part refund)   {K("Esc")}: deselect";
            hudInfo = openingInfo;
            return;
        }

        string toolName = selected == null ? "-" : tool == Tool.None ? "none" : tool.ToString();
        string gravity = selected == null ? "-" : selected.Gravity ? "on" : "off";
        hudStatus = $"Selected: {itemName}   Tool: {toolName}   Gravity: {gravity}";
        hudControls = $"{K("Drag")} item: slide   {K("X")}: move > rotate > hide   {K(rotateKey)}: rotate   {K("G")}: gravity\n" +
                      $"{K("Ctrl")}: no snap   {K("RMB")} drag: camera   {K("RMB")} click / {K("Esc")}: deselect\n" +
                      $"{K(openingsKey)}: windows & doors   {K(wallToolKey)}: build walls   {K(toggleKey)}: save & exit";
        hudInfo = openingInfo;
    }

    private void LateUpdate()
    {
        if (!IsOwner) return;
        var hud = StoreEditHud.Instance;
        if (hud == null) return;
        bool show = IsEditing;
        hud.SetVisible(show);
        if (!show) return;
        BuildHud();
        hud.SetTexts(hudTitle, hudStatus, hudControls, hudInfo);
    }

    private static string StripTags(string s) =>
        string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");

    /// <summary>Built-in HUD, only when there's no StoreEditHud in the scene (and Show Hud is on).</summary>
    private void OnGUI()
    {
        bool canvasHud = StoreEditHud.Instance != null;
        if (!canvasHud && IsOwner && Time.time < refusalUntil && !string.IsNullOrEmpty(refusalMessage))
        {
            var r = new Rect(Screen.width * 0.5f - 260f, Screen.height * 0.35f, 520f, 30f);
            GUI.Box(r, refusalMessage);
        }

        if (!IsOwner || !IsEditing) return;

        if (!canvasHud && showHud)
        {
            BuildHud();
            var lines = new List<string> { hudTitle, hudStatus };
            lines.AddRange(StripTags(hudControls).Split('\n'));
            if (!string.IsNullOrEmpty(hudInfo)) lines.Add(hudInfo);
            GUI.Box(new Rect(10, 10, 470, 16 + lines.Count * 20), GUIContent.none);
            for (int i = 0; i < lines.Count; i++)
                GUI.Label(new Rect(20, 16 + i * 20, 450, 20), lines[i]);
        }

        // The pick lists are still built-in (the windows & doors list only without a WallPiecesMenu).
        if (!finishing && !wallTool)
        {
            DrawSlotPanel();
            DrawOpeningsPanel();
        }
    }

    /* ========================= HELPERS ========================= */

    private int WallMask() => interact != null ? interact.EffectiveWallMask : PlacementCheck.DefaultWallMask;

    /// <summary>Layers that stop an item: the placement blocking layers plus the floor.</summary>
    private int EditMask()
    {
        int mask = interact != null ? interact.PlacementBlockingMask.value : Physics.DefaultRaycastLayers;
        int ground = LayerMask.NameToLayer("Ground");
        if (ground >= 0) mask |= 1 << ground;
        return mask;
    }

    private bool ToScreen(Vector3 world, out Vector2 screen)
    {
        Vector3 s = editCam.WorldToScreenPoint(world);
        screen = s;
        return s.z > 0f;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// Where along the line (origin + axis * t) the mouse ray passes closest.
    /// NaN when the ray runs nearly parallel to the axis.
    /// </summary>
    private static float AxisParam(Ray ray, Vector3 origin, Vector3 axis)
    {
        Vector3 w0 = origin - ray.origin;
        float b = Vector3.Dot(axis, ray.direction);
        float d = Vector3.Dot(axis, w0);
        float e = Vector3.Dot(ray.direction, w0);
        float denom = 1f - b * b;
        if (denom < 1e-4f) return float.NaN;
        return (b * e - d) / denom;
    }

    /* ========================= NETWORK ========================= */

    /// <summary>Host: swap one storefront section of the store this player is editing.</summary>
    [Rpc(SendTo.Server)]
    private void RequestSetFrontModuleRpc(int slot, int moduleId, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId || MallManager.Instance == null) return;

        var plot = MallManager.Instance.GetPlotEditedBy(OwnerClientId);
        if (plot == null) return;

        // Charge the module's price only if the swap actually happens.
        var eco = EconomyManager.Instance;
        ulong team = MallManager.GetTeamOf(OwnerClientId);
        int price = FrontWallCatalog.TryGet(moduleId, out var module) ? module.Price : 0;
        if (eco != null && eco.IsSpawned && !eco.ServerCanAfford(team, price)) return;

        if (plot.ServerTrySetFrontModule(slot, moduleId) && eco != null && eco.IsSpawned)
            eco.ServerTrySpend(team, price);
    }

    [Rpc(SendTo.Server)]
    private void RequestStartEditRpc(RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;

        var mall = MallManager.Instance;
        if (mall == null) return;
        if (!mall.ServerTryStartEdit(OwnerClientId, transform.position, out string reason))
        {
            if (!string.IsNullOrEmpty(reason)) EditRefusedRpc(reason);
            return;
        }

        var plot = mall.GetPlotEditedBy(OwnerClientId);
        if (plot == null) return;

        // Hand the editor the list of items that were taken out, a chunk at a time.
        var session = plot.ServerEditSession;
        int sessionId = plot.ServerEditSessionId;
        if (session.Count == 0)
        {
            ReceiveEditItemsRpc(sessionId, 0, new int[0], new Vector3[0], new Quaternion[0], new uint[0]);
            return;
        }

        for (int start = 0; start < session.Count; start += ChunkSize)
        {
            int n = Mathf.Min(ChunkSize, session.Count - start);
            var ids = new int[n];
            var positions = new Vector3[n];
            var rotations = new Quaternion[n];
            var paints = new uint[n];
            for (int k = 0; k < n; k++)
            {
                var entry = session[start + k];
                ids[k] = entry.itemId;
                positions[k] = entry.position;
                rotations[k] = entry.rotation;
                paints[k] = entry.paint;
            }
            ReceiveEditItemsRpc(sessionId, start, ids, positions, rotations, paints);
        }

        // Then what's on each shelf, so its stock shows (and moves with it) while editing.
        for (int i = 0; i < session.Count; i++)
        {
            var stock = session[i].stock;
            if (stock == null) continue;
            var stockIds = new int[stock.Length];
            var counts = new int[stock.Length];
            var orients = new int[stock.Length];
            var seeds = new int[stock.Length];
            bool any = false;
            for (int z = 0; z < stock.Length; z++)
            {
                stockIds[z] = stock[z].itemId;
                counts[z] = stock[z].count;
                orients[z] = stock[z].orient;
                seeds[z] = stock[z].seed;
                any |= stock[z].count > 0;
            }
            if (any) ReceiveEditStockRpc(sessionId, i, stockIds, counts, orients, seeds);
        }
    }

    /// <summary>Editor: show the stock on one shelf's local copy (sent after its item).</summary>
    [Rpc(SendTo.Owner)]
    private void ReceiveEditStockRpc(int sessionId, int index, int[] itemIds, int[] counts, int[] orients, int[] seeds)
    {
        if (sessionId != clientSessionId || index < 0 || index >= localItems.Count || localItems[index] == null) return;
        localItems[index].ShowStock(itemIds, counts, orients, seeds);
    }

    /// <summary>Editor: build local copies of the items the host took out of the store.</summary>
    [Rpc(SendTo.Owner)]
    private void ReceiveEditItemsRpc(int sessionId, int start, int[] itemIds, Vector3[] positions, Quaternion[] rotations,
                                     uint[] paints)
    {
        if (sessionId != clientSessionId)
        {
            ClearLocalItems();
            clientSessionId = sessionId;
        }

        var catalog = ItemCatalog.Instance;
        for (int k = 0; k < itemIds.Length; k++)
        {
            int index = start + k;
            while (localItems.Count <= index) localItems.Add(null);
            if (localItems[index] != null) continue; // duplicate message

            if (catalog == null || !catalog.TryGet(itemIds[k], out var def) || def.worldNetworkPrefab == null)
            {
                Debug.LogWarning($"[StoreEdit] No catalog entry for item id {itemIds[k]}; it will stay where it was.");
                continue;
            }
            localItems[index] = EditLocalItem.Create(def, index, positions[k], rotations[k]);
            if (paints != null && k < paints.Length && paints[k] != 0) localItems[index].SetPaint(paints[k]); // shown as it is
        }
    }

    /// <summary>Host: collect a chunk of the editor's final positions.</summary>
    [Rpc(SendTo.Server)]
    private void SubmitEditPosesRpc(int sessionId, int start, Vector3[] positions, Quaternion[] rotations,
                                    RpcParams rpcParams = default)
    {
        if (!ServerTryGetSession(rpcParams, sessionId, out _)) return;

        int n = Mathf.Min(positions.Length, rotations.Length);
        for (int k = 0; k < n; k++)
        {
            int index = start + k;
            if (index < 0 || index >= pendingPositions.Length) continue;
            pendingPositions[index] = positions[k];
            pendingRotations[index] = rotations[k];
        }
    }

    /// <summary>Host: the windows and doors the editor took out and bought (sent just before finishing).</summary>
    [Rpc(SendTo.Server)]
    private void SubmitEditChangesRpc(int sessionId, int[] newIds, Vector3[] newPositions,
                                      Quaternion[] newRotations, uint[] newPaints, RpcParams rpcParams = default)
    {
        if (!ServerTryGetSession(rpcParams, sessionId, out _)) return;

        pendingAdded.Clear();
        if (newIds == null || newPositions == null || newRotations == null) return;
        int n = Mathf.Min(MaxNewPerSave, Mathf.Min(newIds.Length, Mathf.Min(newPositions.Length, newRotations.Length)));
        for (int k = 0; k < n; k++)
            pendingAdded.Add(new StorePlot.NewEditItem
            {
                itemId = newIds[k], position = newPositions[k], rotation = newRotations[k],
                paint = newPaints != null && k < newPaints.Length ? newPaints[k] : 0u,
            });
    }

    private const int MaxNewPerSave = 64;

    /// <summary>Host: sell back windows, doors and wall items the editor took out (by their place in the session).</summary>
    [Rpc(SendTo.Server)]
    private void RequestRemoveEditItemsRpc(int sessionId, int[] indices, RpcParams rpcParams = default)
    {
        if (indices == null || !ServerTryGetSession(rpcParams, sessionId, out var plot)) return;
        ulong team = MallManager.GetTeamOf(OwnerClientId);
        foreach (int i in indices) plot.ServerRemoveEditItem(i, team, out _);
    }

    /// <summary>Host: after this save, will the store still have a door in its front wall?</summary>
    private bool ServerStorefrontHasDoor(StorePlot plot)
    {
        var catalog = ItemCatalog.Instance;
        if (catalog == null) return true;

        var session = plot.ServerEditSession;
        for (int i = 0; i < session.Count; i++)
        {
            if (session[i].removed || !catalog.TryGet(session[i].itemId, out var def)) continue;
            Vector3 pos = session[i].position;
            Quaternion rot = session[i].rotation;
            if (pendingPositions != null && i < pendingPositions.Length && float.IsFinite(pendingPositions[i].x))
            {
                pos = pendingPositions[i];
                rot = pendingRotations[i];
            }
            if (plot.IsStorefrontDoor(def, pos, rot)) return true;
        }
        foreach (var n in pendingAdded)
            if (catalog.TryGet(n.itemId, out var def) && plot.IsStorefrontDoor(def, n.position, n.rotation)) return true;
        foreach (var o in WallOpening.All)
        {
            if (o == null || o.IsEditCopy || !o.IsDoor) continue;
            var host = CuttableWall.HostOf(o);
            if (host != null && host.Plot == plot && host.IsStorefront) return true;
        }
        return false;
    }

    /// <summary>Host: respawn every item, at the editor's position where it's valid.</summary>
    [Rpc(SendTo.Server)]
    private void FinishEditRpc(int sessionId, RpcParams rpcParams = default)
    {
        if (!ServerTryGetSession(rpcParams, sessionId, out var plot)) return;

        if (plot.HasStorefrontWalls && !ServerStorefrontHasDoor(plot))
        {
            EditRefusedRpc("Your storefront needs at least one door.");
            return;
        }

        int mask = EditMask();
        plot.ServerEndEdit(pendingPositions, pendingRotations, (def, pos, rot) =>
        {
            // Windows and doors sit inside a wall, so walls they can be cut into don't count.
            var blocker = WallOpening.IsOpening(def)
                ? PlacementCheck.FindOpeningBlocker(def, pos, rot, mask, null, true, plot)
                : PlacementCheck.FindBlockerIgnoringEditCopies(def, pos, rot, mask);
            if (blocker != null && logEditSave)
                Debug.LogWarning($"[StoreEdit] '{def.displayName}' can't stay where it was moved: it overlaps " +
                                 $"'{blocker.name}' (root '{blocker.transform.root.name}', layer " +
                                 $"'{LayerMask.LayerToName(blocker.gameObject.layer)}'). It goes back to where it was.", blocker);
            return blocker == null;
        }, logEditSave, pendingAdded, MallManager.GetTeamOf(OwnerClientId));

        pendingPositions = null;
        pendingRotations = null;
        pendingSessionId = -1;
        pendingAdded.Clear();
    }

    /// <summary>
    /// Host: is this message from the player who owns this controller, for the session they're
    /// editing right now? Sets up the position buffer (pre-filled with "keep where it was").
    /// </summary>
    private bool ServerTryGetSession(RpcParams rpcParams, int sessionId, out StorePlot plot)
    {
        plot = null;
        if (rpcParams.Receive.SenderClientId != OwnerClientId || MallManager.Instance == null) return false;

        plot = MallManager.Instance.GetPlotEditedBy(OwnerClientId);
        if (plot == null || plot.ServerEditSessionId != sessionId) return false;

        if (pendingSessionId != sessionId || pendingPositions == null)
        {
            int count = plot.ServerEditSession.Count;
            pendingPositions = new Vector3[count];
            pendingRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                pendingPositions[i] = Vector3.positiveInfinity;
                pendingRotations[i] = Quaternion.identity;
            }
            pendingSessionId = sessionId;
            pendingAdded.Clear();
        }
        return true;
    }
}
