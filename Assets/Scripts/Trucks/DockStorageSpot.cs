using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A player's quick-storage spot at a loading dock: a box set down in it is held for that player's team for
/// the usual delivery hold (EconomyManager → Reservation Seconds, 10 minutes); after that anyone may take
/// it. Anywhere else on the dock floor a box is anyone's straight away.
///
/// Setup: a BoxCollider over the spot (turned off at runtime), one spot per player you want to allow. Spots
/// go to players in the order they joined (the first player gets the first spot by name, and so on), the
/// same on every machine. Optional Label: shows whose it is.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class DockStorageSpot : MonoBehaviour
{
    public static readonly List<DockStorageSpot> All = new();

    [SerializeField] private TMP_Text label;
    [SerializeField] private string labelFormat = "{0}'s spot";

    private BoxCollider area;
    private float nextLabel;
    private static readonly List<ulong> players = new();

    private void Awake()
    {
        area = GetComponent<BoxCollider>();
        area.enabled = false;
    }

    private void OnEnable()
    {
        All.Add(this);
        All.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
    }

    private void OnDisable() => All.Remove(this);

    public bool Contains(Vector3 world)
    {
        if (area == null) area = GetComponent<BoxCollider>();
        Vector3 p = transform.InverseTransformPoint(world) - area.center;
        Vector3 h = area.size * 0.5f + new Vector3(0f, 1f, 0f); // and what's stacked on it
        return Mathf.Abs(p.x) <= h.x && Mathf.Abs(p.y) <= h.y && Mathf.Abs(p.z) <= h.z;
    }

    /// <summary>Whose spot this is (ulong.MaxValue: nobody's, more spots than players).</summary>
    public ulong Owner
    {
        get
        {
            Players(players);
            int i = All.IndexOf(this);
            return i >= 0 && i < players.Count ? players[i] : ulong.MaxValue;
        }
    }

    /// <summary>The spot this point is in, if any.</summary>
    public static DockStorageSpot At(Vector3 world)
    {
        foreach (var s in All) if (s != null && s.Contains(world)) return s;
        return null;
    }

    // The players by when they joined (client ids go up as they join).
    private static void Players(List<ulong> into)
    {
        into.Clear();
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return;
        if (nm.IsServer) into.AddRange(nm.ConnectedClientsIds);
        else if (nm.SpawnManager != null)
            foreach (var o in nm.SpawnManager.SpawnedObjectsList) if (o.IsPlayerObject) into.Add(o.OwnerClientId);
        into.Sort();
    }

    private void Update()
    {
        if (label == null || Time.unscaledTime < nextLabel) return;
        nextLabel = Time.unscaledTime + 1f;
        ulong owner = Owner;
        string name = owner == ulong.MaxValue ? "" : PlayerNameTag.NameOf(owner);
        label.text = owner == ulong.MaxValue ? "" : string.Format(labelFormat, string.IsNullOrEmpty(name) ? $"Player {owner}" : name);
    }

    private void OnDrawGizmos()
    {
        var b = GetComponent<BoxCollider>();
        if (b == null) return;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
