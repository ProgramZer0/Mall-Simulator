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

        // 1) Nothing in the way.
        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, Hits, rotation,
                                               blockingMask, QueryTriggerInteraction.Ignore);
        if (count > 0) return false;

        // 2) Something underneath to sit on (stops placing items in mid-air).
        Vector3 up = rotation * Vector3.up;
        Vector3 baseCenter = center - up * (halfExtents.y + Skin);
        return Physics.Raycast(baseCenter + up * 0.05f, -up, 0.05f + SupportProbe,
                               supportMask, QueryTriggerInteraction.Ignore);
    }

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
        Vector3 n = wallNormal;
        n.y = 0f;
        if (n.sqrMagnitude < 1e-6f) n = Vector3.forward;
        n.Normalize();

        Vector3 c = Vector3.zero, e = Vector3.one * 0.05f;
        if (def != null && def.worldNetworkPrefab != null && def.LocalBounds.size != Vector3.zero)
        {
            Bounds local = def.LocalBounds;
            Vector3 scale = def.worldNetworkPrefab.transform.localScale;
            c = Vector3.Scale(local.center, scale);
            e = Vector3.Scale(local.extents, Abs(scale));
        }

        // Thin along z: its blue arrow points out of the wall. Thin along x: its red arrow does.
        bool thinX = e.x < e.z;
        float yaw = (thinX ? -90f : 0f) + (flip ? 180f : 0f);
        rotation = Quaternion.LookRotation(n, Vector3.up) * Quaternion.Euler(0f, yaw, 0f);

        // The face touching the wall, in the item's own axes.
        Vector3 back = Quaternion.Inverse(rotation) * -n;
        Vector3 axis = thinX ? new Vector3(Mathf.Sign(back.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(back.z));
        Vector3 anchor = c + Vector3.Scale(axis, e);
        position = hitPoint - rotation * anchor;
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
        return true;
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