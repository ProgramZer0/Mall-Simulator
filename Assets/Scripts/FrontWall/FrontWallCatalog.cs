using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The storefront modules players can choose from in edit mode. Put one in the scene (the
/// MallManager object is a good spot) and drag in the wall module prefabs.
/// </summary>
public class FrontWallCatalog : MonoBehaviour
{
    public static FrontWallCatalog Instance { get; private set; }

    [Tooltip("Module prefabs, each with a WallModule component and a unique Module Id.")]
    [SerializeField] private List<WallModule> modules = new();
    [Tooltip("Stop a store from removing its last doorway (customers need a way in).")]
    [SerializeField] private bool requireEntrance = true;

    private readonly Dictionary<int, WallModule> byId = new();

    public IReadOnlyList<WallModule> Modules => modules;
    public bool RequireEntrance => requireEntrance;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[FrontWallCatalog] More than one in the scene; using the first.", this);
            return;
        }
        Instance = this;

        foreach (var m in modules)
        {
            if (m == null) continue;
            if (m.ModuleId < 1 || m.ModuleId > 255)
                Debug.LogError($"[FrontWallCatalog] '{m.name}' has Module Id {m.ModuleId}; it must be 1-255.", m);
            else if (byId.ContainsKey(m.ModuleId))
                Debug.LogError($"[FrontWallCatalog] Module Id {m.ModuleId} is used by both " +
                               $"'{byId[m.ModuleId].name}' and '{m.name}'.", m);
            else
                byId[m.ModuleId] = m;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static bool TryGet(int id, out WallModule module)
    {
        module = null;
        return Instance != null && Instance.byId.TryGetValue(id, out module);
    }
}
