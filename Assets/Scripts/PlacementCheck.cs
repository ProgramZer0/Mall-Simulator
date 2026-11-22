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

        // 1) Nothing in the way, not in a No Place Area, and not in stock on a shelf (products have no
        //    colliders, so a thing set on a stocked shelf would sit inside them).
        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        if (count > 0 || NoPlaceArea.Find(center, halfExtents, rotation) != null) return false;
        if (OverlapsStock(center, halfExtents, rotation)) return false;

        // 2) Something underneath to sit on (stops placing items in mid-air).
        if (!HasSupport(center, halfExtents, rotation, supportMask)) return false;

        // 3) Not shutting the store's last clear doorway.
        return !BlocksLastDoorway(center, halfExtents, rotation, null);
    }

    /* ========================= STOCK ON SHELVES ========================= */

    /// <summary>
    /// Would a box here be inside a shelf spot or rail that has products in it? Shelf products have no
    /// colliders, so the physics check alone lets things be set right through them.
    /// </summary>
    public static bool OverlapsStock(Vector3 center, Vector3 half, Quaternion rotation)
    {
        // A hair smaller, so resting right on top of (or beside) stocked products still counts as clear.
        Vector3 h = half - Vector3.one * 0.01f;
        if (h.x <= 0f || h.y <= 0f || h.z <= 0f) return false;
        foreach (var sc in StockContainer.All)
        {
            if (sc == null || !sc.IsSpawned) continue;
            for (int z = 0; z < sc.ZoneCount; z++)
            {
                if (sc.GetZone(z).count <= 0) continue;
                if (!sc.TryGetZoneBox(z, out Vector3 zc, out Quaternion zr, out Vector3 size)) continue;
                if (BoxesOverlap(center, h, rotation, zc, size * 0.5f, zr)) return true;
            }
        }
        return false;
    }

    private static readonly Vector3[] SatAxes = new Vector3[15];

    // Two turned boxes overlap (separating axis test).
    private static bool BoxesOverlap(Vector3 ca, Vector3 ha, Quaternion ra, Vector3 cb, Vector3 hb, Quaternion rb)
    {
        Vector3 a0 = ra * Vector3.right, a1 = ra * Vector3.up, a2 = ra * Vector3.forward;
        Vector3 b0 = rb * Vector3.right, b1 = rb * Vector3.up, b2 = rb * Vector3.forward;
        if ((ca - cb).sqrMagnitude > (ha.magnitude + hb.magnitude) * (ha.magnitude + hb.magnitude)) return false;
        int n = 0;
        SatAxes[n++] = a0; SatAxes[n++] = a1; SatAxes[n++] = a2;
        SatAxes[n++] = b0; SatAxes[n++] = b1; SatAxes[n++] = b2;
        Vector3[] aa = { a0, a1, a2 }, bb = { b0, b1, b2 };
        foreach (var x in aa) foreach (var y in bb) SatAxes[n++] = Vector3.Cross(x, y);
        Vector3 d = cb - ca;
        for (int i = 0; i < n; i++)
        {
            Vector3 axis = SatAxes[i];
            if (axis.sqrMagnitude < 1e-8f) continue;
            float ra2 = Mathf.Abs(Vector3.Dot(a0, axis)) * ha.x + Mathf.Abs(Vector3.Dot(a1, axis)) * ha.y + Mathf.Abs(Vector3.Dot(a2, axis)) * ha.z;
            float rb2 = Mathf.Abs(Vector3.Dot(b0, axis)) * hb.x + Mathf.Abs(Vector3.Dot(b1, axis)) * hb.y + Mathf.Abs(Vector3.Dot(b2, axis)) * hb.z;
            if (Mathf.Abs(Vector3.Dot(d, axis)) > ra2 + rb2) return false;
        }
        return true;
    }

    /* ========================= DOORWAYS ========================= */

    private static readonly System.Collections.Generic.List<Bounds> DoorScratch = new();
    private static readonly Collider[] DoorHits = new Collider[64];

    /// <summary>
    /// Would a box here shut off the last doorway of its store that's still clear? Every store keeps one
    /// way in and out clear (MallManager > Doorways), so customers can't be trapped inside. Ignore: the
    /// item being moved (edit mode), so it doesn't count as already in the way.
    /// </summary>
    public static bool BlocksLastDoorway(Vector3 center, Vector3 half, Quaternion rotation, Transform ignore)
    {
        float depth = MallManager.DoorwayClearDepth;
        var mall = MallManager.Instance;
        if (depth <= 0f || mall == null) return false;
        var plot = mall.GetPlotAt(center);
        if (plot == null) return false;
        plot.GetDoorBounds(DoorScratch);
        if (DoorScratch.Count == 0) return false;

        Bounds mine = WorldBox(center, half, rotation);
        Vector3 middle = plot.GetWorldBounds().center;
        int clear = 0, stillClear = 0;
        foreach (var d in DoorScratch)
        {
            Bounds keep = KeepClear(d, middle, depth);
            if (Occupied(keep, ignore)) continue;
            clear++;
            if (!mine.Intersects(keep)) stillClear++;
        }
        return clear > 0 && stillClear == 0;
    }

    /// <summary>The floor just inside a doorway that's kept clear (world box, floor-level part only).</summary>
    public static Bounds KeepClear(Bounds door, Vector3 storeMiddle, float depth)
    {
        Vector3 inward = storeMiddle - door.center;
        bool alongX = Mathf.Abs(inward.x) > Mathf.Abs(inward.z);
        float sign = alongX ? Mathf.Sign(inward.x) : Mathf.Sign(inward.z);
        float height = MallManager.DoorwayClearHeight;
        // The doorway itself (its frame's depth) and Depth meters past it into the store.
        Vector3 c = door.center;
        c.y = door.min.y + height * 0.5f;
        Vector3 size;
        if (alongX)
        {
            c.x += sign * depth * 0.5f;
            size = new Vector3(door.size.x + depth, height, Mathf.Max(0.6f, door.size.z));
        }
        else
        {
            c.z += sign * depth * 0.5f;
            size = new Vector3(Mathf.Max(0.6f, door.size.x), height, door.size.z + depth);
        }
        return new Bounds(c, size);
    }

    // Anything placed in there already (an item, or an edit-mode copy), other than the one being moved?
    private static bool Occupied(Bounds keep, Transform ignore)
    {
        // A little in from each side, so things just touching the edge don't count.
        Vector3 half = Vector3.Max(keep.extents - new Vector3(0.05f, 0f, 0.05f), Vector3.one * 0.01f);
        int n = Physics.OverlapBoxNonAlloc(keep.center, half, DoorHits, Quaternion.identity,
                                           Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = DoorHits[i];
            if (c == null || !c.gameObject.activeInHierarchy || (ignore != null && c.transform.IsChildOf(ignore))) continue;
            var item = c.GetComponentInParent<ItemPickupNet>();
            if (item != null)
            {
                if (item.TryGetComponent<WallOpening>(out _)) continue; // the doorway itself (a door put in the wall)
                if (item.IsSpawned && !item.IsOnCart && !item.OnWall && !item.EditModeOnly) return true;
                continue;
            }
            var copy = c.GetComponentInParent<EditLocalItem>();
            if (copy != null && copy.Opening == null) return true;
        }
        return false;
    }

    // An (turned) box's world-axis bounds.
    private static Bounds WorldBox(Vector3 center, Vector3 half, Quaternion rotation)
    {
        Vector3 r = rotation * Vector3.right, u = rotation * Vector3.up, f = rotation * Vector3.forward;
        Vector3 e = new(
            Mathf.Abs(r.x) * half.x + Mathf.Abs(u.x) * half.y + Mathf.Abs(f.x) * half.z,
            Mathf.Abs(r.y) * half.x + Mathf.Abs(u.y) * half.y + Mathf.Abs(f.y) * half.z,
            Mathf.Abs(r.z) * half.x + Mathf.Abs(u.z) * half.y + Mathf.Abs(f.z) * half.z);
        return new Bounds(center, e * 2f);
    }

    // Something to stand on under the bottom of a (turned) box: straight down from the middle of its lowest
    // point in WORLD terms. (The box's own "up" isn't world up for models whose prefab root is turned,
    // e.g. Blender's -90 on X: looking along that went sideways and found nothing.)
    private static bool HasSupport(Vector3 center, Vector3 half, Quaternion rotation, LayerMask supportMask) =>
        Physics.Raycast(BaseCenter(center, half, rotation) + Vector3.up * 0.05f, Vector3.down, 0.05f + SupportProbe,
                        supportMask, QueryTriggerInteraction.Ignore);

    private static Vector3 BaseCenter(Vector3 center, Vector3 half, Quaternion rotation)
    {
        float worldHalfY = Mathf.Abs((rotation * Vector3.right).y) * half.x +
                           Mathf.Abs((rotation * Vector3.up).y) * half.y +
                           Mathf.Abs((rotation * Vector3.forward).y) * half.z;
        return center - Vector3.up * (worldHalfY + Skin);
    }

    /// <summary>Why Fits says no, in words (for logs): what's in the way, a No Place Area, or nothing under it.</summary>
    public static string WhyNotFits(ItemDef def, Vector3 position, Quaternion rotation,
                                    LayerMask blockingMask, LayerMask supportMask)
    {
        if (def == null || def.worldNetworkPrefab == null) return "no item or world prefab";
        if (!GetTestBox(def, position, rotation, out Vector3 center, out Vector3 halfExtents)) return "ok (nothing to measure)";
        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation, blockingMask, QueryTriggerInteraction.Ignore);
        if (count > 0)
        {
            var sb = new System.Text.StringBuilder("in the way: ");
            for (int i = 0; i < count; i++)
            {
                var c = Hits[i];
                if (i > 0) sb.Append(", ");
                sb.Append($"'{c.name}' ({c.GetType().Name}, layer {LayerMask.LayerToName(c.gameObject.layer)}" +
                          (c.transform.root != c.transform ? $", under '{c.transform.root.name}'" : "") + ")");
            }
            sb.Append($"; test box {halfExtents * 2f:F2} m at {center:F2}");
            return sb.ToString();
        }
        var area = NoPlaceArea.Find(center, halfExtents, rotation);
        if (area != null) return $"inside No Place Area '{area.name}'";
        Vector3 baseCenter = BaseCenter(center, halfExtents, rotation);
        if (!HasSupport(center, halfExtents, rotation, supportMask))
            return $"nothing to stand on under its base at {baseCenter:F2} (looked {SupportProbe + 0.05f:0.00} m down on the support layers; " +
                   $"test box {halfExtents * 2f:F2} m)";
        if (BlocksLastDoorway(center, halfExtents, rotation, null))
            return "it would block the store's last clear doorway (MallManager > Doorways)";
        return "ok";
    }

    /* ========================= BOXES ON CARTS ========================= */

    /// <summary>
    /// What an item turns into when it's set down on a cart: its shipment box (or the catalog's
    /// default box), so it takes less room. Null if it has no box (it goes on as itself).
    /// </summary>
    /// <summary>
    /// Wall pieces (windows, doors: WallOpening) and front wall kits stay in their shipment box when set
    /// down in normal play; they come out in store edit mode. Needs a box to go in.
    /// </summary>
    public static bool StaysBoxed(ItemDef def) =>
        (WallOpening.IsOpening(def) || FrontWallKit.IsKit(def)) && CartBoxOf(def) != null;

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

        return HasSupport(center, half, rotation, supportMask);
    }

    /// <summary>Same as FitsPrefab, and why not: the first thing in the way, or "nothing under it".</summary>
    public static bool FitsPrefab(GameObject prefab, Vector3 position, Quaternion rotation,
                                  LayerMask blockingMask, LayerMask supportMask, out string why)
    {
        why = null;
        if (prefab == null) { why = "no box"; return false; }
        var box = ShipmentPacker.GetBox(prefab);
        Vector3 half = new(Mathf.Max(0.001f, box.half.x - Skin), Mathf.Max(0.001f, box.half.y - Skin),
                           Mathf.Max(0.001f, box.half.z - Skin));
        Vector3 center = position + rotation * box.pivotOffset;
        int n = Physics.OverlapBoxNonAlloc(center, half, Hits, rotation, blockingMask, QueryTriggerInteraction.Ignore);
        if (n > 0)
        {
            var c = Hits[0];
            why = $"'{c.name}' is in the way (layer {LayerMask.LayerToName(c.gameObject.layer)})";
            return false;
        }
        if (!HasSupport(center, half, rotation, supportMask)) { why = "nothing under it to stand on"; return false; }
        return true;
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
        // Against the thinner of its two sideways sides (as it stands: a Blender model's own z is up, so
        // its own axes are told apart by which way they point, not by name). Both, if about the same.
        float thinnest = float.MaxValue;
        for (int i = 0; i < 3; i++)
            if (Mathf.Abs((rotation * Axis(i)).y) <= 0.5f) thinnest = Mathf.Min(thinnest, halfExtents[i]);
        if (thinnest == float.MaxValue) return false;
        for (int i = 0; i < 3; i++)
        {
            Vector3 axis = rotation * Axis(i);
            if (Mathf.Abs(axis.y) > 0.5f || halfExtents[i] > thinnest + 0.02f) continue; // up-and-down, or the wide side
            float extent = halfExtents[i];
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 dir = axis * side;
                Vector3 face = center + dir * (extent + Skin);
                if (Physics.Raycast(face - dir * 0.05f, dir, 0.05f + SupportProbe, wallMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
        }
        return false;
    }

    private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

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
        if (BlocksLastDoorway(center, half, rotation, ignoreRoot)) return false;
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
            if (item == null || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(item.ItemId, out def)) return null;
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