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
    Crouching,       // player (Bool): crouched (kept up to date on every machine)
    Sitting,         // customer (Bool): sitting on a bench (a Seat MallSpot)
    Talking,         // customer (Bool): a group stopped in the hallway chatting
    PushingStroller, // customer (Bool): a parent pushing a stroller
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
    [Tooltip("Also set, when the Animator has them: \"MoveX\" (m/s sideways, right +), \"MoveZ\" (m/s forward, back -) " +
             "for a 2D strafe blend, and \"Turn\" (degrees/s turning, right +) for turn-in-place clips.")]
    [SerializeField] private bool setDirection = true;

    [Header("Head look (players, Humanoid)")]
    [Tooltip("The head (and a little of the chest) turns to where the player's looking: up, down, and side to side " +
             "when free-looking. Needs a Humanoid avatar and IK Pass ticked on the base layer in the Animator.")]
    [SerializeField] private bool headLook = true;
    [SerializeField, Range(0f, 1f)] private float headWeight = 1f;
    [SerializeField, Range(0f, 1f)] private float bodyWeight = 0.25f;
    [Tooltip("How far it can turn (0 = not at all, 1 = as far as the look goes).")]
    [SerializeField, Range(0f, 1f)] private float clamp = 0.6f;

    public float HeadWeight => headWeight;
    public float BodyWeight => bodyWeight;
    public float Clamp => clamp;

    private readonly Dictionary<AnimCue, (int hash, AnimatorControllerParameterType type)> map = new();
    private bool hasSpeed;
    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int MoveXId = Animator.StringToHash("MoveX"), MoveZId = Animator.StringToHash("MoveZ"), TurnId = Animator.StringToHash("Turn");
    private bool hasMoveX, hasMoveZ, hasTurn;
    private float moveX, moveZ, turn, lastYaw;
    private Vector3 lastPos;
    private float speed;
    private PlayerInventoryNet inventory; // a player: its synced states
    private PlayerController3D controller; // ...and its crouch
    private bool[] states;

    private void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        inventory = GetComponentInParent<PlayerInventoryNet>();
        controller = GetComponentInParent<PlayerController3D>();
        lastYaw = transform.eulerAngles.y;
        // Head look runs in the Animator's IK pass, on the Animator's own object.
        if (headLook && controller != null && animator != null && animator.isHuman && !animator.TryGetComponent<HeadLookIK>(out _))
            animator.gameObject.AddComponent<HeadLookIK>().Set(this);
        states = new bool[Enum.GetValues(typeof(AnimCue)).Length];
        Map();
        lastPos = transform.position;
    }

    private void Map()
    {
        map.Clear();
        hasSpeed = false;
        hasMoveX = hasMoveZ = hasTurn = false;
        if (animator == null || animator.runtimeAnimatorController == null) return;
        var types = new Dictionary<int, AnimatorControllerParameterType>();
        foreach (var p in animator.parameters)
        {
            types[p.nameHash] = p.type;
            if (p.nameHash == SpeedId && p.type == AnimatorControllerParameterType.Float) hasSpeed = setSpeed;
            if (p.type == AnimatorControllerParameterType.Float && setDirection)
            {
                if (p.nameHash == MoveXId) hasMoveX = true;
                if (p.nameHash == MoveZId) hasMoveZ = true;
                if (p.nameHash == TurnId) hasTurn = true;
            }
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
        if ((hasMoveX || hasMoveZ || hasTurn) && dt > 0f && animator != null)
        {
            // Movement in its own frame (right +, forward +), and how fast it's turning.
            Vector3 d = transform.position - lastPos;
            d.y = 0f;
            Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, transform.eulerAngles.y, 0f)) * (d / dt);
            if (local.magnitude > 20f) local = Vector3.zero; // a teleport
            float k = 1f - Mathf.Exp(-10f * dt);
            moveX = Mathf.Lerp(moveX, local.x, k);
            moveZ = Mathf.Lerp(moveZ, local.z, k);
            float yaw = transform.eulerAngles.y;
            float rate = Mathf.DeltaAngle(lastYaw, yaw) / dt;
            lastYaw = yaw;
            turn = Mathf.Lerp(turn, Mathf.Clamp(rate, -720f, 720f), k);
            if (hasMoveX) animator.SetFloat(MoveXId, moveX);
            if (hasMoveZ) animator.SetFloat(MoveZId, moveZ);
            if (hasTurn) animator.SetFloat(TurnId, turn);
        }
        lastPos = transform.position;

        // A player: states from what's synced.
        if (inventory != null && inventory.IsSpawned)
        {
            int held = inventory.HeldItemId.Value;
            SetState(AnimCue.Holding, held != 0);
            if (controller != null) SetState(AnimCue.Crouching, controller.IsCrouching);
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

/// <summary>
/// Added by AnimCues to a player's Animator: turns the head (and a little of the chest) to where the
/// player's looking, from their synced look angles. Needs IK Pass on the Animator's base layer.
/// </summary>
public class HeadLookIK : MonoBehaviour
{
    private AnimCues cues;
    private Animator animator;
    private PlayerController3D controller;
    private float weight;

    public void Set(AnimCues owner) => cues = owner;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponentInParent<PlayerController3D>();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || cues == null || controller == null || animator == null) return;
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return;
        weight = Mathf.MoveTowards(weight, 1f, Time.deltaTime * 4f); // faded in (no snap on spawn)
        Quaternion look = Quaternion.Euler(0f, controller.transform.eulerAngles.y, 0f) *
                          Quaternion.Euler(controller.LookPitch, controller.LookYaw, 0f);
        animator.SetLookAtWeight(weight, cues.BodyWeight, cues.HeadWeight, 0f, cues.Clamp);
        animator.SetLookAtPosition(head.position + look * Vector3.forward * 5f);
    }
}
