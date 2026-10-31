using UnityEngine;

/// <summary>
/// One storefront wall section (plain wall, window, doorway, ...). Add this to each wall module
/// prefab and give each prefab a different Module Id; every copy already placed in the scene
/// picks it up automatically.
///
/// Stores find the modules closest to them and treat those as their storefront. In edit mode the
/// owner can swap any section for another module type from the FrontWallCatalog.
/// </summary>
public class WallModule : MonoBehaviour
{
    [Tooltip("Unique per module type, 1-255. This is what gets synced, so don't change it once stores use it.")]
    [SerializeField] private int moduleId = 1;
    [SerializeField] private string displayName = "Wall";
    [Tooltip("Customers can walk through this one (doorways). A store always keeps at least one.")]
    [SerializeField] private bool isEntrance;
    [Tooltip("Cost to put this module in. Not charged yet; used once the economy exists.")]
    [SerializeField] private int price;

    public int ModuleId => moduleId;
    public string DisplayName => displayName;
    public bool IsEntrance => isEntrance;
    public int Price => price;

    /// <summary>Set on copies a store spawned to replace a section, so they're never counted as slots.</summary>
    [System.NonSerialized] public bool IsReplacement;

    /// <summary>
    /// World-space box around a module's meshes. Works on inactive objects, and doesn't care where
    /// the pivot is (these prefabs' pivots sit well away from the mesh).
    /// </summary>
    public static Bounds GeometryBounds(GameObject go)
    {
        bool any = false;
        Bounds result = default;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = mf.transform.localToWorldMatrix;
            Bounds b = mf.sharedMesh.bounds;
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = m.MultiplyPoint3x4(c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                else result.Encapsulate(p);
            }
        }
        return any ? result : new Bounds(go.transform.position, Vector3.zero);
    }
}
