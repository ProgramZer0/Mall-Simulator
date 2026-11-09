using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shared fit test for placing an item. Used by the client (ghost color, click to place)
/// and by the server (final authority before spawning), so both agree on what fits.
/// </summary>
public static class PlacementCheck
{
    // Shrinks the test box so an item resting on a floor/shelf or flush against a wall
    // isn't counted as overlapping the surface it's touching.
    private const float Skin = 0.02f;

    // How far below the item's base we look for something to stand on.
    private const float SupportProbe = 0.15f;

    private static readonly Collider[] Hits = new Collider[16];

    /// <param name="def">Item being placed.</param>
    /// <param name="position">Where the item's root/pivot will be.</param>
    /// <param name="rotation">Rotation of the item's root.</param>
    /// <param name="blockingMask">Layers the item may not intersect (shelves, walls, other items, players).</param>
    /// <param name="supportMask">Layers the item may rest on (floor, shelves, counters).</param>
    public static bool Fits(ItemDef def, Vector3 position, Quaternion rotation,
                            LayerMask blockingMask, LayerMask supportMask)
    {
        if (def == null || def.worldNetworkPrefab == null) return false;
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 halfExtents))
            return true; // nothing measurable; don't block

        // 1) Nothing in the way, and not in a No Place Area.
        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        if (count > 0 || NoPlaceArea.Find(center, halfExtents, rotation) != null) return false;

        // 2) Something underneath to sit on (stops placing items in mid-air).
        Vector3 up = rotation * Vector3.up;
        Vector3 baseCenter = center - up * (halfExtents.y + Skin);
        return Physics.Raycast(baseCenter + up * 0.05f, -up, 0.05f + SupportProbe,
                               supportMask, QueryTriggerInteraction.Ignore);
    }

    /* ========================= BOXES ON CARTS ========================= */

    /// <summary>
    /// What an item turns into when it's set down on a cart: its shipment box (or the catalog's
    /// default box), so it takes less room. Null if it has no box (it goes on as itself).
    /// </summary>
    /// <summary>
    /// Wall pieces (windows, doors: WallOpening) stay in their shipment box when set down in normal play;
    /// they come out in store edit mode, where they're dragged onto a wall. Needs a box to go in.
    /// </summary>
    public static bool StaysBoxed(ItemDef def) => WallOpening.IsOpening(def) && CartBoxOf(def) != null;

    public static NetworkObject CartBoxOf(ItemDef def)
    {
        if (def == null) return null;
        var box = def.GetShipmentPrefab();
        return box != null && box != def.worldNetworkPrefab ? box : null;
    }

    /// <summary>Distance from a prefab's pivot up to its lowest point (like PivotHeightAboveBase, for any prefab).</summary>
    public static float PivotHeightAboveBase(GameObject prefab)
    {
        var box = ShipmentPacker.GetBox(prefab);
        return box.half.y - box.pivotOffset.y;
    }

    /// <summary>Fit test for any prefab (e.g. a shipment box): nothing in the way and something under it.</summary>
    public static bool FitsPrefab(GameObject prefab, Vector3 position, Quaternion rotation,
                                  LayerMask blockingMask, LayerMask supportMask)
    {
        if (prefab == null) return false;
        var box = ShipmentPacker.GetBox(prefab);
        Vector3 half = new(Mathf.Max(0.001f, box.half.x - Skin), Mathf.Max(0.001f, box.half.y - Skin),
                           Mathf.Max(0.001f, box.half.z - Skin));
        Vector3 center = position + rotation * box.pivotOffset;

        if (Physics.OverlapBoxNonAlloc(center, half, Hits, rotation, blockingMask, QueryTriggerInteraction.Ignore) > 0)
            return false;

        Vector3 up = rotation * Vector3.up;
        Vector3 baseCenter = center - up * (half.y + Skin);
        return Physics.Raycast(baseCenter + up * 0.05f, -up, 0.05f + SupportProbe, supportMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>The middle of a prefab's model at a pose.</summary>
    public static Vector3 PrefabCenter(GameObject prefab, Vector3 position, Quaternion rotation) =>
        prefab == null ? position : position + rotation * ShipmentPacker.GetBox(prefab).pivotOffset;

    /* ========================= WALL ITEMS ========================= */

    /// <summary>A surface counts as a wall if it's within this much of vertical (normal.y between -x and x).</summary>
    public const float WallNormalMaxY = 0.3f;

    public static bool IsWallNormal(Vector3 normal) => Mathf.Abs(normal.y) < WallNormalMaxY;

    /// <summary>The "walls" layer if the project has one, otherwise every layer.</summary>
    public static int DefaultWallMask
    {
        get
        {
            int wall = LayerMask.NameToLayer("walls");
            if (wall < 0) wall = LayerMask.NameToLayer("Wall");
            return wall >= 0 ? 1 << wall : Physics.DefaultRaycastLayers;
        }
    }

    /// <summary>Is this collider on one of the wall layers? (An empty mask counts every layer.)</summary>
    public static bool IsWallLayer(Collider c, int wallMask) =>
        c != null && (wallMask == 0 || (wallMask & (1 << c.gameObject.layer)) != 0);

    /// <summary>How an item may be placed (from its ItemPickupNet; Floor if it has none).</summary>
    public static ItemPickupNet.Mounting GetMounting(ItemDef def)
    {
        var pickup = def != null && def.worldNetworkPrefab != null ? def.worldNetworkPrefab.GetComponent<ItemPickupNet>() : null;
        return pickup != null ? pickup.MountingType : ItemPickupNet.Mounting.Floor;
    }

    /// <summary>
    /// Pose for hanging an item on a wall. It stays upright (its bottom stays its bottom, as built),
    /// and is turned so its THINNEST side (x or z, seen from above) goes against the wall, whichever
    /// way the model was built: a painting thin along x hangs flat just like one thin along z. Its
    /// middle goes on the hit point. flip turns it around to put its other face outward.
    /// </summary>
    public static void WallPose(ItemDef def, Vector3 hitPoint, Vector3 wallNormal, bool flip,
                                out Vector3 position, out Quaternion rotation)
    {
        if (def != null && def.worldNetworkPrefab != null && def.LocalBounds.size != Vector3.zero)
            WallPose(def.LocalBounds, def.worldNetworkPrefab.transform.localScale, ModelFix(def), hitPoint, wallNormal,
                     flip, out position, out rotation);
        else
            WallPose(new Bounds(Vector3.zero, Vector3.one * 0.1f), Vector3.one, Quaternion.identity, hitPoint, wallNormal,
                     flip, out position, out rotation);
    }

    /// <summary>
    /// Same as above for any object: Local is its box in its own space before scale (see
    /// ComputeLocalBounds), Scale its scale, Fix how its model stands upright (ModelFix). Used to hang
    /// items placed in the scene.
    /// </summary>
    public static void WallPose(Bounds local, Vector3 scale, Quaternion fix, Vector3 hitPoint, Vector3 wallNormal, bool flip,
                                out Vector3 position, out Quaternion rotation)
    {
        Vector3 n = wallNormal;
        n.y = 0f;
        if (n.sqrMagnitude < 1e-6f) n = Vector3.forward;
        n.Normalize();

        // Worked out for the model standing upright (its Fix applied); the Fix goes back on at the end.
        TurnedBox(local, scale, fix, out Vector3 c, out Vector3 e);

        // Thin along z: its blue arrow points out of the wall. Thin along x: its red arrow does.
        bool thinX = e.x < e.z;
        float yaw = (thinX ? -90f : 0f) + (flip ? 180f : 0f);
        rotation = Quaternion.LookRotation(n, Vector3.up) * Quaternion.Euler(0f, yaw, 0f);

        // The face touching the wall, in the item's own axes.
        Vector3 back = Quaternion.Inverse(rotation) * -n;
        Vector3 axis = thinX ? new Vector3(Mathf.Sign(back.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(back.z));
        Vector3 anchor = c + Vector3.Scale(axis, e);
        position = hitPoint - rotation * anchor;
        rotation *= fix;
    }

    /// <summary>
    /// Fit test for an item hung on a wall: nothing in the way, and a wall right behind it
    /// (instead of a floor underneath).
    /// </summary>
    public static bool FitsOnWall(ItemDef def, Vector3 position, Quaternion rotation,
                                  LayerMask blockingMask, LayerMask wallMask)
    {
        if (def == null || def.worldNetworkPrefab == null) return false;
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 halfExtents)) return true;

        if (Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation, blockingMask, QueryTriggerInteraction.Ignore) > 0)
            return false;
        // Hanging items touch the wall, so the area is checked a little deeper than the item.
        if (NoPlaceArea.Find(center, halfExtents + Vector3.one * (Skin + HangTouch), rotation) != null) return false;

        return HasWallBehind(def, position, rotation, wallMask);
    }

    /// <summary>Is there a wall right against either face of the item's thinnest side (x or z)?</summary>
    public static bool HasWallBehind(ItemDef def, Vector3 position, Quaternion rotation, int wallMask)
    {
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 halfExtents)) return false;
        bool thinX = halfExtents.x < halfExtents.z;
        Vector3 axis = rotation * (thinX ? Vector3.right : Vector3.forward);
        float extent = thinX ? halfExtents.x : halfExtents.z;

        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 dir = axis * side;
            Vector3 face = center + dir * (extent + Skin);
            if (Physics.Raycast(face - dir * 0.05f, dir, 0.05f + SupportProbe, wallMask, QueryTriggerInteraction.Ignore))
                return true;
        }
        return false;
    }

    private static readonly Collider[] EditHits = new Collider[32];
    private static readonly RaycastHit[] EditCastHits = new RaycastHit[32];

    /// <summary>
    /// Edit mode: true if the item at this pose overlaps nothing on the mask except itself.
    /// No support check, since items may float while a store is being edited.
    /// </summary>
    public static bool IsClear(ItemDef def, Vector3 position, Quaternion rotation,
                               int blockingMask, Transform ignoreRoot)
    {
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 half)) return true;

        int count = Physics.OverlapBoxNonAlloc(center, half, EditHits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (!IsPartOf(EditHits[i], ignoreRoot)) return false;
        return NoPlaceArea.Find(center, half + Vector3.one * (Skin + HangTouch), rotation) == null;
    }

    /// <summary>
    /// Saving an edit session: the first thing (if any) in the way of the item at this pose,
    /// ignoring the editor's local edit-mode copies. On the host those copies are still in the
    /// scene while the real items respawn, so without this every item "hits" its own copy and
    /// goes back to where it was before editing.
    /// </summary>
    public static Collider FindBlockerIgnoringEditCopies(ItemDef def, Vector3 position, Quaternion rotation, int blockingMask)
    {
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 half)) return null;

        int count = Physics.OverlapBoxNonAlloc(center, half, EditHits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var c = EditHits[i];
            if (c.GetComponentInParent<EditLocalItem>() != null) continue;      // an edit-mode copy
            if (c.GetComponentInParent<StockVisualFollower>() != null) continue; // stock models on shelves
            return c;
        }
        return NoPlaceArea.Find(center, half + Vector3.one * (Skin + HangTouch), rotation);
    }

    /// <summary>
    /// Windows and doors: the first thing (if any) in the way of one at this pose. Walls it can be cut
    /// into (CuttableWall), the floor (doors stand on it), the store's renovation and for-sale walls,
    /// and Ignore Root (the one being moved) don't count. Skip Edit Copies: while saving on the host,
    /// the editor's copies are still in the scene.
    /// </summary>
    public static Collider FindOpeningBlocker(ItemDef def, Vector3 position, Quaternion rotation, int blockingMask,
                                              Transform ignoreRoot, bool skipEditCopies, StorePlot plot)
    {
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 half)) return null;
        int ground = LayerMask.NameToLayer("Ground");

        int count = Physics.OverlapBoxNonAlloc(center, half, EditHits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var c = EditHits[i];
            if (IsPartOf(c, ignoreRoot)) continue;
            if (c.GetComponentInParent<CuttableWall>() != null) continue;
            if (ground >= 0 && c.gameObject.layer == ground) continue;
            if (plot != null && plot.IsStorefrontCover(c.transform)) continue;
            if (skipEditCopies && c.GetComponentInParent<EditLocalItem>() != null) continue;
            if (c.GetComponentInParent<StockVisualFollower>() != null) continue;
            return c;
        }

        // Something hung on the wall where the hole would go (on either face), or a No Place Area there:
        // the hole can't cut under it.
        if (WallOpening.TryGet(def, out _))
        {
            var shape = WallOpening.Measure(def, position, rotation);
            if (shape.forward.sqrMagnitude > 1e-6f)
            {
                Quaternion facing = Quaternion.LookRotation(shape.forward, shape.up);
                Vector3 h = new(Mathf.Max(0.001f, shape.size.x * 0.5f - Skin), Mathf.Max(0.001f, shape.size.y * 0.5f - Skin),
                                OpeningReach);
                var area = NoPlaceArea.Find(shape.center, h, facing);
                if (area != null) return area;
                float floorY = plot != null ? plot.FloorHeight : float.NegativeInfinity;
                count = Physics.OverlapBoxNonAlloc(shape.center, h, EditHits, facing, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    var c = EditHits[i];
                    if (IsPartOf(c, ignoreRoot)) continue;
                    if (skipEditCopies && c.GetComponentInParent<EditLocalItem>() != null) continue;
                    if (HungItemOf(c, floorY) != null) return c;
                }
            }
        }
        return null;
    }

    /// <summary>How far in front of and behind a hole hung items are looked for (meters, each way).</summary>
    private const float OpeningReach = 0.5f;
    /// <summary>How close to a wall an item's box must come to count as hanging on it.</summary>
    public const float HangTouch = 0.06f;

    /// <summary>
    /// The item this collider belongs to if it's hung on a wall: a wall item (Mounting Wall or
    /// FloorOrWall, not a window or door) whose bottom is off the floor. Works for networked items and
    /// edit mode's copies. Null otherwise. Floor Y: the store's floor (anything at or below it stands
    /// on the floor); negative infinity to skip that test.
    /// </summary>
    public static Transform HungItemOf(Collider c, float floorY)
    {
        if (c == null) return null;
        ItemDef def = null;
        Transform root = null;
        var copy = c.GetComponentInParent<EditLocalItem>();
        if (copy != null) { def = copy.Def; root = copy.transform; }
        else
        {
            var item = c.GetComponentInParent<ItemPickupNet>();
            if (item == null || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(item.itemId, out def)) return null;
            root = item.transform;
        }
        return IsHung(def, root.position, root.rotation, floorY) ? root : null;
    }

    /// <summary>Is an item at this pose a wall item hanging up (not a window or door, not on the floor)?</summary>
    public static bool IsHung(ItemDef def, Vector3 position, Quaternion rotation, float floorY)
    {
        if (def == null || GetMounting(def) == ItemPickupNet.Mounting.Floor || WallOpening.TryGet(def, out _)) return false;
        if (!GetTestBox(def, position, rotation, out Vector3 c, out Vector3 half)) return false;
        float bottom = c.y - (Mathf.Abs((rotation * new Vector3(half.x, 0f, 0f)).y) +
                              Mathf.Abs((rotation * new Vector3(0f, half.y, 0f)).y) +
                              Mathf.Abs((rotation * new Vector3(0f, 0f, half.z)).y));
        return bottom > floorY + 0.05f;
    }

    /// <summary>
    /// Is a hung item at this pose up against a wall that Is Wall says yes to? Its box, grown a little,
    /// touches it.
    /// </summary>
    public static bool IsHungAgainst(ItemDef def, Vector3 position, Quaternion rotation, float floorY,
                                     System.Predicate<Collider> isWall)
    {
        if (!IsHung(def, position, rotation, floorY)) return false;
        if (!GetTestBox(def, position, rotation, out Vector3 c, out Vector3 half)) return false;
        half += Vector3.one * (Skin + HangTouch);
        int count = Physics.OverlapBoxNonAlloc(c, half, EditHits, rotation, Physics.AllLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (isWall(EditHits[i])) return true;
        return false;
    }

    /// <summary>
    /// Edit mode: how far the item can slide from this pose along a direction before touching
    /// something (up to maxDistance). Things it already overlaps are ignored, so a stuck item
    /// can always be slid free.
    /// </summary>
    public static float CastDistance(ItemDef def, Vector3 position, Quaternion rotation,
                                     Vector3 direction, float maxDistance,
                                     int blockingMask, Transform ignoreRoot)
    {
        if (maxDistance <= 0f) return 0f;
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 half)) return maxDistance;

        int count = Physics.BoxCastNonAlloc(center, half, direction.normalized, EditCastHits, rotation,
                                            maxDistance, blockingMask, QueryTriggerInteraction.Ignore);
        float allowed = maxDistance;
        for (int i = 0; i < count; i++)
        {
            var hit = EditCastHits[i];
            if (hit.distance <= 0f || IsPartOf(hit.collider, ignoreRoot)) continue; // initial overlap / self
            allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - 0.001f));
        }
        return allowed;
    }

    private static bool IsPartOf(Collider c, Transform root) =>
        root != null && c != null && c.transform.IsChildOf(root);

    /// <summary>
    /// The world-space box the fit test uses (already shrunk by the skin).
    /// Returns false if the item has no measurable size.
    /// </summary>
    public static bool GetTestBox(ItemDef def, Vector3 position, Quaternion rotation,
                                  out Vector3 center, out Vector3 halfExtents)
    {
        center = position;
        halfExtents = Vector3.zero;
        if (def == null || def.worldNetworkPrefab == null) return false;

        Bounds local = def.LocalBounds;
        if (local.size == Vector3.zero) return false;

        Vector3 scale = def.worldNetworkPrefab.transform.localScale;
        halfExtents = Vector3.Scale(local.extents, Abs(scale));
        halfExtents = new Vector3(
            Mathf.Max(0.001f, halfExtents.x - Skin),
            Mathf.Max(0.001f, halfExtents.y - Skin),
            Mathf.Max(0.001f, halfExtents.z - Skin));

        center = position + rotation * Vector3.Scale(local.center, scale);
        return true;
    }

    /// <summary>
    /// How the model stands upright and faces front: its prefab root's own rotation (e.g. Blender's -90
    /// on X), then the Model Rotation on its ItemPickupNet (or WallOpening, for windows and doors).
    /// Placing keeps it, so turning an item only adds a turn around world up on top of this.
    /// </summary>
    public static Quaternion ModelFix(ItemDef def)
    {
        if (def == null || def.worldNetworkPrefab == null) return Quaternion.identity;
        var root = def.worldNetworkPrefab.transform;
        Vector3 extra = root.TryGetComponent<WallOpening>(out var o) ? o.modelRotation
                      : root.TryGetComponent<ItemPickupNet>(out var p) ? p.ModelRotation : Vector3.zero;
        return root.localRotation * Quaternion.Euler(extra);
    }

    /// <summary>The item's rotation for a turn around world up (Yaw), standing the way its prefab does.</summary>
    public static Quaternion Upright(ItemDef def, Quaternion yaw) => yaw * ModelFix(def);

    /// <summary>
    /// The turn around world up of an item's rotation, with its Model Fix taken out (so an upright item
    /// placed with Upright(def, yaw) gives yaw back).
    /// </summary>
    public static Quaternion YawOf(ItemDef def, Quaternion rotation)
    {
        Vector3 f = rotation * Quaternion.Inverse(ModelFix(def)) * Vector3.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
    }

    /// <summary>
    /// The item's box turned by Rotation, as a box lined up with the world: its middle (from the pivot)
    /// and half size.
    /// </summary>
    public static void TurnedBox(Bounds local, Vector3 scale, Quaternion rotation, out Vector3 center, out Vector3 extents)
    {
        center = rotation * Vector3.Scale(local.center, scale);
        Vector3 e = Vector3.Scale(local.extents, Abs(scale));
        Vector3 x = rotation * new Vector3(e.x, 0f, 0f), y = rotation * new Vector3(0f, e.y, 0f), z = rotation * new Vector3(0f, 0f, e.z);
        extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                              Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                              Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
    }

    /// <summary>
    /// Distance from the item's pivot up (world up) to its lowest point when turned by Rotation. Adding
    /// (up * this) to a surface point makes it sit ON the surface, whatever its pivot or Model Fix.
    /// </summary>
    public static float PivotHeightAboveBase(ItemDef def, Quaternion rotation)
    {
        if (def == null || def.worldNetworkPrefab == null) return 0f;
        Bounds local = def.LocalBounds;
        if (local.size == Vector3.zero) return 0f;
        TurnedBox(local, def.worldNetworkPrefab.transform.localScale, rotation, out Vector3 c, out Vector3 e);
        return -(c.y - e.y);
    }

    /// <summary>
    /// Distance from the item's pivot up to its lowest point. Adding (rotation * up * this)
    /// to a surface hit point makes the item sit ON the surface, wherever its pivot is.
    /// </summary>
    public static float PivotHeightAboveBase(ItemDef def)
    {
        if (def == null || def.worldNetworkPrefab == null) return 0f;
        Bounds local = def.LocalBounds;
        if (local.size == Vector3.zero) return 0f;
        return -local.min.y * def.worldNetworkPrefab.transform.localScale.y;
    }

    /// <summary>
    /// Box around everything in the prefab, in the prefab root's local space (before root scale).
    /// Uses colliders when present (the real physical shape), otherwise meshes.
    /// Works on prefab assets and inactive objects, unlike Collider.bounds/Renderer.bounds.
    /// </summary>
    public static Bounds ComputeLocalBounds(GameObject prefab)
    {
        Transform root = prefab.transform;
        // Root-local space (root scale not applied), so Fits() can apply the scale itself.
        Matrix4x4 toRoot = root.worldToLocalMatrix;

        bool any = false;
        Bounds result = default;

        void Add(Transform t, Bounds b)
        {
            Matrix4x4 m = toRoot * t.localToWorldMatrix;
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                else result.Encapsulate(p);
            }
        }

        foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (col.isTrigger) continue;
            switch (col)
            {
                case BoxCollider box:
                    Add(col.transform, new Bounds(box.center, box.size));
                    break;
                case SphereCollider s:
                    Add(col.transform, new Bounds(s.center, Vector3.one * s.radius * 2f));
                    break;
                case CapsuleCollider cap:
                    {
                        Vector3 size = Vector3.one * cap.radius * 2f;
                        size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2f);
                        Add(col.transform, new Bounds(cap.center, size));
                        break;
                    }
                case MeshCollider mc when mc.sharedMesh != null:
                    Add(col.transform, mc.sharedMesh.bounds);
                    break;
            }
        }

        if (any) return result;

        // No colliders: fall back to visible geometry.
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Add(mf.transform, mf.sharedMesh.bounds);
        foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            Add(smr.transform, smr.localBounds);

        return any ? result : default;
    }

    private static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}