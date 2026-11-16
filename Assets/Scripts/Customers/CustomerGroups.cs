using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

// Customer groups (couples, friends, families): walking together, choosing stores together, swaying each
// other, and families splitting up and meeting again. See CustomerGroupDef.
//
// Everyone in a group follows someone (Customer.follow), up to a "party leader" who follows no one and
// makes the choices. Usually that's the group's leader; a family that splits has two parties (each
// parent with their children), which meet up again later. Children always follow a parent: they never
// shop themselves, but they pull the family toward stores they like and ask for things.
public partial class CustomerManager
{
    private sealed class Group
    {
        public CustomerGroupDef def;
        public readonly List<Customer> members = new(); // [0] is the leader
        public Customer Leader => members.Count > 0 ? members[0] : null;

        // What any of them have bought in the store they're in (for "I'll get one too").
        public readonly HashSet<int> boughtItems = new();
        public readonly HashSet<string> boughtCategories = new();

        // A family that split up: when and where they meet again.
        public bool split;
        public int splits;
        public float meetBy;
        public Vector3 meetSpot;
    }

    private bool groupsLoaded;

    private void LoadGroups()
    {
        groupsLoaded = true;
        groups.RemoveAll(g => g == null);
        if (groups.Count == 0) groups.AddRange(Resources.LoadAll<CustomerGroupDef>("Customer Groups"));
    }

    // A shoppers' group (bystanders false) or a bystanders' one (true: every adult it can have is a bystander).
    private CustomerGroupDef PickGroupDef(bool bystanders)
    {
        if (!groupsLoaded) LoadGroups();
        float total = 0f;
        foreach (var g in groups) if (g != null && IsBystanderGroup(g) == bystanders) total += g.spawnWeight;
        if (total <= 0f) return null;
        float roll = Random.value * total;
        foreach (var g in groups)
        {
            if (g == null || IsBystanderGroup(g) != bystanders) continue;
            roll -= g.spawnWeight;
            if (roll <= 0f) return g;
        }
        return null;
    }

    private static bool IsBystanderGroup(CustomerGroupDef def)
    {
        if (def.personalities == null || def.personalities.Count == 0) return false;
        foreach (var p in def.personalities) if (p != null && !p.bystander) return false;
        return true;
    }

    // Bystanders come on their own count (Max Bystanders), alone or as a bystander group.
    private void UpdateBystanderArrivals(int bystanders)
    {
        if (bystanders >= maxBystanders) { bystanderCredit = 0f; return; }
        bystanderCredit += bystanderArrivalsPerMinute / 60f * Time.deltaTime;
        while (bystanderCredit >= 1f && bystanders < maxBystanders)
        {
            bystanderCredit -= 1f;
            var sp = PickSpawnPoint(null);
            if (sp == null) return;
            var def = Random.value < groupChance ? PickGroupDef(true) : null;
            if (def != null) { int n = SpawnGroup(def, sp.transform.position); bystanders += Mathf.Max(1, n); bystanderCredit -= Mathf.Max(0, n - 1); continue; }
            var p = PickPersonality(true);
            if (p == null) { bystanderCredit = 0f; return; } // no bystander personalities
            Spawn(sp.transform.position, out _, null, p);
            bystanders++;
        }
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

    // Children: any of the family's Child Personalities, evenly (their Spawn Weight is for single customers).
    private static CustomerPersonality PickChildPersonality(CustomerGroupDef def)
    {
        if (def.childPersonalities == null) return null;
        int n = 0;
        foreach (var p in def.childPersonalities) if (p != null) n++;
        if (n == 0) return null;
        int pick = Random.Range(0, n);
        foreach (var p in def.childPersonalities)
            if (p != null && pick-- == 0) return p;
        return null;
    }

    // A whole group arrives together (the leader picks where to go; the rest follow). How many came.
    private int SpawnGroup(CustomerGroupDef def, Vector3 near)
    {
        var g = new Group { def = def };
        var adults = new List<Customer>();
        int nAdults = def.kind == CustomerGroupDef.Kind.Family ? Random.Range(def.adults.x, def.adults.y + 1)
                                                                 : Random.Range(def.size.x, def.size.y + 1);
        int nKids = def.kind == CustomerGroupDef.Kind.Family ? Random.Range(def.children.x, def.children.y + 1) : 0;
        float slowest = float.MaxValue;
        for (int i = 0; i < nAdults; i++)
        {
            var c = Spawn(near + Random.insideUnitSphere * 0.6f, out _, null, PickMemberPersonality(def), pickGoal: false);
            if (c == null) continue;
            c.group = g;
            g.members.Add(c);
            adults.Add(c);
            slowest = Mathf.Min(slowest, c.speed);
        }
        if (adults.Count == 0) return 0;
        for (int i = 0; i < nKids; i++)
        {
            var kp = PickChildPersonality(def);
            if (kp == null) break;
            var kid = Spawn(near + Random.insideUnitSphere * 0.6f, out _, null, kp, pickGoal: false, child: true);
            if (kid == null) continue;
            kid.group = g;
            var parent = adults[Random.Range(0, adults.Count)];
            kid.follow = parent;
            parent.wants++; // they'll want something for the child too
            g.members.Add(kid);
            slowest = Mathf.Min(slowest, kid.speed);
        }
        if (g.members.Count == 1) { g.members[0].group = null; PickGoal(g.members[0]); return 1; }
        bool bystanders = IsBystanderGroup(def);
        foreach (var m in g.members) m.bystanderSlot = bystanders; // children count with their parent

        // They walk at the slowest one's pace, and keep the same lane.
        var leader = g.Leader;
        foreach (var m in g.members)
        {
            m.speed = slowest;
            m.lane = leader.lane;
            m.leaveAt = leader.leaveAt;
            m.stayUntil = leader.stayUntil;
            if (m == leader) continue;
            if (!m.child) m.follow = leader;
            m.mode = Mode.Follow;
        }
        Log(leader, $"leads a group: {def.Name} of {g.members.Count}" + (nKids > 0 ? $" ({g.members.Count - adults.Count} child(ren))" : ""));
        PickGoal(leader);
        return g.members.Count;
    }

    /* ========================= WHO FOLLOWS WHOM ========================= */

    // The one making the choices for this customer's party (themselves if they follow no one).
    private static Customer Root(Customer c)
    {
        var r = c;
        for (int i = 0; i < 8 && r.follow != null; i++) r = r.follow;
        return r;
    }

    /// A bystander (or with one): walks the halls, never goes into a store.
    private static bool IsBystander(Customer c)
    {
        var root = Root(c);
        return root.personality != null && root.personality.bystander;
    }

    private static bool SameParty(Customer a, Customer b) => a.group != null && a.group == b.group && Root(a) == Root(b);

    // Where a member walks: beside whoever they follow, one each side, then a row behind.
    private Vector3 GroupSlot(Customer c)
    {
        var target = c.follow;
        int slot = 1;
        foreach (var m in c.group.members)
        {
            if (m == c) break;
            if (m.follow == target) slot++;
        }
        Vector3 fwd = Quaternion.Euler(0f, target.yaw, 0f) * Vector3.forward;
        Vector3 right = new(fwd.z, 0f, -fwd.x);
        int row = (slot - 1) / 2;
        float spacing = c.child ? groupSpacing * 0.75f : groupSpacing; // a child holds close
        float side = (slot % 2 == 1 ? 1f : -1f) * spacing;
        return target.pos + right * side - fwd * (row * spacing * 0.9f + (row > 0 ? 0.3f : 0f));
    }

    // Walking with whoever they follow. Adults go into a store with them; children stay at their side,
    // in stores too.
    private void ThinkFollow(Customer c)
    {
        var g = c.group;
        var target = c.follow;
        if (target == null || target == c || !byId.ContainsKey(target.id))
        {
            target = g != null && g.Leader != c ? g.Leader : null;
            if (target == null) { c.follow = null; DropFromGroup(c); PickGoal(c); return; }
            c.follow = target;
        }

        // Whoever they're with went into a store: in they go too (to shop for themselves).
        if (!c.child && target.store != null && target.mode != Mode.Leaving && target.mode != Mode.Wait &&
            target.store.IsOpen)
        {
            Enter(c, target.store, target.entrance);
            return;
        }

        Vector3 spot = GroupSlot(c);
        if (NavMesh.SamplePosition(spot, out var hit, 1f, CustomerNavMesh.PreferMask)) spot = hit.position;
        if (target.arrived && FlatDistance(c.pos, spot) < 0.5f)
        {
            if (c.child && target.mode == Mode.Looking) Face(c, target.targetPoint); // looking at it too
            else { c.yaw = target.yaw; c.dirty = true; }
            return;
        }
        if (c.arrived || FlatDistance(c.destination, spot) > 0.8f)
            if (!PathTo(c, spot)) PathTo(c, target.pos);
    }

    // A follower who's fallen behind walks a little faster to catch up.
    private float GroupSpeed(Customer c)
    {
        if (c.follow == null || c.mode != Mode.Follow) return c.speed;
        return FlatDistance(c.pos, c.follow.pos) > 2.5f ? c.speed * 1.35f : c.speed;
    }

    private static bool SameGroup(Customer a, Customer b) => a.group != null && a.group == b.group;

    /* ========================= CHOOSING STORES TOGETHER ========================= */

    // How a store looks to them: to a party, everyone's view averaged (children count Child Pull times,
    // so kids drag their parents into toy stores).
    private StoreAppeal.Result AppealTo(Customer c, StorePlot plot)
    {
        if (c.group == null || c.group.members.Count <= 1) return plot.Appeal.For(c.personality);
        var root = Root(c);
        var sum = new StoreAppeal.Result();
        float n = 0f;
        foreach (var m in c.group.members)
        {
            if (m.personality == null || Root(m) != root) continue;
            float w = m.child ? c.group.def.childPull : 1f;
            var r = plot.Appeal.For(m.personality);
            sum.enterChance += r.enterChance * w;
            sum.outsideDraw += r.outsideDraw * w;
            sum.insideDraw += r.insideDraw * w;
            sum.interest += r.interest * w;
            n += w;
        }
        if (n <= 0f) return plot.Appeal.For(c.personality);
        sum.enterChance = Mathf.Clamp01(sum.enterChance / n);
        sum.outsideDraw = Mathf.Clamp01(sum.outsideDraw / n);
        sum.insideDraw = Mathf.Clamp01(sum.insideDraw / n);
        sum.interest = Mathf.Clamp01(sum.interest / n);
        return sum;
    }

    // Money left in their party (they aren't done while any of them still has some).
    private static int BudgetLeft(Customer c)
    {
        if (c.group == null) return c.budgetCents;
        var root = Root(c);
        int sum = 0;
        foreach (var m in c.group.members) if (Root(m) == root) sum += Mathf.Max(0, m.budgetCents);
        return sum;
    }

    // A follower doesn't choose where to go: they go with whoever they follow.
    private bool FollowInstead(Customer c)
    {
        if (c.group == null || c.follow == null) return false;
        c.mode = Mode.Follow;
        c.goalStore = null;
        c.arrived = true;
        c.nextThink = Time.time;
        return true;
    }

    // Someone goes into a store: the adults with them come in too, and their party's mood starts fresh.
    private void GroupEntered(Customer c, StorePlot plot)
    {
        var g = c.group;
        if (g == null) return;
        if (c.follow == null)
        {
            c.partyStore = plot;
            c.partyMood = 0f;
            g.boughtItems.Clear();
            g.boughtCategories.Clear();
        }
        foreach (var m in g.members.ToArray())
            if (m != c && m.follow == c && !m.child && (m.mode == Mode.Follow || m.mode == Mode.Wait))
                Enter(m, plot, c.entrance);
    }

    /* ========================= SWAYING EACH OTHER ========================= */

    // What each reaction does to the party's mood in the store they're in.
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
        var root = Root(c);
        if (c.store == null || c.store != root.partyStore) return;
        root.partyMood = Mathf.Clamp(root.partyMood + MoodOf(r), -1f, 1f);
    }

    private float PartyMood(Customer c, out CustomerGroupDef def)
    {
        def = c.group?.def;
        if (def == null) return 0f;
        var root = Root(c);
        return c.store != null && c.store == root.partyStore ? root.partyMood : 0f;
    }

    // How much longer (or shorter) the party's mood makes them browse: 1 = as usual.
    private float GroupLooks(Customer c)
    {
        float mood = PartyMood(c, out var def);
        return def == null ? 1f : Mathf.Clamp(1f + mood * def.sway * 2f, 0.3f, 2.5f);
    }

    // How much keener the party's mood makes them to buy: 1 = as usual.
    private float GroupEagerness(Customer c)
    {
        float mood = PartyMood(c, out var def);
        return def == null ? 1f : Mathf.Clamp(1f + mood * def.sway, 0.5f, 1.6f);
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

    // What it's worth to the children with them ("can I have one?"): their parent buys it for them.
    private float KidsWorth(Customer c, StorePlot plot, StoreListing.Source s, float tolerance)
    {
        var g = c.group;
        if (g == null || c.child) return 0f;
        float best = 0f;
        foreach (var m in g.members)
            if (m.child && m.follow == c && m.personality != null)
                best = Mathf.Max(best, plot.Appeal.WorthTo(m.personality, s) * tolerance * g.def.childPull);
        return best;
    }

    private void GroupBought(Customer c, int itemId)
    {
        var g = c.group;
        if (g == null || ItemCatalog.Instance == null || !ItemCatalog.Instance.TryGet(itemId, out var def)) return;
        g.boughtItems.Add(itemId);
        foreach (var cat in def.Categories) if (!string.IsNullOrEmpty(cat)) g.boughtCategories.Add(cat);
    }

    /* ========================= PAYING AND LEAVING TOGETHER ========================= */

    // Paying as one: done shopping, a member hands their things to whoever leads their party (if they're
    // still shopping in the same store) and waits by the door. True if they did.
    private bool HandToPayer(Customer c)
    {
        var g = c.group;
        if (g == null || !g.def.payTogether || c.thief || c.basket.Count == 0) return false;
        var payer = Root(c);
        if (payer == c || payer.thief || payer.store != c.store) return false;
        if (payer.mode != Mode.Browsing && payer.mode != Mode.ToShelf && payer.mode != Mode.Looking &&
            payer.mode != Mode.Entering) return false;
        int n = c.basket.Count;
        payer.basket.AddRange(c.basket);
        payer.handed += n;
        c.basket.Clear();
        Log(c, $"handed {n} thing(s) to #{payer.id} to pay for");
        LeaveStore(c, c.satisfaction);
        return true;
    }

    // Out of a store: if any of their party are still in there, wait by the door for them (a while);
    // otherwise everyone waiting moves on together. True if handled (group member).
    private bool GroupFinishedStore(Customer c, StorePlot plot)
    {
        var g = c.group;
        if (g == null) return false;
        var root = Root(c);
        c.mode = Mode.Wait;
        c.waitFor = plot;
        c.meeting = false;
        c.until = Time.time + groupWaitSeconds;
        if (StillInside(root, plot))
        {
            Log(c, $"waiting outside '{plot.DisplayName}' for the others");
            if (!PathTo(c, Near(c.entrance.outside, 1.2f, plot))) c.arrived = true;
            return true;
        }
        MoveOn(root);
        return true;
    }

    private static bool StillInside(Customer root, StorePlot plot)
    {
        var g = root.group;
        if (g == null) return false;
        foreach (var m in g.members) if (m.store == plot && Root(m) == root) return true;
        return false;
    }

    // Waiting outside a store (everyone's out, or they've waited long enough: off they go), or at the
    // family's meeting spot.
    private void ThinkWait(Customer c)
    {
        var g = c.group;
        if (g == null) { c.store = null; c.meeting = false; PickGoal(c); return; }
        if (c.meeting) { ThinkMeeting(c); return; }
        var root = Root(c);
        if (c.waitFor != null && StillInside(root, c.waitFor) && Time.time < c.until)
        {
            if (c.arrived) Face(c, c.entrance.inside);
            return;
        }
        MoveOn(root);
    }

    // Everyone in the party who was waiting sets off: the one leading picks where next, the rest follow.
    private void MoveOn(Customer root)
    {
        var g = root.group;
        if (g == null) return;
        foreach (var m in g.members)
        {
            if (m == root || m.mode != Mode.Wait || m.meeting || Root(m) != root) continue;
            m.mode = Mode.Follow;
            m.waitFor = null;
            m.arrived = true;
            m.nextThink = Time.time;
        }
        if (root.mode == Mode.Wait && !root.meeting)
        {
            root.waitFor = null;
            PickGoal(root);
        }
    }

    /* ========================= FAMILIES SPLITTING UP ========================= */

    // Before a party leader picks somewhere new: a family may split up here (the other parent takes their
    // children elsewhere), and a family that's split heads back to the meeting spot when it's time.
    // True if that's what they're doing now.
    private bool FamilyPlans(Customer c)
    {
        var g = c.group;
        if (g == null || g.def.kind != CustomerGroupDef.Kind.Family || c.follow != null) return false;
        bool done = Time.time >= c.leaveAt || c.storesVisited >= maxStoresPerVisit || BudgetLeft(c) <= 0;

        if (g.split)
        {
            if (!done && Time.time < g.meetBy) return false;
            GoMeet(c);
            return true;
        }

        // Split up? The leader decides, with another adult to go off with the children who follow them.
        if (done || c != g.Leader || g.splits >= 2 || Random.value >= g.def.splitChance) return false;
        Customer other = null;
        foreach (var m in g.members)
            if (m != c && !m.child && m.follow == c && m.mode == Mode.Follow) { other = m; break; }
        if (other == null) return false;
        g.split = true;
        g.splits++;
        g.meetBy = Time.time + Random.Range(g.def.meetAfterSeconds.x, g.def.meetAfterSeconds.y);
        g.meetSpot = Near(c.pos, 2f, null); // "meet back here"
        other.follow = null;
        other.partyStore = null;
        Log(c, $"family splits up: #{other.id} goes off on their own, meeting back here in {g.meetBy - Time.time:0} s");
        other.mode = Mode.Wander;
        PickGoal(other); // the other party chooses its own way
        return false;
    }

    private void GoMeet(Customer c)
    {
        var g = c.group;
        c.mode = Mode.Wait;
        c.meeting = true;
        c.waitFor = null;
        c.goalStore = null;
        c.until = Time.time + groupWaitSeconds;
        if (!PathTo(c, Near(g.meetSpot, 1.5f, null))) c.arrived = true;
        Log(c, "heading back to meet the rest of the family");
    }

    // At the meeting spot: once every party is there (or they've waited long enough), the family is one
    // party again, with the leader choosing.
    private void ThinkMeeting(Customer c)
    {
        var g = c.group;
        bool allHere = true;
        foreach (var m in g.members)
            if (m.follow == null && (!m.meeting || !m.arrived)) { allHere = false; break; }
        if (!allHere && Time.time < c.until) return;

        var leader = g.Leader;
        foreach (var m in g.members)
        {
            if (m == leader || m.follow != null) continue;
            m.follow = leader; // back together (anyone still in a store catches up when they're done)
            m.meeting = false;
            if (m.mode == Mode.Wait) { m.mode = Mode.Follow; m.arrived = true; m.nextThink = Time.time; }
        }
        g.split = false;
        Log(leader, allHere ? "family's back together" : "family's back together (stopped waiting)");
        leader.meeting = false;
        if (leader.mode == Mode.Wait) PickGoal(leader);
    }

    /* ========================= LEAVING THE GROUP ========================= */

    // Leaving the group (gone, or running off with stolen things). Whoever followed them follows whoever
    // they followed (or the leader); a new leader takes over if the leader goes; someone left on their
    // own carries on as a single customer.
    private void DropFromGroup(Customer c)
    {
        var g = c.group;
        c.group = null;
        var was = c.follow;
        c.follow = null;
        if (g == null) return;
        bool wasLeader = g.Leader == c;
        g.members.Remove(c);
        var nextLeader = g.Leader;
        foreach (var m in g.members)
            if (m.follow == c) m.follow = was != null && was != c ? was : (m != nextLeader ? nextLeader : null);

        int adults = 0;
        foreach (var m in g.members) if (!m.child) adults++;
        if (adults == 0 || g.members.Count == 1)
        {
            // Nobody left to be with: each carries on alone.
            foreach (var m in g.members.ToArray())
            {
                m.group = null;
                m.follow = null;
                if (m.mode == Mode.Follow || m.mode == Mode.Wait) { m.store = null; m.meeting = false; PickGoal(m); }
            }
            g.members.Clear();
            return;
        }
        if (wasLeader && nextLeader != null)
        {
            if (nextLeader.child)
            {
                // A child can't lead: the first adult does.
                foreach (var m in g.members) if (!m.child) { g.members.Remove(m); g.members.Insert(0, m); break; }
                nextLeader = g.Leader;
            }
            nextLeader.follow = null;
            if (nextLeader.mode == Mode.Follow) { nextLeader.store = null; PickGoal(nextLeader); }
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
            if (m.child) continue; // with their parent: they go when their parent does (see Despawn)
            bool withThem = m.mode == Mode.Follow && FlatDistance(m.pos, leader.pos) < 5f;
            if (withThem) { m.follow = null; Despawn(m); continue; }
            m.follow = null;
            m.leaveAt = 0f; // done: the exit next time they choose
            if (m.mode == Mode.Follow || m.mode == Mode.Wait) { m.store = null; m.meeting = false; PickGoal(m); }
        }
        g.members.Clear();
        leader.group = null;
    }
}
