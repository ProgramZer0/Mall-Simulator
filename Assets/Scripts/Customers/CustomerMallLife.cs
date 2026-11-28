using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The mall feeling alive between stores:
/// - MallSpots: vendor carts and displays people stop and look over now and then; benches bystanders sit on.
/// - Groups stopping in the hallway to chat for a bit (all of them standing together, Talking).
/// - Now and then a family pushes a stroller (Stroller Prefab, held in front of the one in charge).
/// </summary>
public partial class CustomerManager
{
    [Header("Mall life")]
    [Tooltip("The chance a group (2 or more) stops in the hallway to chat, each time its leader picks where to go next.")]
    [SerializeField, Range(0f, 1f)] private float groupChatChance = 0.05f;
    [SerializeField] private Vector2 groupChatSeconds = new(10f, 25f);
    [Tooltip("A family with a child pushing a stroller: this rarely (0.04 = 1 in 25). Needs Stroller Prefab.")]
    [SerializeField, Range(0f, 1f)] private float strollerChance = 0.04f;
    [Tooltip("The stroller model (no colliders or scripts needed): pushed in front of the family's leader. Put a " +
             "child called \"PushAnchor\" on the customer look to place it exactly.")]
    [SerializeField] private GameObject strollerPrefab;

    private readonly HashSet<ushort> strollers = new(); // every machine: who has one (for views made later)

    // Between stores: maybe stop at a spot nearby (vendor cart, bench), or (a group's leader) stop to chat.
    private bool TryMallLife(Customer c, bool bystander)
    {
        if (c.child || c.thief || c.courier || c.follow != null) return false;
        if (c.group != null && c.group.members.Count >= 2 && Random.value < groupChatChance) { StartChat(c); return true; }
        if (MallSpot.All.Count == 0) return false;
        MallSpot best = null;
        float bestD = float.MaxValue;
        foreach (var s in MallSpot.All)
        {
            if (s == null || !s.For(bystander)) continue;
            Vector3 d = s.transform.position - c.pos;
            if (Mathf.Abs(d.y) > 2f) continue;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist > s.Range || dist >= bestD) continue;
            if (s.SpotKind == MallSpot.Kind.Seat && c.group != null) continue; // groups don't split up to sit
            best = s;
            bestD = dist;
        }
        if (best == null || Random.value >= best.Chance || !best.TryTake(c.id, out int index)) return false;
        var point = best.Point(index);
        if (!PathTo(c, point.position)) { best.Release(c.id); return false; }
        c.spot = best;
        c.spotIndex = index;
        c.mode = Mode.AtSpot;
        c.until = 0f;
        Log(c, best.SpotKind == MallSpot.Kind.Seat ? $"going to sit at '{best.name}'" : $"stopping to look at '{best.name}'");
        return true;
    }

    private void ThinkAtSpot(Customer c)
    {
        var s = c.spot;
        if (s == null) { LeaveSpot(c); return; }
        if (!c.arrived) return;
        var point = s.Point(c.spotIndex);
        if (c.until <= 0f)
        {
            // There: in place, facing the way the point faces.
            c.until = Time.time + s.RandomSeconds;
            if (s.SpotKind == MallSpot.Kind.Seat) c.pos = new Vector3(point.position.x, c.pos.y, point.position.z); // on the seat
            c.yaw = point.eulerAngles.y;
            c.dirty = true;
            if (s.SpotKind == MallSpot.Kind.Seat) Act(c, AnimCue.Sitting);
            else Act(c, AnimCue.Browse);
            c.nextBrowse = Time.time + Random.Range(2.5f, 4f);
            return;
        }
        if (s.SpotKind == MallSpot.Kind.Browse && Time.time >= c.nextBrowse)
        {
            c.nextBrowse = Time.time + Random.Range(2.5f, 4f);
            Act(c, AnimCue.Browse); // picking something else up to look at
        }
        if (Time.time >= c.until) LeaveSpot(c);
    }

    private void LeaveSpot(Customer c)
    {
        if (c.spot != null)
        {
            if (c.spot.SpotKind == MallSpot.Kind.Seat) Act(c, AnimCue.Sitting, false);
            c.spot.Release(c.id);
        }
        c.spot = null;
        c.mode = Mode.Wander;
        PickGoal(c);
    }

    // A group stops where it is and chats: the others gather round (they settle by the leader).
    private void StartChat(Customer c)
    {
        c.mode = Mode.Chatting;
        c.chatting = true;
        c.until = Time.time + Random.Range(Mathf.Min(groupChatSeconds.x, groupChatSeconds.y), Mathf.Max(groupChatSeconds.x, groupChatSeconds.y));
        c.corners.Clear();
        c.corner = 0;
        c.arrived = true;
        foreach (var m in c.group.members) Act(m, AnimCue.Talking);
        Log(c, "the group stops to chat");
    }

    private void ThinkChatting(Customer c)
    {
        var g = c.group;
        if (g == null || Time.time >= c.until)
        {
            if (g != null) foreach (var m in g.members) Act(m, AnimCue.Talking, false);
            c.chatting = false;
            c.mode = Mode.Wander;
            PickGoal(c);
            return;
        }
        // Facing the middle of the group.
        Vector3 mid = Vector3.zero;
        foreach (var m in g.members) mid += m.pos;
        mid /= g.members.Count;
        if ((mid - c.pos).sqrMagnitude > 0.05f) Face(c, mid);
    }

    // A new family: now and then, a stroller.
    private void MaybeStroller(Customer leader, int kids)
    {
        if (strollerPrefab == null || kids <= 0 || Random.value >= strollerChance) return;
        ShowStroller(leader.id, true);
        StrollerRpc(leader.id, true);
        Act(leader, AnimCue.PushingStroller);
        Log(leader, "pushing a stroller");
    }

    [Rpc(SendTo.NotServer)]
    private void StrollerRpc(ushort id, bool on) => ShowStroller(id, on);

    private void ShowStroller(ushort id, bool on)
    {
        if (on) strollers.Add(id); else strollers.Remove(id);
        if (!views.TryGetValue(id, out var v) || v == null) return;
        v.SetPush(on && strollerPrefab != null ? Instantiate(strollerPrefab) : null);
    }

    // A view just made (or remade): its stroller, if it has one.
    private void StrollerForNewView(ushort id)
    {
        if (strollers.Contains(id)) ShowStroller(id, true);
    }

    private void ForgetMallLife(Customer c)
    {
        if (c.spot != null) c.spot.Release(c.id);
        c.spot = null;
        if (strollers.Remove(c.id)) StrollerRpc(c.id, false); // the id may be used again
    }

    // Called every think: pulled away from a bench or a chat by something else (the mall closing, a
    // store shutting): up off the seat, done talking.
    private void CheckMallLife(Customer c)
    {
        if (c.spot != null && c.mode != Mode.AtSpot)
        {
            if (c.spot.SpotKind == MallSpot.Kind.Seat) Act(c, AnimCue.Sitting, false);
            c.spot.Release(c.id);
            c.spot = null;
        }
        if (c.chatting && c.mode != Mode.Chatting)
        {
            c.chatting = false;
            if (c.group != null) foreach (var m in c.group.members) Act(m, AnimCue.Talking, false);
            else Act(c, AnimCue.Talking, false);
        }
    }
}
