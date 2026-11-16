using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

// Customer groups (couples, friends): walking together, choosing stores together, swaying each other.
// See CustomerGroupDef.
public partial class CustomerManager
{
    private sealed class Group
    {
        public CustomerGroupDef def;
        public readonly List<Customer> members = new(); // [0] is the leader
        public Customer Leader => members.Count > 0 ? members[0] : null;

        // In the store they're in now: how it's going for them (-1 hate it .. +1 love it), and what's been bought.
        public StorePlot moodStore;
        public float mood;
        public readonly HashSet<int> boughtItems = new();
        public readonly HashSet<string> boughtCategories = new();
    }

    private bool groupsLoaded;

    private void LoadGroups()
    {
        groupsLoaded = true;
        groups.RemoveAll(g => g == null);
        if (groups.Count == 0) groups.AddRange(Resources.LoadAll<CustomerGroupDef>("Customer Groups"));
    }

    private CustomerGroupDef PickGroupDef()
    {
        if (!groupsLoaded) LoadGroups();
        float total = 0f;
        foreach (var g in groups) if (g != null) total += g.spawnWeight;
        if (total <= 0f) return null;
        float roll = Random.value * total;
        foreach (var g in groups)
        {
            if (g == null) continue;
            roll -= g.spawnWeight;
            if (roll <= 0f) return g;
        }
        return null;
    }

    private CustomerPersonality PickMemberPersonality(CustomerGroupDef def)
    {
        if (def.personalities == null || def.personalities.Count == 0) return PickPersonality();
        float total = 0f;
        foreach (var p in def.personalities) if (p != null) total += WeightOf(p);
        if (total <= 0f) return PickPersonality();
        float roll = Random.value * total;
        foreach (var p in def.personalities)
        {
            if (p == null) continue;
            roll -= WeightOf(p);
            if (roll <= 0f) return p;
        }
        return PickPersonality();
    }

    // A whole group arrives together (the leader picks where to go; the rest follow). How many came.
    private int SpawnGroup(CustomerGroupDef def, Vector3 near)
    {
        int size = Random.Range(def.size.x, def.size.y + 1);
        var g = new Group { def = def };
        float slowest = float.MaxValue;
        for (int i = 0; i < size; i++)
        {
            var c = Spawn(near + Random.insideUnitSphere * 0.6f, out _, null, PickMemberPersonality(def), pickGoal: false);
            if (c == null) continue;
            c.group = g;
            g.members.Add(c);
            slowest = Mathf.Min(slowest, c.speed);
        }
        if (g.members.Count == 0) return 0;
        if (g.members.Count == 1) { g.members[0].group = null; PickGoal(g.members[0]); return 1; }

        // They walk at the slowest one's pace, and keep the same lane.
        foreach (var m in g.members)
        {
            m.speed = slowest;
            m.lane = g.Leader.lane;
            m.leaveAt = g.Leader.leaveAt;
            m.stayUntil = g.Leader.stayUntil;
        }
        for (int i = 1; i < g.members.Count; i++) g.members[i].mode = Mode.Follow;
        Log(g.Leader, $"leads a group: {def.Name} of {g.members.Count}");
        PickGoal(g.Leader);
        return g.members.Count;
    }

    /* ========================= WALKING TOGETHER ========================= */

    // Where a member walks: beside the leader, one each side, then a row behind.
    private Vector3 GroupSlot(Customer c)
    {
        var g = c.group;
        var leader = g.Leader;
        int slot = g.members.IndexOf(c);
        Vector3 fwd = Quaternion.Euler(0f, leader.yaw, 0f) * Vector3.forward;
        Vector3 right = new(fwd.z, 0f, -fwd.x);
        int row = (slot - 1) / 2;
        float side = (slot % 2 == 1 ? 1f : -1f) * groupSpacing;
        return leader.pos + right * side - fwd * (row * groupSpacing * 0.9f + (row > 0 ? 0.3f : 0f));
    }

    // Walking with the leader (in the hallways). Goes in with them when they go into a store.
    private void ThinkFollow(Customer c)
    {
        var g = c.group;
        var leader = g?.Leader;
        if (leader == null || leader == c) { DropFromGroup(c); PickGoal(c); return; }

        // The leader went into a store: in they go too.
        if (leader.store != null && leader.mode != Mode.Leaving && leader.mode != Mode.Wait && leader.store.IsOpen)
        {
            Enter(c, leader.store, leader.entrance);
            return;
        }

        Vector3 spot = GroupSlot(c);
        if (NavMesh.SamplePosition(spot, out var hit, 1f, CustomerNavMesh.PreferMask)) spot = hit.position;
        if (leader.arrived && FlatDistance(c.pos, spot) < 0.5f)
        {
            c.yaw = leader.yaw;
            c.dirty = true;
            return;
        }
        if (c.arrived || FlatDistance(c.destination, spot) > 0.8f)
            if (!PathTo(c, spot)) PathTo(c, leader.pos);
    }

    // A follower who's fallen behind walks a little faster to catch up.
    private float GroupSpeed(Customer c)
    {
        if (c.group == null || c.mode != Mode.Follow) return c.speed;
        var leader = c.group.Leader;
        return leader != null && FlatDistance(c.pos, leader.pos) > 2.5f ? c.speed * 1.35f : c.speed;
    }

    private static bool SameGroup(Customer a, Customer b) => a.group != null && a.group == b.group;

    /* ========================= CHOOSING STORES TOGETHER ========================= */

    // How a store looks to them: to a group, everyone's view averaged (so one keen member can pull them in).
    private StoreAppeal.Result AppealTo(Customer c, StorePlot plot)
    {
        if (c.group == null || c.group.members.Count <= 1) return plot.Appeal.For(c.personality);
        var sum = new StoreAppeal.Result();
        int n = 0;
        foreach (var m in c.group.members)
        {
            if (m.personality == null) continue;
            var r = plot.Appeal.For(m.personality);
            sum.enterChance += r.enterChance;
            sum.outsideDraw += r.outsideDraw;
            sum.insideDraw += r.insideDraw;
            sum.interest += r.interest;
            n++;
        }
        if (n == 0) return sum;
        sum.enterChance /= n;
        sum.outsideDraw /= n;
        sum.insideDraw /= n;
        sum.interest /= n;
        return sum;
    }

    // Money left between them (a group isn't done while any of them still has some).
    private static int BudgetLeft(Customer c)
    {
        if (c.group == null) return c.budgetCents;
        int sum = 0;
        foreach (var m in c.group.members) sum += Mathf.Max(0, m.budgetCents);
        return sum;
    }

    // A follower doesn't choose where to go: they go with the leader.
    private bool FollowInstead(Customer c)
    {
        var g = c.group;
        if (g == null || g.Leader == c) return false;
        if (g.Leader == null) { DropFromGroup(c); return false; }
        c.mode = Mode.Follow;
        c.goalStore = null;
        c.arrived = true;
        c.nextThink = Time.time;
        return true;
    }

    // The leader goes into a store: everyone with them comes in too, and the group's mood starts fresh.
    private void GroupEntered(Customer leader, StorePlot plot)
    {
        var g = leader.group;
        if (g == null || g.Leader != leader) return;
        g.moodStore = plot;
        g.mood = 0f;
        g.boughtItems.Clear();
        g.boughtCategories.Clear();
        foreach (var m in g.members)
            if (m != leader && (m.mode == Mode.Follow || m.mode == Mode.Wait))
                Enter(m, plot, leader.entrance);
    }

    /* ========================= SWAYING EACH OTHER ========================= */

    // What each reaction does to the group's mood in the store they're in.
    private static float MoodOf(Reaction r) => r switch
    {
        Reaction.Bought => 0.2f,
        Reaction.Paid => 0.1f,
        Reaction.TooPricey => -0.05f,
        Reaction.CheaperElsewhere => -0.05f,
        Reaction.SoldOut => -0.1f,
        Reaction.NothingForMe => -0.35f,
        Reaction.LineTooLong => -0.2f,
        Reaction.GaveUpWaiting => -0.3f,
        Reaction.NoCheckout => -0.3f,
        _ => 0f,
    };

    private void GroupReact(Customer c, Reaction r)
    {
        var g = c.group;
        if (g == null || c.store == null || c.store != g.moodStore) return;
        g.mood = Mathf.Clamp(g.mood + MoodOf(r), -1f, 1f);
    }

    // How much longer (or shorter) the group's mood makes them browse: 1 = as usual.
    private float GroupLooks(Customer c)
    {
        var g = c.group;
        if (g == null || c.store != g.moodStore) return 1f;
        return Mathf.Clamp(1f + g.mood * g.def.sway * 2f, 0.3f, 2.5f);
    }

    // How much keener the group's mood makes them to buy: 1 = as usual.
    private float GroupEagerness(Customer c)
    {
        var g = c.group;
        if (g == null || c.store != g.moodStore) return 1f;
        return Mathf.Clamp(1f + g.mood * g.def.sway, 0.5f, 1.6f);
    }

    // Group tastes and "I'll get one too" for something for sale (1 = no change).
    private float GroupItemBoost(Customer c, int itemId)
    {
        var g = c.group;
        if (g == null || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return 1f;
        float boost = g.def.LikeBoost(def);
        if (g.boughtItems.Contains(itemId)) boost *= 1f + g.def.buyTogether;
        else
            foreach (var cat in g.boughtCategories)
                if (def.HasCategory(cat)) { boost *= 1f + g.def.buyTogether * 0.5f; break; }
        return boost;
    }

    private void GroupBought(Customer c, int itemId)
    {
        var g = c.group;
        if (g == null || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return;
        g.boughtItems.Add(itemId);
        foreach (var cat in def.Categories) if (!string.IsNullOrEmpty(cat)) g.boughtCategories.Add(cat);
    }

    /* ========================= PAYING AND LEAVING TOGETHER ========================= */

    // A couple pays as one: done shopping, a member hands their things to the leader (if the leader's still
    // shopping in the same store) and waits by the door. True if they did.
    private bool HandToPayer(Customer c)
    {
        var g = c.group;
        var leader = g?.Leader;
        if (g == null || !g.def.payTogether || leader == null || leader == c || c.thief || leader.thief ||
            c.basket.Count == 0 || leader.store != c.store) return false;
        if (leader.mode != Mode.Browsing && leader.mode != Mode.ToShelf && leader.mode != Mode.Looking &&
            leader.mode != Mode.Entering) return false;
        int n = c.basket.Count;
        leader.basket.AddRange(c.basket);
        leader.handed += n;
        c.basket.Clear();
        Log(c, $"handed {n} thing(s) to #{leader.id} to pay for");
        LeaveStore(c, c.satisfaction);
        return true;
    }

    // Out of a store: if any of the group are still in there, wait by the door for them (a while);
    // otherwise everyone waiting moves on together. True if handled (group member).
    private bool GroupFinishedStore(Customer c, StorePlot plot)
    {
        var g = c.group;
        if (g == null) return false;
        c.mode = Mode.Wait;
        c.waitFor = plot;
        c.until = Time.time + groupWaitSeconds;
        if (StillInside(g, plot))
        {
            Log(c, $"waiting outside '{plot.DisplayName}' for the others");
            Vector3 spot = Near(c.entrance.outside, 1.2f, plot);
            if (!PathTo(c, spot)) c.arrived = true;
            return true;
        }
        MoveOn(g);
        return true;
    }

    private static bool StillInside(Group g, StorePlot plot)
    {
        foreach (var m in g.members) if (m.store == plot) return true;
        return false;
    }

    // Waiting by a store's door: everyone's out (or they've waited long enough), so off they go.
    private void ThinkWait(Customer c)
    {
        var g = c.group;
        if (g == null) { c.store = null; PickGoal(c); return; }
        if (c.waitFor != null && StillInside(g, c.waitFor) && Time.time < c.until)
        {
            if (c.arrived && c.entrance.outside != Vector3.zero) Face(c, c.entrance.inside);
            return;
        }
        MoveOn(g);
    }

    // Everyone waiting sets off: the leader picks where next, the rest follow.
    private void MoveOn(Group g)
    {
        var leader = g.Leader;
        if (leader == null) return;
        foreach (var m in g.members)
        {
            if (m == leader || m.mode != Mode.Wait) continue;
            m.mode = Mode.Follow;
            m.waitFor = null;
            m.arrived = true;
            m.nextThink = Time.time;
        }
        if (leader.mode == Mode.Wait)
        {
            leader.waitFor = null;
            PickGoal(leader);
        }
    }

    // Leaving the group (gone, or running off with stolen things). A new leader takes over if the
    // leader goes; someone left on their own carries on as a single customer.
    private void DropFromGroup(Customer c)
    {
        var g = c.group;
        c.group = null;
        if (g == null) return;
        bool wasLeader = g.Leader == c;
        g.members.Remove(c);
        if (g.members.Count == 1)
        {
            var last = g.members[0];
            last.group = null;
            g.members.Clear();
            if (last.mode == Mode.Follow || last.mode == Mode.Wait) { last.store = null; PickGoal(last); }
            return;
        }
        if (wasLeader && g.members.Count > 0)
        {
            var next = g.members[0];
            if (next.mode == Mode.Follow) { next.store = null; PickGoal(next); }
        }
    }

    // The leader walked out of the mall: those with them go too, the rest head for the exit.
    private void GroupLeftMall(Customer leader)
    {
        var g = leader.group;
        if (g == null || g.Leader != leader) return;
        foreach (var m in g.members.ToArray())
        {
            if (m == leader) continue;
            m.group = null;
            if (m.mode == Mode.Follow && FlatDistance(m.pos, leader.pos) < 5f) { Despawn(m); continue; }
            m.leaveAt = 0f; // done: the exit next time they choose
            if (m.mode == Mode.Follow || m.mode == Mode.Wait) { m.store = null; PickGoal(m); }
        }
        g.members.Clear();
        leader.group = null;
    }
}
