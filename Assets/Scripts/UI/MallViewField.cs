using TMPro;
using UnityEngine;

/// <summary>
/// For designing the Mall app yourself: put this on a text in the app's panel and pick what it shows.
/// When the Mall app has any of these under it, it fills them in (every second) instead of building its
/// own page. Format: how it's written, {0} is the value (e.g. "Help points: {0}"); empty = just the value.
/// Hide When Empty: hides the text's object when there's nothing to show (no event on, no vote open).
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public class MallViewField : MonoBehaviour
{
    public enum Field
    {
        HelpPoints, VoteWeight, TeamLevel, Money,
        SeasonName, SeasonDescription, SeasonTimeLeft,
        EventNames, EventsList, VoteTimeLeft,
        Shoppers, Players, StoresOpen, StoresTaken, StoresTotal, YourStores,
    }

    public Field field;
    public string format = "";
    public bool hideWhenEmpty;
    [Tooltip("Optional: hide this object (e.g. a whole card) instead of just the text when empty.")]
    public GameObject hideObject;

    private TMP_Text text;

    public void Show(string value)
    {
        if (text == null) text = GetComponent<TMP_Text>();
        bool empty = string.IsNullOrEmpty(value);
        if (hideWhenEmpty)
        {
            var target = hideObject != null ? hideObject : gameObject;
            if (target.activeSelf == empty) target.SetActive(!empty);
            if (empty) return;
        }
        if (text != null) text.text = string.IsNullOrEmpty(format) ? value : string.Format(format, value);
    }
}
