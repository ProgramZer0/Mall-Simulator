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
/// Drag       an arrow to slide along that axis (stops on contact), a ring to rotate
///            (hold Ctrl to snap to 15 degrees)
/// G          toggle gravity for the selected item (everything hangs in place otherwise)
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
    [SerializeField] private float snapAngle = 15f;
    [Tooltip("Line thickness per meter of camera distance.")]
    [SerializeField] private float lineWidth = 0.006f;

    [Header("HUD")]
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

        if (Input.GetKeyDown(toggleKey) || (toggleKey == KeyCode.Alpha0 && Input.GetKeyDown(KeyCode.Keypad0)))
        {
            if (IsEditing) Finish();
            else TryStart();
        }

        SyncEditState();
        if (!IsEditing) return;

        UpdateCamera();
        if (!finishing) UpdateTools();
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

        if (selected != null)
        {
            if (Input.GetKeyDown(toolKey))
                tool = tool == Tool.None ? Tool.Move : tool == Tool.Move ? Tool.Rotate : Tool.None; // None -> Move -> Rotate -> None

            if (Input.GetKeyDown(gravityKey))
                selected.SetGravity(!selected.Gravity);

            if (Input.GetKeyDown(KeyCode.Escape) || rmbClicked)
                Deselect();
        }

        hoverHandle = PickHandle(mouse, grabRadiusPixels);

        if (selectedSlot >= 0 && (Input.GetKeyDown(KeyCode.Escape) || rmbClicked))
            selectedSlot = -1;

        if (Input.GetMouseButtonDown(0) && !IsMouseOverSlotPanel(mouse))
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
                if (editPlot.WouldRemoveLastEntrance(selectedSlot, m.ModuleId))
                    slotMessage = "Your store needs at least one doorway.";
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
            selected.SetDragging(false);
            return;
        }
        if (!Input.GetMouseButton(0))
        {
            freeDragging = false;
            selected.SetDragging(false);
            return;
        }

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
            bool snap = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
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

    /* ========================= HUD ========================= */

    private void OnGUI()
    {
        if (!showHud || !IsOwner || !IsEditing) return;

        string toolName = selected == null ? "-" : tool == Tool.None ? "none (X for arrows)" : tool.ToString();
        string gravity = selected == null ? "-" : selected.Gravity ? "ON" : "off";
        string itemName = selected == null ? "nothing (click an item)" : selected.Def.displayName;

        GUI.Box(new Rect(10, 10, 420, 136), GUIContent.none);
        GUI.Label(new Rect(20, 16, 400, 20), finishing ? "STORE EDIT MODE - saving..." : "STORE EDIT MODE   (0 = save & exit)");
        GUI.Label(new Rect(20, 36, 400, 20), $"Selected: {itemName}");
        GUI.Label(new Rect(20, 56, 400, 20), $"Tool: {toolName}     Gravity: {gravity}");
        GUI.Label(new Rect(20, 76, 400, 20), "X: move > rotate > hide   G: gravity   Ctrl: snap   Esc: deselect");
        GUI.Label(new Rect(20, 96, 400, 20), "Drag item: slide   RMB drag: camera   RMB click: deselect");
        GUI.Label(new Rect(20, 116, 400, 20), "Click a storefront wall section to change it");

        if (!finishing) DrawSlotPanel();
    }

    /* ========================= HELPERS ========================= */

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
        if (mall == null || !mall.ServerTryStartEdit(OwnerClientId, transform.position)) return;

        var plot = mall.GetPlotEditedBy(OwnerClientId);
        if (plot == null) return;

        // Hand the editor the list of items that were taken out, a chunk at a time.
        var session = plot.ServerEditSession;
        int sessionId = plot.ServerEditSessionId;
        if (session.Count == 0)
        {
            ReceiveEditItemsRpc(sessionId, 0, new int[0], new Vector3[0], new Quaternion[0]);
            return;
        }

        for (int start = 0; start < session.Count; start += ChunkSize)
        {
            int n = Mathf.Min(ChunkSize, session.Count - start);
            var ids = new int[n];
            var positions = new Vector3[n];
            var rotations = new Quaternion[n];
            for (int k = 0; k < n; k++)
            {
                var entry = session[start + k];
                ids[k] = entry.itemId;
                positions[k] = entry.position;
                rotations[k] = entry.rotation;
            }
            ReceiveEditItemsRpc(sessionId, start, ids, positions, rotations);
        }
    }

    /// <summary>Editor: build local copies of the items the host took out of the store.</summary>
    [Rpc(SendTo.Owner)]
    private void ReceiveEditItemsRpc(int sessionId, int start, int[] itemIds, Vector3[] positions, Quaternion[] rotations)
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

    /// <summary>Host: respawn every item, at the editor's position where it's valid.</summary>
    [Rpc(SendTo.Server)]
    private void FinishEditRpc(int sessionId, RpcParams rpcParams = default)
    {
        if (!ServerTryGetSession(rpcParams, sessionId, out var plot)) return;

        int mask = EditMask();
        plot.ServerEndEdit(pendingPositions, pendingRotations,
            (def, pos, rot) => PlacementCheck.IsClear(def, pos, rot, mask, null));

        pendingPositions = null;
        pendingRotations = null;
        pendingSessionId = -1;
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
        }
        return true;
    }
}
