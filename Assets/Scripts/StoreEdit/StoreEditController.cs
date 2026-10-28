using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Store edit mode (GDD 11b). Goes on the player prefab next to PlayerController.
///
/// 0          enter / leave edit mode (while standing in your own store)
/// WASD / QE  pan / turn the top-down camera, mouse wheel zooms
/// Click      select a placed item in your store
/// X          cycle Move (arrows) and Rotate (rings) for the selected item
/// Drag       an arrow to slide along that axis (stops on contact), a ring to rotate
///            (hold Ctrl to snap to 15 degrees)
/// G          toggle gravity for the selected item (everything is frozen in the air otherwise)
/// Esc / RMB  deselect
///
/// The host owns every decision: entering edit mode, each move/rotate (re-checked for
/// collisions and store bounds) and gravity. The editor only sees a preview while dragging.
/// </summary>
public class StoreEditController : NetworkBehaviour
{
    private enum Tool { Select, Move, Rotate }

    [Header("Keys")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Alpha0;
    [SerializeField] private KeyCode toolKey = KeyCode.X;
    [SerializeField] private KeyCode gravityKey = KeyCode.G;

    [Header("Camera")]
    [SerializeField] private float pitch = 55f;
    [SerializeField] private float startDistance = 12f;
    [SerializeField] private float minDistance = 4f;
    [SerializeField] private float maxDistance = 30f;
    [SerializeField] private float panSpeed = 10f;
    [SerializeField] private float turnSpeed = 90f;
    [SerializeField] private float zoomStep = 1.5f;
    [Tooltip("How far past the store's edges the camera may pan.")]
    [SerializeField] private float panMargin = 2f;
    [Tooltip("Layers the edit camera doesn't draw, e.g. ceilings that would block the top-down view.")]
    [SerializeField] private LayerMask hiddenInEditView;

    [Header("Handles")]
    [Tooltip("How close (pixels) the mouse must be to an arrow or ring to grab it.")]
    [SerializeField] private float grabRadiusPixels = 14f;
    [SerializeField] private float rotateDegreesPerPixel = 0.5f;
    [SerializeField] private float snapAngle = 15f;
    [Tooltip("Line thickness per meter of camera distance.")]
    [SerializeField] private float lineWidth = 0.006f;
    [Range(0.1f, 1f)]
    [SerializeField] private float ghostAlpha = 0.85f;
    [Tooltip("After a move is sent, how long to show the preview before following the host's real pose.")]
    [SerializeField] private float resyncDelay = 0.5f;

    private static readonly Color BoxColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color BoxGravityColor = new(0.3f, 0.9f, 1f, 0.9f);

    private PlayerInteractInput interact;
    private Camera playerCam;
    private Camera editCam;
    private StoreEditHandles handles;
    private StorePlot editPlot;

    // Camera
    private Vector3 pivot;
    private float yaw;
    private float distance;

    // Selection
    private ItemPickupNet selected;
    private ItemDef selectedDef;
    private GameObject ghost;
    private readonly List<Material> ghostMaterials = new();
    private readonly List<Renderer> hiddenRenderers = new();
    private Vector3 desiredPos;
    private Quaternion desiredRot;
    private Tool tool;
    private int hoverHandle = StoreEditHandles.None;
    private float lastCommitTime = -10f;

    // Dragging
    private int dragHandle = StoreEditHandles.None;
    private Vector3 dragStartPos;
    private Quaternion dragStartRot;
    private Vector3 dragPivot;
    private float dragStartT;
    private float rotateRaw;
    private Vector2 dragTangent;
    private Vector2 lastMouse;

    private readonly Vector3[] ringBuffer = new Vector3[StoreEditHandles.RingSegments];

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
    }

    public override void OnDestroy()
    {
        Deselect();
        if (editCam != null) Destroy(editCam.gameObject);
        if (handles != null) Destroy(handles.gameObject);
        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsOwner || !IsSpawned) return;

        if (Input.GetKeyDown(toggleKey) || (toggleKey == KeyCode.Alpha0 && Input.GetKeyDown(KeyCode.Keypad0)))
            RequestToggle();

        SyncEditState();
        if (!IsEditing) return;

        UpdateCamera();
        UpdateTools();
        DrawHandles();
    }

    /* ========================= ENTER / EXIT ========================= */

    private void RequestToggle()
    {
        if (IsEditing)
        {
            RequestStopEditServerRpc();
            return;
        }

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

        RequestStartEditServerRpc();
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
        if (editCam == null) CreateEditCamera();
        if (handles == null) handles = StoreEditHandles.Create();

        Bounds b = plot.GetWorldBounds();
        pivot = new Vector3(b.center.x, b.min.y, b.center.z);
        yaw = transform.eulerAngles.y;
        distance = Mathf.Clamp(startDistance, minDistance, maxDistance);
        ApplyCamera();

        if (playerCam != null) playerCam.enabled = false;
        editCam.enabled = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void ExitView()
    {
        Deselect();
        if (handles != null) handles.HideAll();
        if (editCam != null) editCam.enabled = false;
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

        Bounds b = editPlot.GetWorldBounds();
        pivot.x = Mathf.Clamp(pivot.x, b.min.x - panMargin, b.max.x + panMargin);
        pivot.z = Mathf.Clamp(pivot.z, b.min.z - panMargin, b.max.z + panMargin);

        ApplyCamera();
    }

    private void ApplyCamera()
    {
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        editCam.transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
    }

    /* ========================= TOOLS ========================= */

    private void UpdateTools()
    {
        Vector2 mouse = Input.mousePosition;
        Ray ray = editCam.ScreenPointToRay(mouse);

        // Selected item vanished (despawned, store released...): drop the selection.
        if (selectedDef != null && selected == null) Deselect();

        if (dragHandle != StoreEditHandles.None)
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) CancelDrag();
            else if (Input.GetMouseButton(0)) ContinueDrag(ray, mouse);
            else EndDrag();

            lastMouse = mouse;
            return;
        }

        // Once a sent move has had time to land, follow the host's real pose. This also
        // snaps the preview back if the host rejected a move, and tracks falling items.
        if (selected != null && Time.time - lastCommitTime > resyncDelay)
        {
            desiredPos = selected.transform.position;
            desiredRot = selected.transform.rotation;
        }

        if (selected != null)
        {
            if (Input.GetKeyDown(toolKey))
                tool = tool == Tool.Move ? Tool.Rotate : Tool.Move;

            if (Input.GetKeyDown(gravityKey))
                RequestSetGravityServerRpc(selected.NetworkObject, !selected.GravityInEdit);

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                Deselect();
        }

        hoverHandle = selected != null ? PickHandle(mouse) : StoreEditHandles.None;

        if (Input.GetMouseButtonDown(0))
        {
            if (hoverHandle != StoreEditHandles.None) BeginDrag(hoverHandle, ray, mouse);
            else TrySelect(ray);
        }

        lastMouse = mouse;
    }

    private void TrySelect(Ray ray)
    {
        int mask = interact != null ? interact.PickupMask.value : Physics.DefaultRaycastLayers;
        if (Physics.Raycast(ray, out RaycastHit hit, 500f, mask, QueryTriggerInteraction.Ignore))
        {
            var item = hit.collider.GetComponentInParent<ItemPickupNet>();
            if (item != null && item == selected) return;
            if (item != null && editPlot.Contains(item.transform.position))
            {
                Select(item);
                return;
            }
        }
        Deselect();
    }

    private void Select(ItemPickupNet item)
    {
        Deselect();
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(item.itemId, out var def)) return;

        selected = item;
        selectedDef = def;
        desiredPos = item.transform.position;
        desiredRot = item.transform.rotation;
        tool = Tool.Select;
        lastCommitTime = -10f;

        // Show a preview copy and hide the real item locally, so dragging looks immediate
        // even though the host decides where the item actually ends up.
        var template = def.GetGhostPrefab();
        if (template != null)
        {
            ghost = Instantiate(template, desiredPos, desiredRot);
            ghost.SetActive(true);
            foreach (var r in ghost.GetComponentsInChildren<Renderer>())
            {
                foreach (var mat in r.materials)
                {
                    Color c = GhostBuilder.GetColor(mat);
                    c.a = ghostAlpha;
                    GhostBuilder.SetColor(mat, c);
                    ghostMaterials.Add(mat);
                }
            }

            foreach (var r in item.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                r.enabled = false;
                hiddenRenderers.Add(r);
            }
        }
    }

    private void Deselect()
    {
        dragHandle = StoreEditHandles.None;
        hoverHandle = StoreEditHandles.None;

        foreach (var r in hiddenRenderers)
            if (r != null) r.enabled = true;
        hiddenRenderers.Clear();

        foreach (var m in ghostMaterials)
            if (m != null) Destroy(m);
        ghostMaterials.Clear();

        if (ghost != null) Destroy(ghost);
        ghost = null;
        selected = null;
        selectedDef = null;
        tool = Tool.Select;

        if (handles != null) handles.HideAll();
    }

    /* ========================= DRAGGING ========================= */

    private void BeginDrag(int handle, Ray ray, Vector2 mouse)
    {
        GetHandleGeometry(out Vector3 center, out _, out float radius);
        Vector3 axis = StoreEditHandles.Axes[handle];

        dragStartPos = desiredPos;
        dragStartRot = desiredRot;
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
    }

    private void ContinueDrag(Ray ray, Vector2 mouse)
    {
        Vector3 axis = StoreEditHandles.Axes[dragHandle];
        int mask = EditMask();

        if (tool == Tool.Move)
        {
            float t = AxisParam(ray, dragPivot, axis);
            if (float.IsNaN(t)) return;

            // Slide from where the item is now toward the mouse, stopping at the first contact.
            Vector3 target = dragStartPos + axis * (t - dragStartT);
            Vector3 delta = target - desiredPos;
            float dist = delta.magnitude;
            if (dist < 1e-5f) return;

            Vector3 dir = delta / dist;
            float allowed = PlacementCheck.CastDistance(selectedDef, desiredPos, desiredRot, dir, dist,
                                                        mask, selected.transform);
            Vector3 next = desiredPos + dir * allowed;
            if (InsidePlot(next, desiredRot)) desiredPos = next;
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
            if (PlacementCheck.IsClear(selectedDef, nextPos, nextRot, mask, selected.transform) &&
                InsidePlot(nextPos, nextRot))
            {
                desiredPos = nextPos;
                desiredRot = nextRot;
            }
        }
    }

    private void EndDrag()
    {
        dragHandle = StoreEditHandles.None;

        bool moved = (desiredPos - dragStartPos).sqrMagnitude > 1e-8f ||
                     Quaternion.Angle(desiredRot, dragStartRot) > 0.01f;
        if (!moved || selected == null) return;

        RequestEditPoseServerRpc(selected.NetworkObject, desiredPos, desiredRot);
        lastCommitTime = Time.time;
    }

    private void CancelDrag()
    {
        desiredPos = dragStartPos;
        desiredRot = dragStartRot;
        dragHandle = StoreEditHandles.None;
    }

    /* ========================= HANDLES ========================= */

    private void DrawHandles()
    {
        if (handles == null) return;
        if (selected == null) { handles.HideAll(); return; }

        if (ghost != null) ghost.transform.SetPositionAndRotation(desiredPos, desiredRot);

        GetHandleGeometry(out Vector3 center, out float length, out float radius);
        float width = Vector3.Distance(editCam.transform.position, center) * lineWidth;

        if (PlacementCheck.GetTestBox(selectedDef, desiredPos, desiredRot, out Vector3 boxCenter, out Vector3 half))
            handles.DrawBox(boxCenter, half, desiredRot, selected.GravityInEdit ? BoxGravityColor : BoxColor, width * 0.6f);
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
        center = desiredPos;
        if (PlacementCheck.GetTestBox(selectedDef, desiredPos, desiredRot, out Vector3 c, out Vector3 half))
        {
            center = c;
            size = Mathf.Max(half.x, Mathf.Max(half.y, half.z));
        }
        arrowLength = size + 0.6f;
        ringRadius = size * 1.15f + 0.3f;
    }

    private int PickHandle(Vector2 mouse)
    {
        if (tool == Tool.Select) return StoreEditHandles.None;

        GetHandleGeometry(out Vector3 center, out float length, out float radius);
        int best = StoreEditHandles.None;
        float bestDist = grabRadiusPixels;

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

    /* ========================= HELPERS ========================= */

    /// <summary>Layers that stop an item: the placement blocking layers plus the floor.</summary>
    private int EditMask()
    {
        int mask = interact != null ? interact.PlacementBlockingMask.value : Physics.DefaultRaycastLayers;
        int ground = LayerMask.NameToLayer("Ground");
        if (ground >= 0) mask |= 1 << ground;
        return mask;
    }

    private bool InsidePlot(Vector3 pos, Quaternion rot) => InsidePlot(editPlot, selectedDef, pos, rot);

    private static bool InsidePlot(StorePlot plot, ItemDef def, Vector3 pos, Quaternion rot)
    {
        if (plot == null) return false;
        Vector3 center = PlacementCheck.GetTestBox(def, pos, rot, out Vector3 c, out _) ? c : pos;
        return plot.Contains(center);
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

    /* ========================= SERVER ========================= */

    [ServerRpc]
    private void RequestStartEditServerRpc()
    {
        if (MallManager.Instance != null)
            MallManager.Instance.ServerTryStartEdit(OwnerClientId, transform.position);
    }

    [ServerRpc]
    private void RequestStopEditServerRpc()
    {
        if (MallManager.Instance != null)
            MallManager.Instance.ServerStopEdit(OwnerClientId);
    }

    [ServerRpc]
    private void RequestEditPoseServerRpc(NetworkObjectReference itemRef, Vector3 position, Quaternion rotation)
    {
        if (!ServerCanEdit(itemRef, out var item, out var plot)) return;
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(item.itemId, out var def)) return;

        rotation.Normalize();
        if (!InsidePlot(plot, def, position, rotation)) return;
        if (!PlacementCheck.IsClear(def, position, rotation, EditMask(), item.transform)) return;

        item.ServerApplyEditPose(position, rotation);
    }

    [ServerRpc]
    private void RequestSetGravityServerRpc(NetworkObjectReference itemRef, bool on)
    {
        if (ServerCanEdit(itemRef, out var item, out _))
            item.ServerSetGravityInEdit(on);
    }

    /// <summary>Server: is this player editing the store that this item sits in?</summary>
    private bool ServerCanEdit(NetworkObjectReference itemRef, out ItemPickupNet item, out StorePlot plot)
    {
        item = null;
        plot = null;
        if (MallManager.Instance == null || !itemRef.TryGet(out NetworkObject no)) return false;

        item = no.GetComponent<ItemPickupNet>();
        if (item == null) return false;

        plot = MallManager.Instance.GetPlotEditedBy(OwnerClientId);
        return plot != null && plot.Contains(item.transform.position);
    }
}
