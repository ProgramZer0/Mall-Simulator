using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A spot in the hallway people stop at now and then:
/// - Browse: a vendor cart, a display, a fountain. They stand at one of its Points, facing the way the point
///   faces, and look it over (the Browse animation) for a few seconds. Nothing's bought.
/// - Seat: a bench or seating area. Each Point is a seat (placed where they sit, facing out); they sit
///   (the Sitting animation) for a while.
/// Each passer-by between stores has Chance of stopping (when one's within Range on their floor and a point's
/// free). Points: empty objects under it; none: the spot itself is the one point.
/// </summary>
public class MallSpot : MonoBehaviour
{
    public enum Kind { Browse, Seat }

    public static readonly List<MallSpot> All = new();

    [SerializeField] private Kind kind = Kind.Browse;
    [Tooltip("Where people stand (Browse) or sit (Seat), facing the way each faces. Empty: this object.")]
    [SerializeField] private Transform[] points = new Transform[0];
    [Tooltip("The chance someone passing (between stores) stops here. Keep it low for vendor carts.")]
    [SerializeField, Range(0f, 1f)] private float chance = 0.03f;
    [Tooltip("How long they stay (seconds, random between).")]
    [SerializeField] private Vector2 seconds = new(5f, 12f);
    [SerializeField] private bool shoppers = true;
    [SerializeField] private bool bystanders = true;
    [Tooltip("How near (meters) they have to be to think of stopping.")]
    [SerializeField, Min(1f)] private float range = 18f;

    private ushort[] taken;

    public Kind SpotKind => kind;
    public float Chance => chance;
    public float Range => range;
    public float RandomSeconds => Random.Range(Mathf.Min(seconds.x, seconds.y), Mathf.Max(seconds.x, seconds.y));
    public bool For(bool bystander) => bystander ? bystanders : shoppers;

    private void OnEnable()
    {
        All.Add(this);
        taken = new ushort[Mathf.Max(1, points.Length)];
    }

    private void OnDisable() => All.Remove(this);

    public Transform Point(int i) => points.Length == 0 ? transform : points[Mathf.Clamp(i, 0, points.Length - 1)] ?? transform;

    /// <summary>A free point for this customer (true), taken until Release.</summary>
    public bool TryTake(ushort id, out int index)
    {
        index = -1;
        int start = Random.Range(0, taken.Length);
        for (int k = 0; k < taken.Length; k++)
        {
            int i = (start + k) % taken.Length;
            if (taken[i] != 0) continue;
            taken[i] = id;
            index = i;
            return true;
        }
        return false;
    }

    public void Release(ushort id)
    {
        if (taken == null) return;
        for (int i = 0; i < taken.Length; i++) if (taken[i] == id) taken[i] = 0;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = kind == Kind.Seat ? new Color(0.4f, 0.8f, 1f) : new Color(1f, 0.8f, 0.3f);
        int n = Mathf.Max(1, points.Length);
        for (int i = 0; i < n; i++)
        {
            var p = Point(i);
            if (p == null) continue;
            Gizmos.DrawWireSphere(p.position + Vector3.up * 0.1f, 0.25f);
            Gizmos.DrawLine(p.position + Vector3.up * 0.1f, p.position + Vector3.up * 0.1f + p.forward * 0.5f);
        }
    }
}
