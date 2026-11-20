using UnityEngine;

/// <summary>
/// The store's open/closed button: a button on a wall inside the store. It glows green while the store is
/// open and red while it's closed. Its team presses F to open or close the store: closed, customers don't
/// come in and the ones inside head out (StorePlot.IsOpen); the store's Doors close too.
///
/// Setup: a scene object with a collider (on a layer the player's interact ray hits) inside or by the
/// store. Store: which one (empty: the store it's in, or whose child it is).
/// To move it around in edit mode, make it an item: a prefab with NetworkObject + ItemPickupNet (Edit Mode
/// Only) + this, in the catalog. Leave Store empty: it goes by the store it's in, wherever it's moved.
/// Lamp: the renderer that glows
/// (empty: the first renderer on it). Optional Lights tinted the same. No NetworkObject needed: the
/// store's open state is already synced.
/// </summary>
public class StoreOpenButton : MonoBehaviour
{
    [SerializeField] private StorePlot store;
    [SerializeField] private Renderer lamp;
    [SerializeField] private Light[] lights = new Light[0];
    [SerializeField] private Color openColor = new(0.2f, 1f, 0.3f, 1f);
    [SerializeField] private Color closedColor = new(1f, 0.15f, 0.1f, 1f);
    [Tooltip("Shown while the store's being edited or has no owner.")]
    [SerializeField] private Color offColor = new(0.3f, 0.3f, 0.3f, 1f);
    [Tooltip("How bright the glow is (emission strength, for materials with emission).")]
    [SerializeField, Min(0f)] private float glow = 2f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    private MaterialPropertyBlock block;
    private int shown = -1; // 0 off, 1 open, 2 closed

    private StorePlot found;      // (Store empty) the store it was found in...
    private Vector3 foundAt;      // ...from here (moved: looked up again)
    private bool foundOnce;

    public StorePlot Store
    {
        get
        {
            if (store != null) return store;
            if ((foundOnce && found != null && (transform.position - foundAt).sqrMagnitude < 0.04f)) return found;
            foundOnce = true;
            foundAt = transform.position;
            found = GetComponentInParent<StorePlot>();
            if (found == null && MallManager.Instance != null) found = MallManager.Instance.GetPlotAt(transform.position);
            return found;
        }
    }

    private void Reset()
    {
        lamp = GetComponentInChildren<Renderer>();
        store = GetComponentInParent<StorePlot>();
    }

    private void Update()
    {
        var s = Store;
        int now = s == null || !s.HasOwner || s.IsRenovating ? 0 : s.IsOpen ? 1 : 2;
        if (now == shown) return;
        shown = now;
        var c = now == 1 ? openColor : now == 2 ? closedColor : offColor;
        if (lamp == null) lamp = GetComponentInChildren<Renderer>();
        if (lamp != null)
        {
            block ??= new MaterialPropertyBlock();
            lamp.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
            block.SetColor(EmissionId, now == 0 ? Color.black : c * glow);
            lamp.SetPropertyBlock(block);
        }
        foreach (var l in lights)
            if (l != null) { l.color = c; l.enabled = now != 0; }
    }

    /// <summary>What F would do here (for the interact prompt), or null.</summary>
    public string Prompt(ulong clientId)
    {
        var s = Store;
        if (s == null) return null;
        if (!s.HasOwner) return $"{s.DisplayName}: for sale";
        if (s.IsRenovating) return $"{s.DisplayName}: being renovated";
        if (!s.IsAuthorized(clientId)) return $"{s.DisplayName} is {(s.IsOpen ? "open" : "closed")}";
        return s.IsOpen ? "F: Close the store" : "F: Open the store";
    }

    public void Press(ulong clientId)
    {
        var s = Store;
        if (s == null || !s.HasOwner || s.IsRenovating || !s.IsAuthorized(clientId) || MallManager.Instance == null) return;
        MallManager.Instance.RequestSetStoreClosedServerRpc(s.PlotId, s.IsOpen);
    }
}
