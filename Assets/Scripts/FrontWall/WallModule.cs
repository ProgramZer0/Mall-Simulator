using UnityEngine;

/// <summary>
/// One storefront wall section: a type of front wall. Add this to each front wall prefab and give
/// each prefab a different Module Id; every copy already placed in the scene picks it up
/// automatically. The one placed in the scene is the store's starting front. Other types are bought in
/// the shop as FrontWallKit items and put up in edit mode (click the kit's box, then a section).
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
    [Tooltip("Not used any more: front wall types are bought in the shop as a FrontWallKit item (its catalog price).")]
    [SerializeField] private int price;
    [Tooltip("Doorways: the door object inside this module. The store shows it while closed, for " +
             "sale or being renovated, and hides it while open.")]
    [SerializeField] private GameObject door;
    [Tooltip("Repeat this module side by side across the section it replaces, so the model never stretches " +
             "(a few percent at most, to fill the length exactly). Model one tile at full wall height, any rotation " +
             "(a Blender -90 on X is fine): its wider side is laid along the wall and its thin side faces out. Put " +
             "the flat part on a child cube with a CuttableWall: the tiles' panels are joined into one long panel, " +
             "so windows and doors can go anywhere. Off: Fit To Section.")]
    [SerializeField] private bool tile = true;
    [Tooltip("(Tile.) Turn the tiles round: use it if they come out facing into the store.")]
    [SerializeField] private bool flipTile;
    [Tooltip("(When Tile is off.) Stretch it to the length and height of the section it replaces (its " +
             "thickness stays). Off: it keeps its own size and is only centered on the section.")]
    [SerializeField] private bool fitToSection = true;

    public int ModuleId => moduleId;
    public string DisplayName => displayName;
    public bool IsEntrance => isEntrance;
    public int Price => price;
    public GameObject Door => door;
    public bool FitToSection => fitToSection;
    public bool Tile => tile;
    public bool FlipTile => flipTile;

    /// <summary>
    /// Make a store's plain storefront wall (a CuttableWall in its Storefront Walls) a section front wall
    /// kits can replace, without setup: it becomes type 0, "Starting wall".
    /// </summary>
    public static WallModule MakeStartingWall(GameObject wall)
    {
        var m = wall.AddComponent<WallModule>();
        m.moduleId = 0;
        m.displayName = "Starting wall";
        return m;
    }

    /// <summary>Set on copies a store spawned to replace a section, so they're never counted as slots.</summary>
    [System.NonSerialized] public bool IsReplacement;

    /// <summary>
    /// Box around a module's meshes in Frame's local space (scale included), e.g. the section's own
    /// transform, so sizes along its length, height and thickness can be compared.
    /// </summary>
    public static Bounds GeometryBoundsIn(GameObject go, Transform frame) =>
        GeometryBoundsIn(go, frame.position, frame.rotation);

    /// <summary>Same, in a frame given by a position and rotation.</summary>
    public static Bounds GeometryBoundsIn(GameObject go, Vector3 framePosition, Quaternion frameRotation)
    {
        bool any = false;
        Bounds result = default;
        Matrix4x4 toFrame = Matrix4x4.TRS(framePosition, frameRotation, Vector3.one).inverse;
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
