using UnityEngine;

/// <summary>
/// One storefront wall section: a type of front wall. Add this to each front wall prefab and give
/// each prefab a different Module Id; every copy already placed in the scene picks it up
/// automatically. The one placed in the scene is the store's starting front; in edit mode the owner
/// can click the front and swap it for another type from the FrontWallCatalog.
///
/// A module can be any model. Where players may put windows, doors and wall items, give it one or
/// more flat panels: a child box with a CuttableWall (see CuttableWall). Everything else is
/// decoration: its colliders already keep things out, and a NoPlaceArea box keeps any other spot
/// clear (a bumpy part without a collider, the space in front of a ledge...). A plain front wall can
/// be a single object with WallModule + CuttableWall.
///
/// Swapped-in modules are lined up on the scene one's middle and, with Fit To Section on, stretched
/// to its length and height (thickness kept), so one prefab fits fronts of different sizes.
/// </summary>
public class WallModule : MonoBehaviour
{
    [Tooltip("Unique per module type, 1-255. This is what gets synced, so don't change it once stores use it.")]
    [SerializeField] private int moduleId = 1;
    [SerializeField] private string displayName = "Wall";
    [Tooltip("Customers can walk through this one (doorways). A store always keeps at least one.")]
    [SerializeField] private bool isEntrance;
    [Tooltip("Cost to put this module in (charged when swapped in during edit mode).")]
    [SerializeField] private int price;
    [Tooltip("Doorways: the door object inside this module. The store shows it while closed, for " +
             "sale or being renovated, and hides it while open.")]
    [SerializeField] private GameObject door;
    [Tooltip("When swapped in, stretch it to the length and height of the section it replaces (its " +
             "thickness stays). Off: it keeps its own size and is only centered on the section.")]
    [SerializeField] private bool fitToSection = true;

    public int ModuleId => moduleId;
    public string DisplayName => displayName;
    public bool IsEntrance => isEntrance;
    public int Price => price;
    public GameObject Door => door;
    public bool FitToSection => fitToSection;

    /// <summary>Set on copies a store spawned to replace a section, so they're never counted as slots.</summary>
    [System.NonSerialized] public bool IsReplacement;

    /// <summary>
    /// Box around a module's meshes in Frame's local space (scale included), e.g. the section's own
    /// transform, so sizes along its length, height and thickness can be compared.
    /// </summary>
    public static Bounds GeometryBoundsIn(GameObject go, Transform frame)
    {
        bool any = false;
        Bounds result = default;
        Matrix4x4 toFrame = Matrix4x4.TRS(frame.position, frame.rotation, Vector3.one).inverse;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = toFrame * mf.transform.localToWorldMatrix;
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
        return any ? result : new Bounds(Vector3.zero, Vector3.zero);
    }

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
