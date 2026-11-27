using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Every animation the game asks for. Mapped to Animator parameters in AnimCues.</summary>
public enum AnimCue
{
    None = 0,
    // Player: moments (Triggers)
    PickUp, PutDown, Stock, TakeStock, Paint, Scan, PriceScan,
    // Player: states (Bools), kept up to date on every machine
    Holding, CarryingCrate, PushingCart, UsingPhone,
    // Customers: moments (Triggers)
    Browse, TakeItem, Pay, ReactHappy, ReactUnhappy,
    // Customers: states (Bools)
    Running, Lost,
    // Added later (kept at the end so filled-in slots keep their place)
    Asking,          // customer (Bool): waiting by a player to ask the way (a wave, a hand up)
    CarryingPackage, // customer (Bool): the delivery guy holding the package in both hands
    HandOver,        // customer (Trigger): the delivery guy handing the package over
    Riding,          // customer (Bool): standing on an escalator
    Point,           // player (Trigger): pointing a customer the way
    HoldingTool,     // player (Bool): the paint brush or price scanner out
}

/// <summary>
/// Animation hooks: put it on the player prefab, or a customer look prefab, next to (or above) its
/// Animator. Each cue has a slot with the Animator parameter it sets: a Trigger fires for moments
/// (Stock, Paint, Pay...), a Bool is set for states (Holding, Pushing Cart, Running...). The parameter's
/// type in the Animator decides which. Left empty, or not in the Animator: nothing happens, so models
/// without those animations are fine. Walking: a float "Speed" (m/s) is set for you when the Animator
/// has one.
///
/// On a player it also keeps Holding, Carrying Crate and Pushing Cart up to date on every machine from
/// what's already synced (what they hold, the cart they push). Other cues come from the game
/// (Sfx.ForAll for players, CustomerManager for customers).
/// </summary>
public class AnimCues : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        [HideInInspector] public string name;
        public AnimCue cue;
        [Tooltip("The Animator parameter (Trigger or Bool). Empty: nothing.")]
        public string parameter;
    }

    [SerializeField] private Animator animator;
    [SerializeField] private Slot[] slots = Array.Empty<Slot>();
    [Tooltip("Set a float \"Speed\" (m/s) from how it moves, when the Animator has one (customers already do this).")]
    [SerializeField] private bool setSpeed = true;

    private readonly Dictionary<AnimCue, (int hash, AnimatorControllerParameterType type)> map = new();
    private bool hasSpeed;
    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private Vector3 lastPos;
    private float speed;
    private PlayerInventoryNet inventory; // a player: its synced states
    private bool[] states;

    private void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        inventory = GetComponentInParent<PlayerInventoryNet>();
        states = new bool[Enum.GetValues(typeof(AnimCue)).Length];
        Map();
        lastPos = transform.position;
    }

    private void Map()
    {
        map.Clear();
        hasSpeed = false;
        if (animator == null || animator.runtimeAnimatorController == null) return;
        var types = new Dictionary<int, AnimatorControllerParameterType>();
        foreach (var p in animator.parameters)
        {
            types[p.nameHash] = p.type;
            if (p.nameHash == SpeedId && p.type == AnimatorControllerParameterType.Float) hasSpeed = setSpeed;
        }
        foreach (var s in slots)
        {
            if (s == null || s.cue == AnimCue.None || string.IsNullOrWhiteSpace(s.parameter)) continue;
            int h = Animator.StringToHash(s.parameter.Trim());
            if (types.TryGetValue(h, out var t)) map[s.cue] = (h, t);
        }
    }

    // One slot per cue (the parameter defaults to the cue's name), keeping what's filled in.
    private void OnValidate()
    {
        var old = new Dictionary<AnimCue, Slot>();
        if (slots != null) foreach (var s in slots) if (s != null && !old.ContainsKey(s.cue)) old[s.cue] = s;
        var list = new List<Slot>();
        foreach (AnimCue c in Enum.GetValues(typeof(AnimCue)))
        {
            if (c == AnimCue.None) continue;
            if (!old.TryGetValue(c, out var s)) s = new Slot { cue = c, parameter = c.ToString() };
            s.name = c.ToString();
            list.Add(s);
        }
        slots = list.ToArray();
    }

    /// <summary>A moment (Trigger) or a state turned on (Bool).</summary>
    public void Fire(AnimCue cue, bool on = true)
    {
        if (cue == AnimCue.None) return;
        if ((int)cue < states.Length) states[(int)cue] = on;
        if (animator == null || !map.TryGetValue(cue, out var p)) return;
        switch (p.type)
        {
            case AnimatorControllerParameterType.Trigger: if (on) animator.SetTrigger(p.hash); break;
            case AnimatorControllerParameterType.Bool: animator.SetBool(p.hash, on); break;
            case AnimatorControllerParameterType.Float: animator.SetFloat(p.hash, on ? 1f : 0f); break;
            case AnimatorControllerParameterType.Int: animator.SetInteger(p.hash, on ? 1 : 0); break;
        }
    }

    /// <summary>The AnimCues on (or above, or under) an object, if any: fire a cue on it.</summary>
    public static void Fire(GameObject on, AnimCue cue, bool state = true)
    {
        if (on == null || cue == AnimCue.None) return;
        var a = on.GetComponentInChildren<AnimCues>();
        if (a == null) a = on.GetComponentInParent<AnimCues>();
        if (a != null) a.Fire(cue, state);
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (hasSpeed && dt > 0f)
        {
            Vector3 d = transform.position - lastPos;
            d.y = 0f;
            float now = d.magnitude / dt;
            if (now > 20f) now = 0f; // a teleport, not walking
            speed = Mathf.Lerp(speed, now, 1f - Mathf.Exp(-10f * dt));
            animator.SetFloat(SpeedId, speed);
        }
        lastPos = transform.position;

        // A player: states from what's synced.
        if (inventory != null && inventory.IsSpawned)
        {
            int held = inventory.HeldItemId.Value;
            SetState(AnimCue.Holding, held != 0);
            SetState(AnimCue.CarryingCrate, held != 0 && StockContainer.IsStockCrate(held));
            SetState(AnimCue.PushingCart, CartNet.GetPushedBy(inventory.OwnerClientId) != null);
        }
    }

    private void SetState(AnimCue cue, bool on)
    {
        if (states[(int)cue] == on) return;
        Fire(cue, on);
    }
}
