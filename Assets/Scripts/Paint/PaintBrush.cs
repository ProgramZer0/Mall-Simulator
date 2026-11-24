using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The paint brush. Put it on the player prefab. Tap 2 (with empty hands) to take it out or put it
/// away; hold 2 to open the color wheel, point at a color and let go to pick it (that takes the brush
/// out too). With the brush out, aim at something of yours and click to paint it:
/// - an item with PaintParts in your store
/// - a built wall, the storefront or the floor of your store
/// Each paint job costs the PaintPalette's fee for that kind of thing, paid straight away. Painting
/// something the color it already is does nothing.
///
/// Only one hand tool at a time: taking the brush out puts the price scanner away and the other way
/// round. Not available in store edit mode.
/// </summary>
public class PaintBrush : NetworkBehaviour
{
    [SerializeField] private KeyCode key = KeyCode.Alpha2;
    [Tooltip("Holding the key this long opens the color wheel instead of taking the brush out / putting it away.")]
    [SerializeField] private float holdSeconds = 0.25f;
    [Tooltip("Optional: the brush in your hand. Give it PaintParts (e.g. on the bristles) to show the color picked.")]
    [SerializeField] private GameObject handVisualPrefab;
    [Tooltip("Where the brush is held. Empty = the inventory's Hand Anchor (where held items show). Set it to a " +
             "hand bone later to put the brush in the hand.")]
    [SerializeField] private Transform hand;
    [Tooltip("Where the brush sits from the hand: right, up, forward (meters). The default keeps it low in " +
             "the right corner of the view.")]
    [SerializeField] private Vector3 handOffset = new(0.3f, -0.25f, 0.15f);
    [Tooltip("How the brush is turned in the hand (degrees). Change this if the model points the wrong way.")]
    [SerializeField] private Vector3 handRotation = new(0f, -90f, 0f);
    [Tooltip("How far you can paint, in meters.")]
    [SerializeField] private float range = 4f;
    [Tooltip("Log every paint click, what the host does with it (and why not), and what gets painted on each " +
             "machine: each renderer, its materials, and whether they have a base color and a texture.")]
    [SerializeField] private bool debugLogs;
    [Tooltip("Items this far outside your store's space still count as yours (windows and doors in the front " +
             "wall stick out a little), in meters.")]
    [SerializeField] private float storeBuffer = 0.75f;
    [Tooltip("Outline color for what a click would paint.")]
    [SerializeField] private Color outlineColor = new(1f, 0.85f, 0.3f, 1f);

    /// <summary>Is the local player's brush out? (Other tools and clicks check this.)</summary>
    public static bool LocalActive { get; private set; }

    private bool active;
    private byte color = 1;
    private Camera cam;
    private PlayerInventoryNet inventory;
    private StoreEditController storeEdit;
    private PriceScanner scanner;
    private GameObject handVisual;

    private float keyDownAt = -1f;
    private bool wheelOpen;
    private byte wheelHover;

    private enum Kind { None, Item, Wall, Front, Floor }
    private Kind targetKind;
    private ItemPickupNet targetItem;
    private int targetZone;
    private int targetModule; // storefront: the front wall type painted (targetZone is its part)
    private StorePlot targetPlot;
    private BuiltWall targetWall;
    private GameObject targetObject;
    private string prompt;

    /// <summary>What the brush would do right now (shown by PlayerInteractInput's prompt).</summary>
    public string Prompt => active ? prompt : null;
    public bool IsActive => active;

    private void Awake()
    {
        inventory = GetComponent<PlayerInventoryNet>();
        storeEdit = GetComponent<StoreEditController>();
        scanner = GetComponent<PriceScanner>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) cam = GetComponentInChildren<Camera>(true);
        if (IsOwner && debugLogs) PaintPalette.DebugApply = true;
    }

    private void DLog(string message)
    {
        if (debugLogs) Debug.Log($"[Paint] {message}", this);
    }

    private string Describe(ItemPickupNet item) =>
        item == null ? "nothing" : $"'{item.name}' (item {item.ItemId}, net id {item.NetworkObjectId}, paint {item.Paint:X8})";

    public override void OnNetworkDespawn()
    {
        SetActive(false);
        CloseWheel(false);
    }

    public override void OnDestroy()
    {
        if (IsOwner) LocalActive = false;
        UIState.CloseMenu(this);
        TargetOutline.Release(this);
        base.OnDestroy();
    }

    /// <summary>Put the brush away (another tool is coming out).</summary>
    public void PutAway() => SetActive(false);

    private void Update()
    {
        if (!IsOwner || cam == null) return;

        bool editing = storeEdit != null && storeEdit.IsEditing;
        bool holding = (inventory != null && inventory.HeldItemId.Value != 0) || CartNet.GetPushedBy(OwnerClientId) != null;
        if (editing || holding)
        {
            if (active) SetActive(false);
            if (wheelOpen) CloseWheel(false);
            keyDownAt = -1f;
            return;
        }

        if (wheelOpen)
        {
            UpdateWheel();
            if (!Input.GetKey(key)) CloseWheel(true);
            return;
        }
        if (UIState.IsMenuOpen || PriceScanner.LocalTyping) { keyDownAt = -1f; return; } // digits are a price

        // Tap: take out / put away. Hold: the color wheel.
        if (Input.GetKeyDown(key)) keyDownAt = Time.unscaledTime;
        if (keyDownAt >= 0f)
        {
            if (!Input.GetKey(key))
            {
                keyDownAt = -1f;
                SetActive(!active);
            }
            else if (Time.unscaledTime - keyDownAt >= holdSeconds)
            {
                keyDownAt = -1f;
                OpenWheel();
                return;
            }
        }

        if (!active) return;
        FindTarget();
        UpdatePreview();
        if (targetObject != null) TargetOutline.For(this).ShowObject(targetObject, outlineColor);
        else TargetOutline.For(this).Hide();
        if (Input.GetMouseButtonDown(0) && targetKind != Kind.None) Paint();
    }

    /* ========================= PREVIEW ========================= */

    // What's shown in the brush's color right now (this machine only), so it can be put back.
    private Kind previewKind;
    private Object previewObject; // the item, or the wall piece
    private StorePlot previewPlot;
    private int previewZone;
    private int previewWallId;
    private byte previewColor;

    // Show what's under the brush in the brush's color; put it back once the brush moves off it.
    // After a click it stays in the new color until the host's change arrives, so nothing flickers.
    private void UpdatePreview()
    {
        Object obj = targetKind == Kind.Item ? targetItem : targetKind == Kind.Wall ? targetObject : null;
        bool same = targetKind == previewKind && obj == previewObject && targetPlot == previewPlot &&
                    targetZone == previewZone && color == previewColor &&
                    (targetKind != Kind.Wall || targetWall.id == previewWallId);
        if (same) return;

        EndPreview();
        if (targetKind == Kind.None) return;
        previewKind = targetKind;
        previewObject = obj;
        previewPlot = targetPlot;
        previewZone = targetZone;
        previewWallId = targetWall.id;
        previewColor = color;

        switch (targetKind)
        {
            case Kind.Item:
                var zones = PaintParts.ZonesOf(targetItem.gameObject);
                if (targetZone < zones.Count) zones[targetZone].Apply(color);
                break;
            case Kind.Wall:
                if (targetObject.TryGetComponent<CuttableWall>(out var w)) w.SetPaint(color);
                break;
            case Kind.Front: targetPlot.PreviewFrontPaint(targetModule, targetZone, color); break;
            case Kind.Floor: targetPlot.PreviewFloorPaint(color); break;
        }
    }

    // Back to the synced colors.
    private void EndPreview()
    {
        switch (previewKind)
        {
            case Kind.Item:
                if (previewObject is ItemPickupNet item && item != null)
                {
                    var zones = PaintParts.ZonesOf(item.gameObject);
                    if (previewZone < zones.Count) zones[previewZone].Apply(PaintSet.Get(item.Paint, previewZone));
                }
                break;
            case Kind.Wall:
                if (previewObject is GameObject piece && piece != null && piece.TryGetComponent<CuttableWall>(out var w) &&
                    previewPlot != null && previewPlot.TryGetBuiltWallById(previewWallId, out var wall))
                    w.SetPaint(wall.paint);
                break;
            case Kind.Front:
            case Kind.Floor:
                if (previewPlot != null) previewPlot.ShowPaint();
                break;
        }
        previewKind = Kind.None;
        previewObject = null;
        previewPlot = null;
    }

    private void SetActive(bool on)
    {
        if (on && scanner != null) scanner.PutAway();
        active = on;
        if (IsOwner) LocalActive = on;
        if (!on)
        {
            prompt = null;
            targetKind = Kind.None;
            EndPreview();
            TargetOutline.For(this).Hide();
        }

        Transform holdAt = hand != null ? hand : inventory != null ? inventory.HandAnchor : null;
        if (on && handVisual == null && handVisualPrefab != null && holdAt != null)
        {
            handVisual = Instantiate(handVisualPrefab, holdAt);
            handVisual.transform.localPosition = handOffset;
            handVisual.transform.localRotation = Quaternion.Euler(handRotation);
            if (IsOwner) HeldItemBlocker.Add(handVisual, transform, inventory.HeldItemBlockedBy);
            PaintHand();
        }
        if (!on && handVisual != null)
        {
            Destroy(handVisual);
            handVisual = null;
        }
    }

    private void PaintHand()
    {
        if (handVisual != null) PaintParts.ApplySet(handVisual, PaintSet.All(color));
    }

    /* ========================= COLOR WHEEL ========================= */

    private void OpenWheel()
    {
        if (PaintPalette.Instance == null) return;
        wheelOpen = true;
        wheelHover = color;
        UIState.OpenMenu(this); // frees the cursor and stops looking around while picking
        var wheel = PaintWheel.GetOrCreate(); // a plain one if the scene has none
        wheel.SetOpen(true);
        wheel.Highlight(wheelHover);
    }

    private void UpdateWheel()
    {
        Vector2 fromCenter = (Vector2)Input.mousePosition - new Vector2(Screen.width, Screen.height) * 0.5f;
        if (fromCenter.sqrMagnitude < 30f * 30f) return; // still in the middle: keep the current pick
        if (PaintWheel.Instance == null) return;
        wheelHover = PaintWheel.Instance.PickColor(fromCenter);
        PaintWheel.Instance.Highlight(wheelHover);
    }

    private void CloseWheel(bool pick)
    {
        if (!wheelOpen) return;
        wheelOpen = false;
        UIState.CloseMenu(this);
        if (PaintWheel.Instance != null) PaintWheel.Instance.SetOpen(false);
        if (!pick) return;
        color = wheelHover;
        PaintHand();
        if (!active) SetActive(true);
    }

    /* ========================= TARGET ========================= */

    private void FindTarget()
    {
        targetKind = Kind.None;
        targetItem = null;
        targetPlot = null;
        targetObject = null;
        prompt = $"Brush: {PaintPalette.NameOf(color)}   (hold {Key()} to pick a color, tap to put away)";

        var palette = PaintPalette.Instance;
        if (palette == null) { prompt = "No PaintPalette in the scene"; return; }
        if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, range,
                             Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;

        Transform t = hit.collider.transform;
        var mall = MallManager.Instance;
        byte current;
        int fee;
        string what;

        var item = t.GetComponentInParent<ItemPickupNet>();
        if (item != null && !item.IsSpawned) return; // not a live item (an edit copy, one being taken away)
        if (item != null)
        {
            if (!PaintParts.IsPaintable(item.gameObject)) { prompt = "This can't be painted"; return; }
            if (IsStock(item)) { prompt = "Stock stays the color it was bought in"; return; }
            var plot = PlotOf(item);
            if (plot == null || !plot.IsAuthorized(OwnerClientId)) { prompt = "Not in your store"; return; }
            // Items can have several paint zones (a door's frame and its doors): the one pointed at.
            var zones = PaintParts.ZonesOf(item.gameObject);
            targetZone = Mathf.Max(0, PaintParts.ZoneAt(item.gameObject, hit.point));
            targetKind = Kind.Item;
            targetItem = item;
            targetPlot = plot;
            targetObject = zones.Count > 1 ? zones[targetZone].gameObject : item.gameObject;
            current = PaintSet.Get(item.Paint, targetZone);
            fee = palette.ItemFee;
            var def = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(item.ItemId, out var d) ? d : null;
            what = def != null ? def.displayName : "Item";
            // Not a color this item comes in (or not unlocked yet): say so.
            if (current != color && !ColorRules.Allows(def, color, Team, out string why))
            {
                prompt = why;
                targetKind = Kind.None;
                return;
            }
            string zone = PaintParts.ZoneName(item.gameObject, targetZone);
            if (zone.Length > 0) what += $" ({zone})";
        }
        else
        {
            var piece = t.GetComponentInParent<BuiltWallPiece>();
            var plot = piece != null ? piece.Plot : mall != null ? mall.GetPlotAt(hit.point + hit.normal * 0.05f) : null;
            if (plot == null) plot = FindStorefrontOwner(t);
            if (plot == null) return;
            if (!plot.IsAuthorized(OwnerClientId)) { prompt = "Not your store"; return; }
            if (plot.IsRenovating) { prompt = "This store is being renovated"; return; }

            if (piece != null && plot.TryGetBuiltWallById(piece.WallId, out var wall))
            {
                targetKind = Kind.Wall;
                targetWall = wall;
                targetObject = piece.gameObject;
                current = wall.paint;
                fee = palette.WallFee(wall.Length);
                what = $"Wall ({wall.Length:0.0} m)";
            }
            else if (plot.TryGetFrontZoneAt(t, hit.point, out targetModule, out targetZone, out string part))
            {
                // One part of this front wall type, on every section showing it.
                targetKind = Kind.Front;
                current = plot.GetFrontPaint(targetModule, targetZone);
                fee = palette.FrontFee;
                what = part.Length > 0 ? $"Storefront ({part})" : "Storefront";
            }
            else if (plot.IsFloor(t))
            {
                targetKind = Kind.Floor;
                current = plot.FloorPaint;
                fee = palette.FloorFee;
                what = "Floor";
            }
            else return;
            targetPlot = plot;
            if (current != color && !ColorRules.AllowsPlain(color, Team, out string why))
            {
                prompt = why;
                targetKind = Kind.None;
                return;
            }
        }

        if (current == color)
        {
            prompt = $"{what} is already {PaintPalette.NameOf(color)}";
            targetKind = Kind.None;
            return;
        }
        prompt = $"Click: paint {what} {PaintPalette.NameOf(color)}" + (fee > 0 ? $" (${fee})" : "");
    }

    // The store an item belongs to: the store whose wall a window or door is in, otherwise the store its
    // middle is in or just outside of (Store Buffer).
    private StorePlot PlotOf(ItemPickupNet item)
    {
        if (item.TryGetComponent<WallOpening>(out var opening))
        {
            var host = CuttableWall.HostOf(opening);
            if (host != null && host.Plot != null) return host.Plot;
        }
        var mall = MallManager.Instance;
        if (mall == null) return null;
        Vector3 center = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(item.ItemId, out var def) &&
                         PlacementCheck.GetTestBox(def, item.transform.position, item.transform.rotation, out Vector3 c, out _)
            ? c : item.transform.position;
        return mall.GetPlotNear(center, storeBuffer);
    }

    // A storefront panel just outside the store's space still belongs to it.
    private static StorePlot FindStorefrontOwner(Transform t)
    {
        var wall = t.GetComponentInParent<CuttableWall>();
        return wall != null && wall.IsStorefront ? wall.Plot : null;
    }

    private void Paint()
    {
        DLog($"Click: {targetKind} {(targetKind == Kind.Item ? Describe(targetItem) : targetObject != null ? $"'{targetObject.name}'" : "")} " +
             $"zone {targetZone}{(targetKind == Kind.Front ? $" (front type {targetModule})" : "")} → {PaintPalette.NameOf(color)} (id {color}), " +
             $"store '{(targetPlot != null ? targetPlot.DisplayName : "none")}'.");
        if (targetPlot == null) { DLog("  no store: nothing sent."); return; }
        Vector3 at = targetItem != null ? targetItem.transform.position : targetObject != null ? targetObject.transform.position : transform.position;
        Sfx.ForAll(Cue.Paint, at, AnimCue.Paint, OwnerClientId);
        switch (targetKind)
        {
            case Kind.Item:
                if (targetItem != null && targetItem.IsSpawned) RequestPaintItemRpc(targetItem.NetworkObject, targetZone, color);
                break;
            case Kind.Wall: RequestPaintWallRpc(targetPlot.PlotId, targetWall.id, color); break;
            case Kind.Front: RequestPaintFrontRpc(targetPlot.PlotId, targetModule, targetZone, color); break;
            case Kind.Floor: RequestPaintFloorRpc(targetPlot.PlotId, color); break;
        }
    }

    private string Key() => key.ToString().Replace("Alpha", "");

    /* ========================= HOST ========================= */

    // Stock (a crate of it, or a product that's sold) keeps the color it was bought in: picking the color
    // is part of buying it.
    private static bool IsStock(ItemPickupNet item) => item != null && (item.IsStockCrate || Sellable.IsSellable(item.ItemId));

    [Rpc(SendTo.Server)]
    private void RequestPaintItemRpc(NetworkObjectReference itemRef, int zone, byte paint, RpcParams rpcParams = default)
    {
        if (!itemRef.TryGet(out NetworkObject no) || !no.TryGetComponent<ItemPickupNet>(out var item)) { DLog("Host: item not found."); return; }
        if (IsStock(item)) { DLog($"Host: {Describe(item)} is stock: it keeps the color it was bought in."); return; }
        int zones = PaintParts.ZonesOf(item.gameObject).Count;
        if (zone < 0 || zone >= zones) { DLog($"Host: {Describe(item)} has {zones} zone(s), not zone {zone}."); return; }
        if (PaintSet.Get(item.Paint, zone) == paint) { DLog($"Host: {Describe(item)} zone {zone} is already {PaintPalette.NameOf(paint)}."); return; }
        var plot = PlotOf(item);
        if (!ServerCanPaint(rpcParams, plot, paint, item.transform.position, out var palette, storeBuffer)) return;
        var def = ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(item.ItemId, out var d) ? d : null;
        if (!ColorRules.Allows(def, paint, Team, out string why)) { DLog($"Host: not allowed: {why}."); return; }
        if (!ServerPay(palette.ItemFee)) { DLog($"Host: can't pay ${palette.ItemFee}."); return; }
        item.ServerSetZonePaint(zone, paint);
        DLog($"Host: painted {Describe(item)} zone {zone} {PaintPalette.NameOf(paint)}.");
    }

    [Rpc(SendTo.Server)]
    private void RequestPaintWallRpc(int plotId, int wallId, byte paint, RpcParams rpcParams = default)
    {
        if (MallManager.Instance == null || !MallManager.Instance.TryGetPlot(plotId, out var plot)) return;
        if (!plot.TryGetBuiltWallById(wallId, out var wall)) { DLog($"Host: wall {wallId} not found."); return; }
        if (wall.paint == paint) { DLog($"Host: wall {wallId} is already {PaintPalette.NameOf(paint)}."); return; }
        if (!ServerCanPaint(rpcParams, plot, paint, (wall.a + wall.b) * 0.5f, out var palette, wall.Length * 0.5f)) return;
        if (!ColorRules.AllowsPlain(paint, Team, out string why)) { DLog($"Host: not allowed: {why}."); return; }
        if (!ServerPay(palette.WallFee(wall.Length))) { DLog("Host: can't pay."); return; }
        plot.ServerPaintWall(wallId, paint);
        DLog($"Host: painted wall {wallId} {PaintPalette.NameOf(paint)}.");
    }

    [Rpc(SendTo.Server)]
    private void RequestPaintFrontRpc(int plotId, int moduleId, int zone, byte paint, RpcParams rpcParams = default)
    {
        if (MallManager.Instance == null || !MallManager.Instance.TryGetPlot(plotId, out var plot)) { DLog($"Host: store {plotId} not found."); return; }
        if (plot.GetFrontPaint(moduleId, zone) == paint) { DLog($"Host: storefront type {moduleId} part {zone} is already {PaintPalette.NameOf(paint)}."); return; }
        if (!ServerCanPaint(rpcParams, plot, paint, transform.position, out var palette)) return;
        if (!ColorRules.AllowsPlain(paint, Team, out string why)) { DLog($"Host: not allowed: {why}."); return; }
        if (!ServerPay(palette.FrontFee)) { DLog("Host: can't pay."); return; }
        plot.ServerPaintFront(moduleId, zone, paint);
        DLog($"Host: painted storefront type {moduleId} part {zone} {PaintPalette.NameOf(paint)}.");
    }

    [Rpc(SendTo.Server)]
    private void RequestPaintFloorRpc(int plotId, byte paint, RpcParams rpcParams = default)
    {
        if (MallManager.Instance == null || !MallManager.Instance.TryGetPlot(plotId, out var plot)) { DLog($"Host: store {plotId} not found."); return; }
        if (plot.FloorPaint == paint) { DLog($"Host: the floor is already {PaintPalette.NameOf(paint)}."); return; }
        if (!plot.HasPaintableFloor) { DLog("Host: this store has no paintable floor."); return; }
        if (!ServerCanPaint(rpcParams, plot, paint, transform.position, out var palette)) return;
        if (!ColorRules.AllowsPlain(paint, Team, out string why)) { DLog($"Host: not allowed: {why}."); return; }
        if (!ServerPay(palette.FloorFee)) { DLog("Host: can't pay."); return; }
        plot.ServerPaintFloor(paint);
        DLog($"Host: painted the floor {PaintPalette.NameOf(paint)}.");
    }

    // The sender is this player, the store is theirs (and not being edited), the color is real, and
    // they're close enough to what they're painting.
    private bool ServerCanPaint(RpcParams rpcParams, StorePlot plot, byte paint, Vector3 at, out PaintPalette palette,
                                float extraReach = 0f)
    {
        palette = PaintPalette.Instance;
        if (rpcParams.Receive.SenderClientId != OwnerClientId) { DLog("Host: sent by someone else."); return false; }
        if (palette == null) { DLog("Host: no PaintPalette."); return false; }
        if (plot == null) { DLog("Host: not in a store."); return false; }
        if (!PaintPalette.IsValid(paint)) { DLog($"Host: color {paint} isn't in the palette."); return false; }
        if (!plot.IsAuthorized(OwnerClientId)) { DLog($"Host: '{plot.DisplayName}' isn't theirs."); return false; }
        if (plot.IsRenovating) { DLog($"Host: '{plot.DisplayName}' is being renovated."); return false; }
        float d = Vector3.Distance(transform.position, at);
        if (d > range + 3f + extraReach) { DLog($"Host: too far ({d:0.0} m)."); return false; }
        return true;
    }

    private ulong Team => MallManager.GetTeamOf(OwnerClientId);

    private bool ServerPay(int fee)
    {
        var eco = EconomyManager.Instance;
        if (eco == null || !eco.IsSpawned || fee <= 0) return true;
        return eco.ServerTrySpend(MallManager.GetTeamOf(OwnerClientId), fee);
    }
}
