using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class PlayerInteractInput : NetworkBehaviour
{
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private LayerMask interactMask;
    [SerializeField] private LayerMask pickupMask;
    [SerializeField] private LayerMask placementMask;
    [SerializeField] private LayerMask placementBlockingMask;

    private Camera cam;
    private PlayerInventoryNet inventory;
    private StoreEditController storeEdit;

    /// <summary>Layers placed items may not overlap. Shared with store edit mode.</summary>
    public LayerMask PlacementBlockingMask => placementBlockingMask;
    /// <summary>Layers items can be picked up from. Edit mode clicks items on these.</summary>
    public LayerMask PickupMask => pickupMask;

    [Header("Rotation")]
    [Tooltip("Degrees per second while holding < or > in preview.")]
    [SerializeField] private float fineRotateSpeed = 45f;

    private float placeRotationY;
    private GameObject ghostPreview;
    private bool previewActive;

    // After a place request, keep the hand item hidden until the server answers.
    // If the server rejects the place (HeldItemId never changes), it reappears after this.
    [SerializeField] private float placeResponseTimeout = 0.5f;
    private float placePendingUntil = -1f;

    [Header("Ghost Settings")]
    [Tooltip("How strongly an invalid placement is washed toward Invalid Color. 0 = no tint at all.")]
    [Range(0f, 1f)]
    [SerializeField] private float invalidTintStrength = 0.35f;
    [SerializeField] private Color invalidColor = new(1f, 0.25f, 0.25f, 1f);
    [Tooltip("Opacity of the preview when the item can be placed there.")]
    [Range(0.02f, 1f)]
    [SerializeField] private float validAlpha = 0.45f;
    [Tooltip("Opacity when the spot is blocked. Keep it below Valid Alpha so blocked ghosts look fainter.")]
    [Range(0.02f, 1f)]
    [SerializeField] private float invalidAlpha = 0.2f;

    /// <summary>Preview opacity when placeable. Safe to set at runtime (e.g. from a settings slider).</summary>
    public float ValidGhostAlpha
    {
        get => validAlpha;
        set { validAlpha = Mathf.Clamp(value, 0.02f, 1f); ghostShownValid = null; }
    }

    /// <summary>Preview opacity when blocked. Safe to set at runtime.</summary>
    public float InvalidGhostAlpha
    {
        get => invalidAlpha;
        set { invalidAlpha = Mathf.Clamp(value, 0.02f, 1f); ghostShownValid = null; }
    }

    // Lets Inspector tweaks during Play mode show up on the current ghost immediately.
    private void OnValidate() => ghostShownValid = null;

    [Header("Debug")]
    [Tooltip("Draw the fit-test box and log what it overlaps (once a second) while previewing.")]
    [SerializeField] private bool debugPlacement;

    private readonly List<(Material mat, Color original)> ghostMaterials = new();
    private bool? ghostShownValid;

    private void Awake()
    {
        inventory = GetComponent<PlayerInventoryNet>();
        storeEdit = GetComponent<StoreEditController>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            cam = GetComponentInChildren<Camera>(true);
    }

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        // Store edit mode or an open menu: drop any placement preview and stand by.
        if ((storeEdit != null && storeEdit.IsEditing) || UIState.IsMenuOpen)
        {
            lookedAtSign = null;
            lookedAtMonitor = null;
            ClearStockTarget();
            if (previewActive || ghostPreview != null)
            {
                previewActive = false;
                DestroyGhost();
                inventory.SetHandVisualHidden(false);
            }
            return;
        }

        UpdateLookedAtSign();
        UpdateStockTarget();
        HandleInteract();
        HandleRotation();
        HandlePlacement();
    }

    /* ========================= INTERACT ========================= */

    private ForSaleSign lookedAtSign;
    private ShopMonitor lookedAtMonitor;

    /// <summary>Is the player looking at a for-sale wall of an unowned store, or at their shop monitor?</summary>
    private void UpdateLookedAtSign()
    {
        lookedAtSign = null;
        lookedAtMonitor = null;
        if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit,
                interactRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var sign = hit.collider.GetComponentInParent<ForSaleSign>();
            if (sign != null && sign.Plot != null && sign.Plot.IsForSale) lookedAtSign = sign;

            var monitor = hit.collider.GetComponentInParent<ShopMonitor>();
            if (monitor != null && monitor.CanUse(OwnerClientId)) lookedAtMonitor = monitor;
        }
    }

    /* ========================= STOCKING ========================= */

    [Header("Stocking")]
    [Tooltip("Opacity of the preview showing where a stock crate will go.")]
    [Range(0.02f, 1f)]
    [SerializeField] private float stockPreviewAlpha = 0.45f;

    [Tooltip("While holding a stock crate: put the product you're looking at back into the crate.")]
    [SerializeField] private KeyCode putBackKey = KeyCode.G;
    [Tooltip("Turn the products a quarter turn: the next ones you stock in an empty spot, or the ones already in the spot you're looking at.")]
    [SerializeField] private KeyCode stockTurnKey = KeyCode.R;
    [Tooltip("Shelves only: stand products up / lay them down (front facing up).")]
    [SerializeField] private KeyCode stockLayKey = KeyCode.T;

    private int stockOrient; // the player's pick for empty spots (StockContainer.Orientation)

    private StockContainer stockTarget;
    private int stockZone = -1;       // F: the zone to stock, or (empty hands) the zone to take from
    private bool stockTargetIsTake;   // true: F takes a product off; false: F puts one from the held crate on
    private int putBackZone = -1;     // G: a zone holding the held crate's product, to put one back into it
    private GameObject stockPreview;
    private GameObject stockPreviewSource;
    private static readonly Dictionary<GameObject, GameObject> StockGhostTemplates = new();

    /// <summary>
    /// Holding a stock crate: the zone nearest where you're looking that has room for it (F), and
    /// the nearest zone holding the same product if the crate has room for one back (G).
    /// Empty hands: the stocked zone nearest where you're looking (F).
    /// </summary>
    private void UpdateStockTarget()
    {
        stockTarget = null;
        stockZone = -1;
        putBackZone = -1;

        int held = inventory.HeldItemId.Value;
        bool holdingCrate = held != 0 && StockContainer.IsStockCrate(held);
        bool placePending = Time.time < placePendingUntil;

        if ((held == 0 || holdingCrate) && !previewActive && !placePending &&
            Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit,
                interactRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var container = hit.collider.GetComponentInParent<StockContainer>();
            if (container != null && container.IsSpawned &&
                MallManager.CanPickupOrPlace(OwnerClientId, transform.position, container.transform.position))
            {
                stockTargetIsTake = held == 0;
                stockZone = container.FindNearestZone(hit.point, held, wantStock: held == 0, orient: stockOrient);
                if (holdingCrate && inventory.HeldUnits.Value < StockContainer.GetUnitsPerCrate(held))
                    putBackZone = container.FindNearestZone(hit.point, held, wantStock: true);
                if (stockZone >= 0 || putBackZone >= 0) stockTarget = container;
            }
        }

        // Show the product where it'll go.
        if (stockTarget != null && !stockTargetIsTake && stockZone >= 0 &&
            stockTarget.TryGetNextSlot(stockZone, held, stockOrient, out Vector3 slotPos, out Quaternion slotRot))
            ShowStockPreview(StockContainer.GetStockedPrefab(held), slotPos, slotRot);
        else DestroyStockPreview();
    }

    private void ClearStockTarget()
    {
        stockTarget = null;
        stockZone = -1;
        putBackZone = -1;
        DestroyStockPreview();
    }

    private void ShowStockPreview(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (prefab == null) { DestroyStockPreview(); return; }

        if (stockPreview == null || stockPreviewSource != prefab)
        {
            DestroyStockPreview();
            if (!StockGhostTemplates.TryGetValue(prefab, out var template) || template == null)
                StockGhostTemplates[prefab] = template = GhostBuilder.BuildTemplate(prefab, stockPreviewAlpha);
            stockPreview = Instantiate(template);
            stockPreview.SetActive(true);
            foreach (var c in stockPreview.GetComponentsInChildren<Collider>()) c.enabled = false;
            stockPreviewSource = prefab;
        }
        stockPreview.transform.SetPositionAndRotation(position, rotation);
    }

    private void DestroyStockPreview()
    {
        if (stockPreview != null) Destroy(stockPreview);
        stockPreview = null;
        stockPreviewSource = null;
    }

    public override void OnDestroy()
    {
        DestroyStockPreview();
        base.OnDestroy();
    }

    private static GUIStyle promptStyle; // made once; a new GUIStyle every OnGUI call makes garbage
    private static readonly GUIContent PromptContent = new();

    private void OnGUI()
    {
        if (!IsOwner || (lookedAtSign == null && lookedAtMonitor == null && stockTarget == null) ||
            (storeEdit != null && storeEdit.IsEditing) || UIState.IsMenuOpen) return;

        string text = lookedAtMonitor != null ? "F: Shop monitor"
                    : lookedAtSign != null ? $"F: Buy {lookedAtSign.Plot.DisplayName} ({lookedAtSign.Plot.Price})"
                    : StockPrompt();
        promptStyle ??= new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        PromptContent.text = text;
        Vector2 size = promptStyle.CalcSize(PromptContent) + new Vector2(20f, 10f);
        GUI.Box(new Rect((Screen.width - size.x) * 0.5f, Screen.height * 0.6f, size.x, size.y), PromptContent, promptStyle);
    }

    private string StockPrompt()
    {
        if (stockTargetIsTake)
        {
            var z = stockTarget.GetZone(stockZone);
            return $"F: Take {ItemName(z.itemId)} ({z.count} on shelf)" + OrientPrompt();
        }

        int held = inventory.HeldItemId.Value;
        string text = $"{ItemName(held)}: {inventory.HeldUnits.Value}/{StockContainer.GetUnitsPerCrate(held)} in crate";
        if (stockZone >= 0)
        {
            var z = stockTarget.GetZone(stockZone);
            text = $"F: Stock {text}   (shelf {(z.itemId == held ? z.count : 0)}/{stockTarget.GetCapacity(stockZone, held, stockOrient)})";
        }
        if (putBackZone >= 0) text += $"    {putBackKey}: Put back";
        return text + OrientPrompt();
    }

    private string OrientPrompt()
    {
        int zone = OrientZone();
        if (zone < 0) return "";
        string text = $"\n{stockTurnKey}: Turn";
        if (!stockTarget.IsHangRail(zone))
        {
            var z = stockTarget.GetZone(zone);
            int orient = z.count > 0 ? z.orient : stockOrient;
            text += $"   {stockLayKey}: {(StockContainer.Orientation.Lying(orient) ? "Stand up" : "Lay down")}";
        }
        return text;
    }

    /// <summary>The spot R / T act on: the one being stocked (or taken from), else the put-back one.</summary>
    private int OrientZone() => stockTarget == null ? -1 : stockZone >= 0 ? stockZone : putBackZone;

    /// <summary>
    /// R / T: a spot that already has products is changed for everyone (if they all still fit that
    /// way); an empty spot just changes how the next product will go in.
    /// </summary>
    private void HandleStockOrientation()
    {
        bool turn = Input.GetKeyDown(stockTurnKey);
        bool lay = Input.GetKeyDown(stockLayKey);
        int zone = OrientZone();
        if ((!turn && !lay) || zone < 0) return;
        if (lay && stockTarget.IsHangRail(zone)) return;

        var z = stockTarget.GetZone(zone);
        int current = z.count > 0 ? z.orient : stockOrient;
        int next = turn ? StockContainer.Orientation.Turn(current) : StockContainer.Orientation.ToggleLying(current);

        if (z.count > 0) inventory.RequestOrientZoneServerRpc(stockTarget.NetworkObject, zone, next);
        stockOrient = next; // remember it for the next empty spot too
    }

    private static string ItemName(int itemId) =>
        ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def) ? def.displayName : "item";

    private void HandleInteract()
    {
        // Put a product back into the held crate.
        if (Input.GetKeyDown(putBackKey) && stockTarget != null && putBackZone >= 0)
        {
            inventory.RequestUnstockServerRpc(stockTarget.NetworkObject, putBackZone);
            return;
        }

        HandleStockOrientation();

        if (!Input.GetKeyDown(KeyCode.F)) return;

        // The shop monitor opens the shop GUI for its store (local only; orders go through the host).
        if (lookedAtMonitor != null)
        {
            if (ShopUIController.Instance != null) ShopUIController.Instance.Open(lookedAtMonitor.GetPlot());
            else Debug.LogWarning("[PlayerInteractInput] No ShopUIController in the scene to open.");
            return;
        }

        // Buying a store from its for-sale wall works whether or not you're holding something.
        if (lookedAtSign != null)
        {
            if (MallManager.Instance != null)
                MallManager.Instance.RequestBuyFromSignServerRpc(lookedAtSign.Plot.PlotId);
            return;
        }

        // Stocking a shelf, or taking stock off it. (An empty shelf is picked up as usual below.)
        if (stockTarget != null && stockZone >= 0)
        {
            if (stockTargetIsTake) inventory.RequestUnstockServerRpc(stockTarget.NetworkObject, stockZone);
            else
            {
                inventory.RequestStockServerRpc(stockTarget.NetworkObject, stockZone, stockOrient);
                // Last product: the crate empties and goes away, so hide it until the server answers.
                if (inventory.HeldUnits.Value <= 1)
                {
                    inventory.SetHandVisualHidden(true);
                    placePendingUntil = Time.time + placeResponseTimeout;
                    DestroyStockPreview();
                }
            }
            return;
        }

        if (inventory.HeldItemId.Value == 0)
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward,
                out RaycastHit hit, interactRange, pickupMask))
            {
                var no = hit.collider.GetComponentInParent<NetworkObject>();
                var pickup = hit.collider.GetComponentInParent<ItemPickupNet>();

                if (no && pickup)
                    inventory.RequestPickupServerRpc(no);
            }
        }
        else
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward,
                out RaycastHit hit, interactRange, interactMask))
            {
                var no = hit.collider.GetComponentInParent<NetworkObject>();
                var interactable = hit.collider.GetComponentInParent<InteractableNet>();

                if (no && interactable)
                    inventory.RequestUseOnServerRpc(no);
            }
        }
    }

    /* ========================= ROTATION ========================= */

    /// <summary>True while the placement preview is up.</summary>
    public bool IsPreviewing => ghostPreview != null;

    private void HandleRotation()
    {
        // Fine rotation while previewing: hold > to turn clockwise, < to turn counter-clockwise.
        // These are the , and . keys, so Shift isn't needed.
        if (IsPreviewing)
        {
            bool cw = Input.GetKey(KeyCode.Period); // >
            bool ccw = Input.GetKey(KeyCode.Comma);  // <
            float dir = (cw ? 1f : 0f) - (ccw ? 1f : 0f);
            if (dir != 0f)
                placeRotationY = Mathf.Repeat(placeRotationY + dir * fineRotateSpeed * Time.deltaTime, 360f);
        }

        // Tap R: jump to the next exact 90  step. After fine-tuning to e.g. 17 ,
        // a tap lands on 90 , so you can always get back to a clean right angle.
        if (Input.GetKeyDown(KeyCode.R))
        {
            float nextStep = (Mathf.Floor(placeRotationY / 90f + 0.001f) + 1f) * 90f;
            placeRotationY = Mathf.Repeat(nextStep, 360f);
        }
    }

    /* ========================= PLACEMENT ========================= */

    private void HandlePlacement()
    {
        if (inventory.HeldItemId.Value == 0)
        {
            DestroyGhost();
            previewActive = false;
            placePendingUntil = -1f;
            return;
        }

        bool placePending = Time.time < placePendingUntil;

        // Right-click toggles the preview on/off; Esc also cancels it.
        if (Input.GetMouseButtonDown(1) && !placePending)
            previewActive = !previewActive;
        if (previewActive && Input.GetKeyDown(KeyCode.Escape))
            previewActive = false;

        bool previewing = previewActive;
        bool confirmPlace = Input.GetMouseButtonDown(0);

        if (previewing && !placePending)
        {
            UpdateGhostPreview();
            inventory.SetHandVisualHidden(true);
        }
        else
        {
            DestroyGhost();
            // Cancelled (or server rejected the place): put the item back in hand.
            if (!placePending)
                inventory.SetHandVisualHidden(false);
        }

        if (confirmPlace && ghostPreview != null)
        {
            if (!IsPlacementValid(ghostPreview)) return;

            if (TryGetPlacementPose(out Vector3 pos, out Quaternion rot))
            {
                inventory.RequestPlaceServerRpc(pos, rot);
                DestroyGhost();
                previewActive = false;
                // Stay hidden so the item doesn't flash back into the hand before the server confirms.
                placePendingUntil = Time.time + placeResponseTimeout;
            }
        }
    }

    /* ========================= GHOST ========================= */

    private void UpdateGhostPreview()
    {
        if (ghostPreview == null)
            CreateGhost();

        if (ghostPreview == null) return;

        if (TryGetPlacementPose(out Vector3 pos, out Quaternion rot))
        {
            ghostPreview.transform.SetPositionAndRotation(pos, rot);
        }

        bool valid = IsPlacementValid(ghostPreview);
        UpdateGhostColor(valid);

        if (debugPlacement)
            DebugPlacement(ghostPreview.transform.position, ghostPreview.transform.rotation, valid);
    }

    private void CreateGhost()
    {
        if (!ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def))
            return;

        var prefab = def.GetGhostPrefab();
        if (prefab == null) return;

        ghostPreview = Instantiate(prefab);
        ghostPreview.SetActive(true);

        // Per-ghost material copies so tinting never touches the cached template.
        ghostMaterials.Clear();
        foreach (var r in ghostPreview.GetComponentsInChildren<Renderer>())
            foreach (var mat in r.materials)
                ghostMaterials.Add((mat, GhostBuilder.GetColor(mat)));

        ghostShownValid = null;
    }

    private void DestroyGhost()
    {
        if (ghostPreview == null) return;

        foreach (var (mat, _) in ghostMaterials)
            if (mat != null) Destroy(mat);
        ghostMaterials.Clear();

        Destroy(ghostPreview);
        ghostPreview = null;
        ghostShownValid = null;
    }

    /// <summary>
    /// Valid: the item's own colors, just transparent. Invalid: a light red wash.
    /// Only touches materials when validity actually changes.
    /// </summary>
    private void UpdateGhostColor(bool valid)
    {
        if (ghostShownValid == valid) return;
        ghostShownValid = valid;

        foreach (var (mat, original) in ghostMaterials)
        {
            if (mat == null) continue;

            Color c = valid ? original : Color.Lerp(original, invalidColor, invalidTintStrength);
            // Blocked ghosts fade out further, so "can't place" reads even when the
            // item's own color is close to the red tint.
            c.a = valid ? validAlpha : invalidAlpha;
            GhostBuilder.SetColor(mat, c);
        }
    }

    /* ========================= PLACEMENT LOGIC ========================= */

    private bool TryGetPlacementPose(out Vector3 pos, out Quaternion rot)
    {
        Vector3 rayStart = cam.transform.position + cam.transform.forward * 0.5f;

        Quaternion yaw = Quaternion.Euler(0f, placeRotationY, 0f);

        if (Physics.Raycast(rayStart, cam.transform.forward,
            out RaycastHit hit, 5f, placementMask, QueryTriggerInteraction.Ignore))
        {
            pos = hit.point;

            // Items always stand upright. Aiming at the top of something puts the item on it;
            // aiming at a side leaves it hanging in the air, so the support check turns it red.
            // (Wall-mounted items can tilt to the surface later via a floor/wall placement type.)
            rot = yaw;
        }
        else
        {
            pos = cam.transform.position + cam.transform.forward * 2f;
            rot = yaw;
        }

        // Sit the item on the surface by its measured bottom, not its pivot,
        // so models with a centered pivot don't sink halfway into the floor.
        if (ItemCatalog.Instance != null &&
            ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def))
        {
            pos += rot * Vector3.up * PlacementCheck.PivotHeightAboveBase(def);
        }

        return true;
    }

    /* ========================= DEBUG ========================= */

    private float nextDebugLogTime;

    /// <summary>
    /// Draws the fit-test box (Scene view, or Game view with Gizmos on) and, once a second,
    /// logs every collider inside it and whether the fit test counts it.
    /// </summary>
    private void DebugPlacement(Vector3 pos, Quaternion rot, bool valid)
    {
        if (!ItemCatalog.Instance.TryGet(inventory.HeldItemId.Value, out var def)) return;

        if (!PlacementCheck.GetTestBox(def, pos, rot, out Vector3 center, out Vector3 half))
        {
            if (Time.time >= nextDebugLogTime)
            {
                nextDebugLogTime = Time.time + 1f;
                Debug.LogWarning($"[Placement] '{def.displayName}' has no measurable size " +
                                 "(no colliders or meshes found on its world prefab).", this);
            }
            return;
        }

        DrawBox(center, half, rot, valid ? Color.green : Color.red);

        if (Time.time < nextDebugLogTime) return;
        nextDebugLogTime = Time.time + 1f;

        int ground = LayerMask.NameToLayer("Ground");
        var all = Physics.OverlapBox(center, half, rot, ~0, QueryTriggerInteraction.Collide);

        var sb = new System.Text.StringBuilder();
        sb.Append($"[Placement] '{def.displayName}' valid={valid}, box size={(half * 2f):F2}, ")
          .Append($"overlapping {all.Length} collider(s):");

        foreach (var c in all)
        {
            int layer = c.gameObject.layer;
            bool inMask = (placementBlockingMask.value & (1 << layer)) != 0;

            string verdict =
                c.isTrigger ? "IGNORED (trigger)" :
                layer == ground ? "IGNORED (Ground layer never blocks)" :
                !inMask ? "IGNORED (layer not in Placement Blocking Mask)" :
                                   "BLOCKS";

            sb.Append($"\n  - '{c.name}' (root '{c.transform.root.name}') layer '{LayerMask.LayerToName(layer)}' -> {verdict}");
        }

        Debug.Log(sb.ToString(), this);
    }

    private static void DrawBox(Vector3 c, Vector3 h, Quaternion r, Color col)
    {
        Vector3 P(float x, float y, float z) => c + r * new Vector3(h.x * x, h.y * y, h.z * z);

        Vector3[] p =
        {
            P(-1,-1,-1), P( 1,-1,-1), P( 1,-1, 1), P(-1,-1, 1),
            P(-1, 1,-1), P( 1, 1,-1), P( 1, 1, 1), P(-1, 1, 1),
        };
        for (int i = 0; i < 4; i++)
        {
            Debug.DrawLine(p[i], p[(i + 1) % 4], col); // bottom
            Debug.DrawLine(p[i + 4], p[(i + 1) % 4 + 4], col); // top
            Debug.DrawLine(p[i], p[i + 4], col); // sides
        }
    }

    private bool IsPlacementValid(GameObject ghost)
    {
        if (ghost == null) return false;

        // Same store rule the server applies, so the ghost goes red before you even click.
        if (!MallManager.CanPickupOrPlace(OwnerClientId, transform.position, ghost.transform.position))
            return false;
        if (MallManager.IsOthersShipmentArea(OwnerClientId, ghost.transform.position))
            return false;

        return FitsAt(inventory.HeldItemId.Value, ghost.transform.position, ghost.transform.rotation);
    }

    /// <summary>
    /// Fit test using this player's placement layers. Called on the owner for the preview
    /// and on the server before spawning (see PlayerInventoryNet.RequestPlaceServerRpc).
    /// </summary>
    public bool FitsAt(int itemId, Vector3 position, Quaternion rotation)
    {
        if (ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def))
            return false;

        // The floor must never count as "in the way", even if it's ticked in the blocking mask.
        int ground = LayerMask.NameToLayer("Ground");
        int blocking = placementBlockingMask.value;
        if (ground >= 0) blocking &= ~(1 << ground);

        return PlacementCheck.Fits(def, position, rotation, blocking, placementMask);
    }
}