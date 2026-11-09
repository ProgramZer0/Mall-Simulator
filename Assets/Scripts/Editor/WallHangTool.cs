using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

/// <summary>
/// Scene view tool for setting up stores: hang wall items (pictures, signs, wall shelves...) on walls
/// exactly like a player would, without entering Play mode.
///
/// Use:
/// - Select an item with an ItemPickupNet whose Mounting is Wall or FloorOrWall.
/// - Turn the tool on: the "Hang On Wall" button in the Scene view's Tools overlay (component tools,
///   shown while the item is selected), or Tools > Mall Sim > Hang On Wall Tool.
/// - Point at a wall: an outline shows where it'll go. Click to hang it there (its back against the
///   wall, centered on the point). Hold Shift while clicking to turn it the other way round.
/// - Or Tools > Mall Sim > Hang Selected On Nearest Wall (Ctrl/Cmd+Shift+H) moves every selected wall
///   item onto the closest wall beside it, keeping its height.
///
/// Hung items get Starts On Wall ticked, so they stay up when the game starts instead of falling.
/// Walls are found on the "walls" layer (or "Wall"); every layer if the project has neither.
/// </summary>
[EditorTool("Hang On Wall", typeof(ItemPickupNet))]
public class WallHangTool : EditorTool
{
    private const float NearestWallRange = 3f;

    [MenuItem("Tools/Mall Sim/Hang On Wall Tool")]
    private static void Activate() => ToolManager.SetActiveTool<WallHangTool>();

    [MenuItem("Tools/Mall Sim/Hang On Wall Tool", true)]
    private static bool CanActivate() => Selection.activeGameObject != null &&
                                         Selection.activeGameObject.GetComponentInParent<ItemPickupNet>() != null;

    public override void OnToolGUI(EditorWindow window)
    {
        if (window is not SceneView) return;
        var item = target as ItemPickupNet;
        if (item == null) return;
        Event e = Event.current;

        // Clicks are ours while the tool is on (not the Scene view's select-on-click).
        int id = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
        if (e.type == EventType.MouseMove) window.Repaint();

        if (item.MountingType == ItemPickupNet.Mounting.Floor)
        {
            Label(e.mousePosition, "Floor-only item: set Mounting to Wall or FloorOrWall to hang it");
            return;
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!FindWall(ray, item.transform, out RaycastHit hit))
        {
            Label(e.mousePosition, "Point at a wall");
            return;
        }

        Pose(item, hit.point, hit.normal, e.shift, out Vector3 pos, out Quaternion rot);
        DrawOutline(item, pos, rot);
        Label(e.mousePosition, e.shift ? "Click: hang (turned round)" : "Click: hang   (Shift: turned round)");

        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Hang(item, pos, rot);
            e.Use();
        }
    }

    [MenuItem("Tools/Mall Sim/Hang Selected On Nearest Wall %#h")]
    private static void HangSelectedOnNearestWall()
    {
        int hung = 0;
        var seen = new HashSet<ItemPickupNet>();
        foreach (var go in Selection.gameObjects)
        {
            var item = go.GetComponentInParent<ItemPickupNet>();
            if (item == null || !seen.Add(item)) continue;
            if (item.MountingType == ItemPickupNet.Mounting.Floor)
            {
                Debug.LogWarning($"[WallHangTool] '{item.name}' is Floor-only; set Mounting to Wall or FloorOrWall.", item);
                continue;
            }
            if (!FindNearestWall(item, out RaycastHit hit))
            {
                Debug.LogWarning($"[WallHangTool] No wall within {NearestWallRange} m of '{item.name}'.", item);
                continue;
            }
            Pose(item, hit.point, hit.normal, false, out Vector3 pos, out Quaternion rot);
            Hang(item, pos, rot);
            hung++;
        }
        if (hung > 0) Debug.Log($"[WallHangTool] Hung {hung} item(s) on walls.");
    }

    /* ========================= HELPERS ========================= */

    private static void Pose(ItemPickupNet item, Vector3 point, Vector3 normal, bool flip,
                             out Vector3 pos, out Quaternion rot)
    {
        Bounds local = PlacementCheck.ComputeLocalBounds(item.gameObject);
        PlacementCheck.WallPose(local, item.transform.lossyScale, point, normal, flip, out pos, out rot);
    }

    private static void Hang(ItemPickupNet item, Vector3 pos, Quaternion rot)
    {
        Undo.RecordObject(item.transform, "Hang On Wall");
        item.transform.SetPositionAndRotation(pos, rot);
        item.SetStartsOnWallInEditor(true);
        PrefabUtility.RecordPrefabInstancePropertyModifications(item.transform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(item);
        Physics.SyncTransforms();
    }

    // The nearest wall under the mouse, skipping the item itself.
    private static bool FindWall(Ray ray, Transform self, out RaycastHit wall)
    {
        wall = default;
        Physics.SyncTransforms();
        var hits = Physics.RaycastAll(ray, 500f, PlacementCheck.DefaultWallMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider.transform.IsChildOf(self) || h.distance >= best) continue;
            best = h.distance;
            wall = h;
        }
        return best < float.MaxValue && PlacementCheck.IsWallNormal(wall.normal);
    }

    // The closest wall beside the item, looking all around it at the height of its middle.
    private static bool FindNearestWall(ItemPickupNet item, out RaycastHit wall)
    {
        wall = default;
        Physics.SyncTransforms();
        Bounds local = PlacementCheck.ComputeLocalBounds(item.gameObject);
        Vector3 center = item.transform.TransformPoint(local.center);

        float best = float.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * item.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
            dir.Normalize();
            foreach (var h in Physics.RaycastAll(center, dir, NearestWallRange, PlacementCheck.DefaultWallMask,
                                                 QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(item.transform) || !PlacementCheck.IsWallNormal(h.normal)) continue;
                if (h.distance < best) { best = h.distance; wall = h; }
            }
        }
        return best < float.MaxValue;
    }

    private static void DrawOutline(ItemPickupNet item, Vector3 pos, Quaternion rot)
    {
        Bounds local = PlacementCheck.ComputeLocalBounds(item.gameObject);
        Vector3 s = item.transform.lossyScale;
        using (new Handles.DrawingScope(new Color(0.3f, 1f, 0.4f, 1f), Matrix4x4.TRS(pos, rot, s)))
            Handles.DrawWireCube(local.center, local.size);
    }

    private static void Label(Vector2 mouse, string text)
    {
        Handles.BeginGUI();
        var size = EditorStyles.helpBox.CalcSize(new GUIContent(text));
        GUI.Label(new Rect(mouse.x + 16f, mouse.y + 16f, size.x + 8f, size.y + 4f), text, EditorStyles.helpBox);
        Handles.EndGUI();
    }
}
