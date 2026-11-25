using UnityEngine;
using Unity.Netcode;
using UnityEngine.AI;
using Random = UnityEngine.Random;

// Lost children. Now and then, while a family walks the halls, a child wanders off. Everyone is told,
// and the child and the family are marked over their heads (seen through walls) so players can find
// them. The family stops shopping and searches around where the child was lost.
//
// A player who reaches the child and presses F is followed by them. Walk too far ahead for too long and
// the child is lost again where they stopped. Bring them close to their family and they're back
// together: the family is happier, and the player gets a help point and XP for their team. Left alone
// long enough, the child finds the family themselves (no reward).
public partial class CustomerManager
{
    [Header("Lost Children")]
    [Tooltip("Minutes after a lost child (across the whole mall) before the next one can happen. From then on " +
             "there's a chance every minute (Lost Child Chance), growing until 4 times this, then staying there. " +
             "Never guaranteed. 0: children never get lost.")]
    [SerializeField, Min(0f)] private float lostChildEveryMinutes = 10f;
    [Tooltip("Chance a minute of a child getting lost: x right at Lost Child Every Minutes, growing to y at 4 times " +
             "it (and staying y after). E.g. 0.01 = 1% a minute.")]
    [SerializeField] private Vector2 lostChildChance = new(0.01f, 0.06f);
    [Tooltip("Into the game, the chance starts after this many minutes instead (then grows the same way).")]
    [SerializeField, Min(0f)] private float firstLostChildAfterMinutes = 3f;
    [Tooltip("At most this many children lost at the same time.")]
    [SerializeField, Min(1)] private int maxLostAtOnce = 1;
    [Tooltip("How far from their family a lost child wanders off to before stopping (meters, straight line; " +
             "the walk can be longer). Once there they stand still, and everyone is told.")]
    [SerializeField] private Vector2 lostDistance = new(30f, 55f);
    [Tooltip("Still walking off after this long (seconds): everyone's told anyway, and they stop where they are.")]
    [SerializeField, Min(5f)] private float lostWalkSeconds = 90f;
    [Tooltip("How far around where the child was lost the family searches (meters).")]
    [SerializeField, Min(2f)] private float searchRadius = 10f;
    [Tooltip("A child this close to someone of their family is back with them (meters).")]
    [SerializeField, Min(0.5f)] private float reunionDistance = 2.5f;
    [Tooltip("A child following a player: farther from them than this (meters)...")]
    [SerializeField, Min(2f)] private float helperKeepUpDistance = 7f;
    [Tooltip("...for this long (seconds) and the child is lost again, where they stopped.")]
    [SerializeField, Min(0.5f)] private float helperLostSeconds = 4f;
    [Tooltip("How fast a child following a player can hurry to keep up (meters a second).")]
    [SerializeField, Min(0.5f)] private float helperFollowSpeed = 4f;
    [Tooltip("Minutes before a lost child nobody helps finds their family by themselves (no reward).")]
    [SerializeField, Min(0.5f)] private float lostFindThemselvesMinutes = 5f;
    [Tooltip("How much happier the family is when a player brings their child back (added to each adult's " +
             "satisfaction, -1..1).")]
    [SerializeField, Range(0f, 1f)] private float reunionHappiness = 0.5f;
    [Tooltip("XP for the team of the player who brings a lost child back.")]
    [SerializeField, Min(0f)] private float reunionXp = 30f;

    /// <summary>Who's lost, following a player, or searching (sent to everyone: the interact prompt).</summary>
    public enum LostMark : byte { None, LostChild, Following, Searching }

    // Host: the clock the chance grows on (game start, then the last lost child), when it last rolled, and a
    // roll that came up (the next child walking the halls gets lost).
    private float lostClockFrom = -1f, lostRolledAt = -1f;
    private bool lostChildDue, lostFirstDone;

    // Rolls the chance for the time since the last roll (mall-wide, however many children are about).
    private void RollLostChild()
    {
        if (lostClockFrom < 0f) lostClockFrom = lostRolledAt = Time.time;
        float dt = Time.time - lostRolledAt;
        if (lostChildDue || dt < 1f) return;
        lostRolledAt = Time.time;
        float every = lostChildEveryMinutes * 60f;
        float start = lostFirstDone ? every : firstLostChildAfterMinutes * 60f;
        float since = Time.time - lostClockFrom;
        if (since < start) return;
        float t = Mathf.Clamp01((since - start) / (every * 3f));
        float perMinute = Mathf.Clamp01(Mathf.Lerp(lostChildChance.x, lostChildChance.y, t));
        // The chance over dt seconds of a per-minute chance.
        if (Random.value < 1f - Mathf.Pow(1f - perMinute, dt / 60f)) lostChildDue = true;
    }

    // Every machine: who's marked.
    private readonly System.Collections.Generic.Dictionary<ushort, byte> marks = new();

    /* ========================= GETTING LOST ========================= */

    // A child following their family through the halls may wander off (once per visit per family).
    private bool MaybeGetLost(Customer kid)
    {
        var g = kid.group;
        var parent = kid.follow;
        if (g == null || g.lostOnce || parent == null || lostChildEveryMinutes <= 0f || !byId.ContainsKey(parent.id)) return false;
        // Mall-wide: a chance that grows over time (the first child walking the halls once it comes up), a few at most.
        RollLostChild();
        if (!lostChildDue || cases.Count >= maxLostAtOnce) return false;
        var root = Root(kid);
        if (root.store != null || root.mode != Mode.Wander || root.searchingFor != null) return false; // walking the halls
        // Not a family with a shoplifter in it (searching parents can't be stopped and checked).
        foreach (var m in g.members)
            if (m != null && (m.thief || m.fleeing || m.stolen.Count > 0)) return false;
        // Far off (a shorter way if nothing's that far).
        if (!NearbyHallwaySpot(kid.pos, lostDistance.x, lostDistance.y, out var spot) &&
            !NearbyHallwaySpot(kid.pos, lostDistance.x * 0.5f, lostDistance.x, out spot)) return false;

        g.lostOnce = true;
        lostChildDue = false;
        lostFirstDone = true;
        lostClockFrom = lostRolledAt = Time.time;
        kid.lostFrom = parent;
        kid.lostSince = Time.time;
        kid.lostLetDown = false;
        kid.lostAnnounced = false;
        kid.lostSettled = false;
        kid.lostAt = kid.pos;
        kid.follow = null;
        kid.mode = Mode.Lost;
        Act(kid, AnimCue.Lost);
        kid.helper = ulong.MaxValue;
        if (!PathTo(kid, spot)) kid.arrived = true;

        // Their family stops what they're doing and looks around where they lost them (the child is
        // walking off meanwhile; everyone's told once the child has stopped, see Announce).
        root.searchingFor = kid;
        root.mode = Mode.Searching;
        root.goalStore = null;
        root.arrived = true;
        root.nextThink = Time.time;
        Log(kid, $"wandered off (from {parent.id}) toward a spot {FlatDistance(kid.pos, spot):0} m away; family {root.id} searching");
        return true;
    }

    // The child has stopped, far from their family: now everyone knows (and the time they've been lost
    // starts from here).
    private void Announce(Customer kid)
    {
        // On the stairs, in a doorway...: walk on to somewhere better first (once), then they're told.
        if (!kid.lostSettled)
        {
            kid.lostSettled = true;
            if (MoveOffBadSpot(kid)) { kid.lostSince = Time.time - lostWalkSeconds + 20f; return; }
        }
        kid.lostAnnounced = true;
        kid.lostSince = Time.time;
        kid.corners.Clear();
        kid.corner = 0;
        kid.arrived = true;
        if (kid.mode == Mode.Lost) SetMark(kid, LostMark.LostChild);
        React(kid, Reaction.LostMyFamily);
        var family = FamilyOf(kid);
        var root = family != null ? Root(family) : null;
        if (root == null) return;
        React(root, Reaction.LostChild);
        MarkParty(root, LostMark.Searching);
        CaseRpc(kid.id, root.id, true);
        Log(kid, $"lost, {FlatDistance(kid.pos, root.pos):0} m from their family: everyone's told");

        var notes = NotificationManager.Instance;
        if (notes != null)
            foreach (var client in NetworkManager.ConnectedClientsList)
                notes.ServerNotifyClientText(client.ClientId, "Lost child!",
                    "A family has lost their child. Open Lost & Found on your phone to see where the child and their parents are.");
    }

    // Somewhere in the halls (not in a store) between min and max meters away, that can be walked to.
    private bool NearbyHallwaySpot(Vector3 from, float min, float max, out Vector3 spot)
    {
        spot = default;
        var mall = MallManager.Instance;
        for (int tries = 0; tries < 24; tries++)
        {
            Vector2 d = Random.insideUnitCircle.normalized * Random.Range(min, max);
            var p = from + new Vector3(d.x, 0f, d.y);
            if (!NavMesh.SamplePosition(p, out var hit, 3f, CustomerNavMesh.PreferMask)) continue;
            if (Mathf.Abs(hit.position.y - from.y) > 2f) continue; // same floor
            if (mall != null && mall.GetPlotAt(hit.position + Vector3.up * 0.1f) != null) continue; // not in a store
            if (!GoodLostSpot(hit.position)) continue;
            spot = hit.position;
            return true;
        }
        return false;
    }

    // Somewhere a lost child can stand: flat floor (not stairs or a ramp), not a doorway, not in a store,
    // not in a No Lost Child Zone.
    private bool GoodLostSpot(Vector3 p)
    {
        if (!NavMesh.SamplePosition(p, out var hit, 0.6f, CustomerNavMesh.CustomerMask)) return false;
        p = hit.position;
        for (int i = 0; i < 4; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward * 0.7f;
            if (NavMesh.SamplePosition(p + d, out var h, 0.5f, CustomerNavMesh.CustomerMask) && Mathf.Abs(h.position.y - p.y) > 0.12f)
                return false; // sloping or stepped: stairs, a ramp
        }
        if (InDoorway(p) || NoLostChildZone.Contains(p)) return false;
        var mall = MallManager.Instance;
        if (mall != null && mall.GetPlotAt(p + Vector3.up * 0.1f) != null) return false;
        return true;
    }

    // The nearest good spot to stand within Radius (rings a meter apart), on the same floor.
    private bool NearestGoodLostSpot(Vector3 from, float radius, out Vector3 spot)
    {
        spot = from;
        for (float r = 1f; r <= radius; r += 1f)
            for (int i = 0; i < 12; i++)
            {
                Vector3 p = from + Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward * r;
                if (!NavMesh.SamplePosition(p, out var hit, 1f, CustomerNavMesh.CustomerMask) || Mathf.Abs(hit.position.y - from.y) > 1.5f) continue;
                if (!GoodLostSpot(hit.position)) continue;
                spot = hit.position;
                return true;
            }
        return false;
    }

    // Stopped somewhere a child shouldn't wait (the stairs): walk on to the nearest good spot first.
    private bool MoveOffBadSpot(Customer kid)
    {
        if (GoodLostSpot(kid.pos) || !NearestGoodLostSpot(kid.pos, 15f, out var spot)) return false;
        if (!PathTo(kid, spot)) return false;
        Log(kid, $"stopped somewhere a child shouldn't wait ({kid.pos:F1}): moving on to {spot:F1}");
        return true;
    }

    /* ========================= LOST ========================= */

    private void ThinkLost(Customer kid)
    {
        // Still walking off: told once they've stopped (or taken too long about it).
        if (!kid.lostAnnounced)
        {
            if (FamilyOf(kid) == null) { GoHomeAlone(kid); return; }
            if (kid.arrived || Time.time - kid.lostSince >= lostWalkSeconds) Announce(kid);
            return;
        }
        // On their own they only find their family once they've given up waiting and go looking (the
        // family, searching, never stumbles on them: finding them is the players' job).
        bool gaveUp = Time.time - kid.lostSince >= lostFindThemselvesMinutes * 60f;
        if (gaveUp && TryReunite(kid, helper: ulong.MaxValue)) return;
        var parent = FamilyOf(kid);
        if (parent == null) { GoHomeAlone(kid); return; }

        // Nobody came: off to find them.
        if (gaveUp)
        {
            if (!kid.lostLetDown) LostChildLetDown(kid);
            if (kid.arrived || FlatDistance(kid.destination, parent.pos) > 2f) PathTo(kid, parent.pos);
            return;
        }
        if (kid.arrived && Random.value < 0.02f) React(kid, Reaction.LostMyFamily); // now and then
    }

    // The family they belong to (the one they wandered from, else whoever leads their party); null: gone.
    private Customer FamilyOf(Customer kid)
    {
        if (kid.lostFrom != null && byId.ContainsKey(kid.lostFrom.id)) return kid.lostFrom;
        if (kid.group != null)
            foreach (var m in kid.group.members)
                if (m != kid && !m.child && byId.ContainsKey(m.id)) return m;
        return null;
    }

    // Their family's gone (left, or got stuck): they leave too.
    private void GoHomeAlone(Customer kid)
    {
        Log(kid, "lost, and their family's gone: leaving");
        if (!kid.lostLetDown) LostChildLetDown(kid);
        SetMark(kid, LostMark.None);
        CaseRpc(kid.id, 0, false);
        Despawn(kid);
    }

    /* ========================= FOLLOWING A PLAYER ========================= */

    // F on a lost child: they follow that player (true: it was one).
    private bool HelpLostChild(Customer kid, ulong player)
    {
        if (kid.mode == Mode.WithHelper) return true; // already following someone
        if (kid.mode != Mode.Lost) return false;
        kid.mode = Mode.WithHelper;
        kid.helper = player;
        kid.helperFarSince = -1f;
        kid.arrived = true;
        kid.nextThink = Time.time;
        SetMark(kid, LostMark.Following);
        Log(kid, $"following player {player}");
        if (NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyClientText(player, "They're following you",
                "Bring the child to their parents (Lost & Found on your phone shows where). Stay close, or they'll get lost again.");
        return true;
    }

    private void ThinkWithHelper(Customer kid)
    {
        kid.nextThink = Time.time + 0.25f; // keeping up with someone: look often
        if (TryReunite(kid, kid.helper)) return;
        if (FamilyOf(kid) == null) { GoHomeAlone(kid); return; }
        if (!TryGetPlayer(kid.helper, out Vector3 at)) { LostAgain(kid, notify: false); return; } // they left

        float d = FlatDistance(kid.pos, at);
        if (d > helperKeepUpDistance)
        {
            if (kid.helperFarSince < 0f) kid.helperFarSince = Time.time;
            else if (Time.time - kid.helperFarSince >= helperLostSeconds) { LostAgain(kid, notify: true); return; }
        }
        else kid.helperFarSince = -1f;

        // Just behind the player.
        if (d > 1.4f && (kid.arrived || FlatDistance(kid.destination, at) > 1f))
        {
            Vector3 spot = at + (kid.pos - at).normalized * 1f;
            if (!NavMesh.SamplePosition(spot, out var hit, 1.5f, CustomerNavMesh.PreferMask) || !PathTo(kid, hit.position))
                PathTo(kid, at);
        }
        else if (d <= 1.4f) { kid.corners.Clear(); kid.corner = 0; kid.arrived = true; Face(kid, at); }
    }

    // Hurrying to keep up with the player (up to Helper Follow Speed), else their own pace.
    private float HelperSpeed(Customer kid) =>
        TryGetPlayer(kid.helper, out Vector3 at) && FlatDistance(kid.pos, at) > 2.5f ? Mathf.Max(kid.speed, helperFollowSpeed) : kid.speed;

    // Fell behind: lost again where they are.
    private void LostAgain(Customer kid, bool notify)
    {
        ulong player = kid.helper;
        kid.mode = Mode.Lost;
        Act(kid, AnimCue.Lost);
        kid.helper = ulong.MaxValue;
        kid.helperFarSince = -1f;
        kid.corners.Clear();
        kid.corner = 0;
        kid.arrived = true;
        SetMark(kid, LostMark.LostChild);
        React(kid, Reaction.LostMyFamily);
        Log(kid, "lost again (fell behind the player)");
        MoveOffBadSpot(kid); // not on the stairs: on to the nearest good spot
        if (notify && NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyClientText(player, "You lost them!",
                "The child couldn't keep up and is waiting where they stopped. Go back for them.");
    }

    private bool TryGetPlayer(ulong clientId, out Vector3 position)
    {
        position = default;
        if (clientId == ulong.MaxValue || !NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ||
            client.PlayerObject == null) return false;
        position = client.PlayerObject.transform.position;
        return true;
    }

    /* ========================= BACK TOGETHER ========================= */

    // Close enough to someone of their family: together again. Helper: the player who brought them.
    private bool TryReunite(Customer kid, ulong helper)
    {
        var parent = FamilyOf(kid);
        if (parent == null) return false;
        Customer near = null;
        if (kid.group != null)
            foreach (var m in kid.group.members)
                if (m != kid && !m.child && byId.ContainsKey(m.id) && FlatDistance(m.pos, kid.pos) <= reunionDistance) { near = m; break; }
        if (near == null && FlatDistance(parent.pos, kid.pos) <= reunionDistance) near = parent;
        if (near == null) return false;

        kid.follow = parent;
        kid.lostFrom = null;
        kid.helper = ulong.MaxValue;
        kid.mode = Mode.Follow;
        Act(kid, AnimCue.Lost, false);
        kid.arrived = true;
        kid.nextThink = Time.time;
        SetMark(kid, LostMark.None);
        CaseRpc(kid.id, 0, false);

        var root = Root(kid);
        if (root.searchingFor == kid) StopSearching(root);
        React(near, Reaction.Reunited);
        Log(kid, helper != ulong.MaxValue ? $"brought back to their family by player {helper}" : "found their family");

        if (helper == ulong.MaxValue) return true;
        // A player brought them back: a happier family, and thanks.
        if (kid.group != null)
            foreach (var m in kid.group.members)
                if (!m.child) m.satisfaction = Mathf.Min(1f, m.satisfaction + reunionHappiness);
        if (HelpPoints.Instance != null) HelpPoints.Instance.ServerAward(helper, HelpPoints.HelpAction.FoundLostChild);
        ulong team = MallManager.GetTeamOf(helper);
        if (reunionXp > 0f && ProgressionManager.Instance != null) ProgressionManager.Instance.ServerAddXp(team, reunionXp);
        if (NotificationManager.Instance != null)
            NotificationManager.Instance.ServerNotifyClientText(helper, "Family reunited!",
                reunionXp > 0f ? $"You brought the child back to their family: +{reunionXp:0} XP." : "You brought the child back to their family.");
        return true;
    }

    /* ========================= SEARCHING ========================= */

    // The family walks around where the child was lost (the rest of the party follows the leader).
    private void ThinkSearching(Customer root)
    {
        var kid = root.searchingFor;
        if (kid == null || !byId.ContainsKey(kid.id) || (kid.mode != Mode.Lost && kid.mode != Mode.WithHelper))
        {
            StopSearching(root);
            return;
        }
        if (!root.arrived) return;
        if (Random.value < 0.15f) React(root, Reaction.LostChild);
        if (NearbyHallwaySpot(kid.lostAt, 0f, searchRadius, out var spot)) PathTo(root, spot);
        root.nextThink = Time.time + Random.Range(1.5f, 4f); // a look around before moving on
    }

    private void StopSearching(Customer root)
    {
        root.searchingFor = null;
        MarkParty(root, LostMark.None);
        root.mode = Mode.Wander;
        root.arrived = true;
        root.nextThink = Time.time;
        PickGoal(root);
    }

    /* ========================= MARKS ========================= */

    // Everyone in a party (the adults, and children with them) gets the mark.
    private void MarkParty(Customer root, LostMark mark)
    {
        if (root.group == null) { SetMark(root, mark); return; }
        foreach (var m in root.group.members)
            if (Root(m) == root && m.mode != Mode.Lost && m.mode != Mode.WithHelper) SetMark(m, mark);
    }

    private void SetMark(Customer c, LostMark mark)
    {
        if (c.mark == (byte)mark) return;
        c.mark = (byte)mark;
        MarkRpc(c.id, c.mark);
    }

    [Rpc(SendTo.Everyone, AllowTargetOverride = true)] // (a late joiner gets just theirs)
    private void MarkRpc(ushort id, byte mark, RpcParams rpcParams = default)
    {
        if (mark == 0) marks.Remove(id); else marks[id] = mark;
    }

    /// <summary>Every machine: a customer's mark (for the interact prompt).</summary>
    public LostMark MarkOf(ushort id) => marks.TryGetValue(id, out byte m) ? (LostMark)m : LostMark.None;

    /* ========================= LOST & FOUND (every machine) ========================= */

    /// <summary>A lost child and the family looking for them (Family: the one leading the party).</summary>
    public struct LostCase
    {
        public ushort kid, family;
        public float since; // Time.time it came in, on this machine
    }

    private readonly System.Collections.Generic.List<LostCase> cases = new();

    /// <summary>Every machine: the lost children right now, oldest first.</summary>
    public System.Collections.Generic.IReadOnlyList<LostCase> LostCases => cases;
    /// <summary>Every machine: the list changed.</summary>
    public static event System.Action LostCasesChanged;
    /// <summary>Every machine: a child just got lost (not sent for ones lost before you joined).</summary>
    public static event System.Action<LostCase> LostCaseOpened;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetLostEvents()
    {
        LostCasesChanged = null;
        LostCaseOpened = null;
    }

    // Open: family is who leads their party. Closed: just the kid. Fresh: just now (not a late joiner's catch-up).
    [Rpc(SendTo.Everyone, AllowTargetOverride = true)]
    private void CaseRpc(ushort kid, ushort family, bool open, bool fresh = true, RpcParams rpcParams = default)
    {
        int at = cases.FindIndex(x => x.kid == kid);
        if (!open)
        {
            if (at < 0) return;
            cases.RemoveAt(at);
            LostCasesChanged?.Invoke();
            return;
        }
        var lost = new LostCase { kid = kid, family = family, since = Time.time };
        if (at >= 0) cases[at] = lost; else cases.Add(lost);
        LostCasesChanged?.Invoke();
        if (fresh) LostCaseOpened?.Invoke(lost);
    }

    // Host: the open cases, for a player who just joined.
    private void SendCases(ulong clientId)
    {
        foreach (var c in customers)
            if (c.mode == Mode.Lost || c.mode == Mode.WithHelper)
            {
                var family = FamilyOf(c);
                if (family != null) CaseRpc(c.id, Root(family).id, true, false, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            }
    }

    // A customer's view went away (they left): their case goes with them.
    private void ForgetCasesOf(ushort id)
    {
        if (cases.RemoveAll(x => x.kid == id || x.family == id) > 0) LostCasesChanged?.Invoke();
    }

    /// <summary>Every machine: where a customer is shown (their feet), and whether they're a child.</summary>
    public bool TryGetShown(ushort id, out Vector3 feet, out bool child)
    {
        feet = default;
        child = false;
        if (!views.TryGetValue(id, out var v) || v == null) return false;
        feet = v.Feet;
        child = v.IsChild;
        return true;
    }
}
