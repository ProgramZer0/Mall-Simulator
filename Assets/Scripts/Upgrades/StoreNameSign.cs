using TMPro;
using UnityEngine;

/// <summary>
/// A text that shows a store's name (its default, or what its owners renamed it to), e.g. a sign over
/// the storefront. Put it on a TMP text (world-space or 3D TextMeshPro); Store: the store, or empty to
/// use the store it stands in.
/// </summary>
public class StoreNameSign : MonoBehaviour
{
    [SerializeField] private StorePlot store;
    [SerializeField] private TMP_Text text;
    [Tooltip("{0} = the store's name.")]
    [SerializeField] private string format = "{0}";

    private void Awake()
    {
        if (text == null) text = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        if (store == null && MallManager.Instance != null) store = MallManager.Instance.GetPlotAt(transform.position);
        if (store != null) store.Changed += OnChanged;
        Show();
    }

    private void OnDisable()
    {
        if (store != null) store.Changed -= OnChanged;
    }

    // The store may not be found yet when this wakes up (MallManager not ready).
    private void Start()
    {
        if (store != null) return;
        OnEnable();
    }

    private void OnChanged(StorePlot plot) => Show();

    private void Show()
    {
        if (text != null && store != null) text.text = string.Format(format, store.DisplayName);
    }
}
