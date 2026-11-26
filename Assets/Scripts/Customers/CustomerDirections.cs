using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Customers asking for directions. Now and then a shopper between stores walks up to a nearby player,
/// says "Excuse me!" and waits. F on them: they ask, either for a store by name ("Where's Just Dirt?")
/// or for something to buy ("Where can I find shoes?"). The player then aims at the right store's front
/// (from wherever they can see it) and clicks; right-click: "Sorry, I don't know".
/// - Right (that store, or any open store selling that): a help point, a happy customer who heads there.
/// - Wrong: an annoyed customer (less satisfied).
/// - Not asked or not answered in time: they give up and walk on.
/// </summary>
public partial class CustomerManager
{
    [Header("Asking for directions")]
    [Tooltip("The chance a shopper between stores goes to ask a nearby player the way (each time they pick where to go next).")]
    [SerializeField, Range(0f, 1f)] private float askDirectionsChance = 0.04f;
    [Tooltip("How near (meters, same floor) a player has to be for them to come and ask.")]
    [SerializeField, Min(1f)] private float askRange = 14f;
    [Tooltip("Most customers asking at once in the whole mall.")]
    [SerializeField, Min(0)] private int maxAskingAtOnce = 3;
    [Tooltip("Seconds they wait by the player for F before giving up.")]
    [SerializeField, Min(3f)] private float askWaitSeconds = 25f;
    [Tooltip("Seconds the player has to point out the store once asked.")]
    [SerializeField, Min(3f)] private float answerSeconds = 30f;
    [Tooltip("The chance they ask for a kind of thing (shoes) rather than a store by name.")]
    [SerializeField, Range(0f, 1f)] private float askForKindChance = 0.5f;
    [Tooltip("Categories they never ask for (shop-only things like shelves and decor).")]
    [SerializeField] private List<string> neverAskFor = new() { "Shelves", "Decor", "Wall", "Walls", "Counters", "Fixtures" };

    private sealed class Question
    {
        public ulong player;        // who they came to ask
        public ulong askedBy = ulong.MaxValue; // who pressed F (answers it)
        public StorePlot store;     // "Where's ...?" (null: a kind of thing)
        public string kind;         // "Where can I find ...?"
        public string text;
    }

    private readonly Dictionary<ushort, Question> questions = new();

    // Between stores: maybe go and ask the nearest player the way. True if they're off to ask.
    private bool TryAskDirections(Customer c)
    {
        if (askDirectionsChance <= 0f || c.child || c.thief || c.group != null || c.courier || questions.Count >= maxAskingAtOnce) return false;
        if (Random.value >= askDirectionsChance || MallManager.Instance == null) return false;
        // The nearest player on this floor, not already being asked.
        ulong best = ulong.MaxValue;
        float bestD = askRange * askRange;
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            var p = client.PlayerObject;
            if (p == null) continue;
            Vector3 d = p.transform.position - c.pos;
            if (Mathf.Abs(d.y) > 2f) continue;
            d.y = 0f;
            if (d.sqrMagnitude >= bestD) continue;
            bool busy = false;
            foreach (var q in questions.Values) if (q.player == client.ClientId) { busy = true; break; }
            if (busy) continue;
            bestD = d.sqrMagnitude;
            best = client.ClientId;
        }
        if (best == ulong.MaxValue) return false;
        var question = MakeQuestion(c);
        if (question == null) return false;
        question.player = best;
        questions[c.id] = question;
        c.mode = Mode.Asking;
        c.until = Time.time + askWaitSeconds;
        c.goalStore = null;
        SetMark(c, LostMark.Asking);
        React(c, Reaction.ExcuseMe);
        Log(c, $"going to ask client {best}: {question.text}");
        c.nextRepath = 0f;
        return true;
    }

    // A store by name, or a kind of thing some open store sells.
    private Question MakeQuestion(Customer c)
    {
        var open = new List<StorePlot>();
        foreach (var plot in MallManager.Instance.Plots)
            if (plot != null && plot.IsOpen && entrances.ContainsKey(plot)) open.Add(plot);
        if (open.Count == 0) return null;
        if (Random.value < askForKindChance)
        {
            var kinds = new List<string>();
            foreach (var plot in open)
            {
                if (plot.Listing == null) continue;
                foreach (int itemId in plot.Listing.ProductIds)
                    if (ItemCatalog.Instance != null && ItemCatalog.Instance.TryGet(itemId, out var def))
                        foreach (var cat in def.Categories)
                            if (!kinds.Contains(cat) && !neverAskFor.Exists(n => string.Equals(n, cat, System.StringComparison.OrdinalIgnoreCase)))
                                kinds.Add(cat);
            }
            if (kinds.Count > 0)
            {
                string kind = kinds[Random.Range(0, kinds.Count)];
                return new Question { kind = kind, text = $"Excuse me, where can I find {kind.ToLowerInvariant()}?" };
            }
        }
        var store = open[Random.Range(0, open.Count)];
        return new Question { store = store, text = $"Excuse me, where's {store.DisplayName}?" };
    }

    private void ThinkAsking(Customer c)
    {
        if (!questions.TryGetValue(c.id, out var q)) { StopAsking(c, null); return; }
        // Not asked (or not answered) in time: never mind.
        if (Time.time >= c.until)
        {
            Log(c, q.askedBy == ulong.MaxValue ? "nobody stopped to help: walking on" : "no answer: walking on");
            StopAsking(c, Reaction.NeverMind);
            return;
        }
        if (q.askedBy != ulong.MaxValue) { FacePlayer(c, q.askedBy); return; } // listening for the answer
        // Over to the player, stopping a couple of meters short; then waiting, facing them.
        var player = PlayerOf(q.player);
        if (player == null) { StopAsking(c, null); return; }
        Vector3 at = player.transform.position;
        if (FlatDistance(c.pos, at) > 2.2f)
        {
            if (Time.time >= c.nextRepath || c.arrived)
            {
                c.nextRepath = Time.time + 1.5f;
                Vector3 toward = c.pos - at;
                toward.y = 0f;
                Vector3 stop = at + (toward.sqrMagnitude > 1e-4f ? toward.normalized : Vector3.forward) * 1.6f;
                PathTo(c, stop);
            }
            return;
        }
        if (!c.arrived) { c.corners.Clear(); c.corner = 0; c.arrived = true; }
        FacePlayer(c, q.player);
    }

    private void FacePlayer(Customer c, ulong clientId)
    {
        var p = PlayerOf(clientId);
        if (p != null) Face(c, p.transform.position);
    }

    private NetworkObject PlayerOf(ulong clientId) =>
        NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ? client.PlayerObject : null;

    // F on someone waiting to ask: the question, to that player.
    private bool HearQuestion(Customer c, ulong clientId)
    {
        if (c.mode != Mode.Asking || !questions.TryGetValue(c.id, out var q)) return false;
        if (q.askedBy != ulong.MaxValue) return true; // already asked someone
        q.askedBy = clientId;
        c.until = Time.time + answerSeconds;
        if (!c.arrived) { c.corners.Clear(); c.corner = 0; c.arrived = true; }
        FacePlayer(c, clientId);
        QuestionRpc(c.id, q.text, answerSeconds, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        Log(c, $"asked client {clientId}: {q.text}");
        return true;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void QuestionRpc(ushort id, string text, float seconds, RpcParams rpcParams = default) =>
        DirectionsAnswer.Begin(id, text, seconds);

    /// <summary>The local player's answer: the store they pointed at (-1: "I don't know").</summary>
    public void Answer(ushort customerId, int plotId) => AnswerRpc(customerId, plotId);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void AnswerRpc(ushort id, int plotId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!byId.TryGetValue(id, out var c) || !questions.TryGetValue(id, out var q) || q.askedBy != sender) return;
        StorePlot pointed = null;
        if (plotId >= 0 && MallManager.Instance != null) MallManager.Instance.TryGetPlot(plotId, out pointed);
        if (pointed == null)
        {
            Log(c, "they didn't know");
            StopAsking(c, Reaction.NeverMind);
            return;
        }
        bool right = q.store != null ? pointed == q.store
                   : pointed.IsOpen && SellsKind(pointed, q.kind);
        if (right)
        {
            Log(c, $"pointed the right way: '{pointed.DisplayName}'");
            c.satisfaction += 0.2f;
            if (HelpPoints.Instance != null) HelpPoints.Instance.ServerAward(sender, HelpPoints.HelpAction.GaveDirections);
            questions.Remove(id);
            SetMark(c, LostMark.None);
            React(c, Reaction.ThanksForDirections);
            // Off there.
            c.mode = Mode.Wander;
            if (pointed.IsOpen && entrances.TryGetValue(pointed, out var ways) && ways.Count > 0)
            {
                c.goalStore = pointed;
                c.goalEntrance = ways[0];
                c.mustEnter = pointed;
                if (PathTo(c, ways[0].outside)) return;
            }
            PickGoal(c);
            return;
        }
        Log(c, $"pointed the wrong way: '{pointed.DisplayName}'");
        c.satisfaction -= 0.35f;
        StopAsking(c, Reaction.WrongDirections);
    }

    private static bool SellsKind(StorePlot plot, string kind)
    {
        if (plot.Listing == null || string.IsNullOrEmpty(kind) || ItemCatalog.Instance == null) return false;
        foreach (int itemId in plot.Listing.ProductIds)
            if (ItemCatalog.Instance.TryGet(itemId, out var def) && def.HasCategory(kind)) return true;
        return false;
    }

    private void StopAsking(Customer c, Reaction? reaction)
    {
        if (questions.TryGetValue(c.id, out var q) && q.askedBy != ulong.MaxValue)
            QuestionDoneRpc(c.id, RpcTarget.Single(q.askedBy, RpcTargetUse.Temp));
        questions.Remove(c.id);
        SetMark(c, LostMark.None);
        if (reaction.HasValue) React(c, reaction.Value);
        c.mode = Mode.Wander;
        PickGoal(c);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void QuestionDoneRpc(ushort id, RpcParams rpcParams = default) => DirectionsAnswer.End(id);

    private void ForgetQuestion(Customer c) => questions.Remove(c.id);
}
