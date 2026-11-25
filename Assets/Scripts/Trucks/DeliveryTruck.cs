using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A delivery truck (see EconomyManager → Trucks). It backs into a TruckBay with the shipment inside its
/// Cargo Area. The team it's for opens the dock door (TruckBay → Door) with F, on the door or the truck; the
/// boxes are theirs to unload
/// until the truck leaves. Its comings and goings are driven by the host (EconomyManager); every machine
/// moves it the same way from the synced phase and start time (no NetworkTransform needed).
///
/// Setup (one prefab per size, small, medium, large): a NetworkObject (in the NetworkManager's prefab list)
/// with this on the root; the root's pivot at the back of the truck at floor level, the truck pointing
/// along +Z (it drives off forward). The root's own rotation in the prefab is kept on top of the bay's
/// (a Blender model's -90° X stays standing; facing backwards: add 180 to its Y). No other NetworkObjects
/// inside it (a door prefab's): a spawned prefab can't have them. Cargo Area: a BoxCollider over the cargo floor (turned off at runtime);
/// Door (optional, usually empty now the door's on the dock): a door on the truck itself, opened with the dock's.
/// Sounds (optional): Engine Loop while it drives in and out, Reversing Loop (beeping) while it backs in,
/// Park Sound when it stops (air brakes), Depart Sound as it pulls away.
/// </summary>
public class DeliveryTruck : NetworkBehaviour, IPlayerUsable
{
    public enum Phase : byte { Arriving, Parked, Leaving }

    public static readonly List<DeliveryTruck> All = new();

    [Tooltip("A BoxCollider over the cargo floor: the shipment is set down in it.")]
    public BoxCollider cargoArea;
    [Tooltip("Optional: a door on the truck itself (the dock's door is on its TruckBay). It moves by Door Open " +
             "Offset (its own space) to open, along with the dock's.")]
    [SerializeField] private Transform door;
    [SerializeField] private Vector3 doorOpenOffset = new(0f, 2.4f, 0f);
    [SerializeField, Min(0.05f)] private float doorSeconds = 0.8f;
    [Tooltip("How far it drives in and out (meters), and how long that takes.")]
    [SerializeField, Min(0f)] private float driveDistance = 12f;
    [SerializeField, Min(0.1f)] private float driveSeconds = 3f;

    [Header("Sounds (optional)")]
    [Tooltip("Plays while it drives in and while it drives off.")]
    [SerializeField] private AudioClip engineLoop;
    [Tooltip("Plays (with the engine) while it backs into the bay: the beeping.")]
    [SerializeField] private AudioClip reversingLoop;
    [Tooltip("Once, as it stops at the bay (air brakes, engine off).")]
    [SerializeField] private AudioClip parkSound;
    [Tooltip("Once, as it starts to pull away.")]
    [SerializeField] private AudioClip departSound;
    [SerializeField, Range(0f, 1f)] private float volume = 0.8f;

    private readonly NetworkVariable<int> orderId = new();
    private readonly NetworkVariable<ulong> teamId = new();
    private readonly NetworkVariable<byte> phase = new();
    private readonly NetworkVariable<double> phaseStart = new();
    private readonly NetworkVariable<bool> doorOpen = new();
    private readonly NetworkVariable<int> bay = new(-1);
    private readonly NetworkVariable<Vector3> parkedAt = new();      // where it parks (for anyone joining mid-drive)
    private readonly NetworkVariable<Quaternion> parkedFacing = new(Quaternion.identity);
    private readonly NetworkVariable<Quaternion> modelTurn = new(Quaternion.identity); // the prefab root's own turn

    /// <summary>
    /// The prefab root's own rotation (a Blender model's -90° X, say), kept on top of the way it faces, so
    /// the model stands up whatever its root is turned to. (On the prefab itself: its root's rotation.)
    /// </summary>
    public Quaternion ModelTurn => IsSpawned ? modelTurn.Value : transform.rotation;

    /// <summary>The way the truck faces (its drive-off direction along Z, up is up), without the model's turn.</summary>
    public Quaternion Facing => transform.rotation * Quaternion.Inverse(ModelTurn);

    /// <summary>
    /// The cargo space in the truck's facing frame: its middle, the frame (X across, Y up, Z along the
    /// truck) and its size along those (whichever way the Cargo Area box itself is turned).
    /// </summary>
    public bool CargoFrame(out Vector3 center, out Quaternion frame, out Vector3 size)
    {
        frame = Facing;
        center = default;
        size = default;
        if (cargoArea == null) return false;
        var t = cargoArea.transform;
        center = t.TransformPoint(cargoArea.center);
        Vector3 s = Vector3.Scale(cargoArea.size, t.lossyScale);
        Vector3 ax = t.right * s.x, ay = t.up * s.y, az = t.forward * s.z;
        Vector3 fx = frame * Vector3.right, fy = frame * Vector3.up, fz = frame * Vector3.forward;
        size = new Vector3(
            Mathf.Abs(Vector3.Dot(ax, fx)) + Mathf.Abs(Vector3.Dot(ay, fx)) + Mathf.Abs(Vector3.Dot(az, fx)),
            Mathf.Abs(Vector3.Dot(ax, fy)) + Mathf.Abs(Vector3.Dot(ay, fy)) + Mathf.Abs(Vector3.Dot(az, fy)),
            Mathf.Abs(Vector3.Dot(ax, fz)) + Mathf.Abs(Vector3.Dot(ay, fz)) + Mathf.Abs(Vector3.Dot(az, fz)));
        return true;
    }

    public int OrderId => orderId.Value;
    public ulong TeamId => teamId.Value;
    public Phase CurrentPhase => (Phase)phase.Value;
    public bool DoorOpen => doorOpen.Value;
    public int BayIndex => bay.Value;
    /// <summary>Done driving in (the shipment can go in).</summary>
    public bool Arrived => CurrentPhase == Phase.Parked || (CurrentPhase == Phase.Arriving && Progress >= 1f);
    /// <summary>Gone (driven off): the host despawns it.</summary>
    public bool Gone => CurrentPhase == Phase.Leaving && Progress >= 1f;

    private Vector3 parkedPos;
    private Quaternion parkedRot;
    private Vector3 doorClosedPos;
    private float doorT;

    private float Progress =>
        NetworkManager == null ? 1f : Mathf.Clamp01((float)(NetworkManager.ServerTime.Time - phaseStart.Value) / driveSeconds);

    private void Awake()
    {
        parkedPos = transform.position;
        parkedRot = transform.rotation;
        if (door != null) doorClosedPos = door.localPosition;
        if (cargoArea != null) cargoArea.enabled = false; // only its size and place matter
    }

    public override void OnNetworkSpawn()
    {
        parkedPos = transform.position; // spawned where it parks
        parkedRot = transform.rotation;
        All.Add(this);
        if (IsServer) CheckSetup();
    }

    // Two easy prefab mistakes, said plainly (once per truck kind).
    private static readonly HashSet<string> checkedKinds = new();

    private void CheckSetup()
    {
        string kind = name.Replace("(Clone)", "").Trim();
        if (!checkedKinds.Add(kind)) return;
        foreach (var child in GetComponentsInChildren<NetworkObject>(true))
            if (child != NetworkObject)
                Debug.LogError($"[DeliveryTruck] '{kind}' has another NetworkObject inside it, on '{child.name}' (a door " +
                               "prefab?). A spawned prefab can't have NetworkObjects inside it: remove the NetworkObject (and " +
                               "DoorNet / DoorMotion) from that child and keep just its model. The truck's own Door field opens it.", this);
    }

    public override void OnNetworkDespawn() => All.Remove(this);

    /// <summary>Host: set up a new truck for an order (right after spawning it at its bay).</summary>
    public void ServerSetup(int order, ulong team, int bayIndex, Quaternion facing, Quaternion prefabTurn)
    {
        parkedRot = facing;
        modelTurn.Value = prefabTurn;
        orderId.Value = order;
        teamId.Value = team;
        bay.Value = bayIndex;
        parkedAt.Value = parkedPos;
        parkedFacing.Value = parkedRot;
        phase.Value = (byte)Phase.Arriving;
        phaseStart.Value = NetworkManager.ServerTime.Time;
        doorOpen.Value = false;
    }

    public void ServerParked() { if (CurrentPhase == Phase.Arriving) phase.Value = (byte)Phase.Parked; }

    public void ServerSetDoor(bool open) => doorOpen.Value = open;

    public void ServerLeave()
    {
        doorOpen.Value = false;
        phase.Value = (byte)Phase.Leaving;
        phaseStart.Value = NetworkManager.ServerTime.Time;
    }

    /// <summary>Is this point in the cargo area (the shipment still on the truck)?</summary>
    public bool CargoContains(Vector3 world)
    {
        if (!CargoFrame(out var center, out var frame, out var size)) return false;
        Vector3 p = Quaternion.Inverse(frame) * (world - center);
        Vector3 h = size * 0.5f + new Vector3(0.05f, 1.5f, 0.05f); // a bit of room, and anything stacked
        return Mathf.Abs(p.x) <= h.x && Mathf.Abs(p.y) <= h.y && Mathf.Abs(p.z) <= h.z;
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (!IsServer && bay.Value >= 0) { parkedPos = parkedAt.Value; parkedRot = parkedFacing.Value; }
        // Backing in from out front, or driving off forward: the same on every machine.
        float t = Progress;
        float e = t * t * (3f - 2f * t);
        Vector3 forward = parkedRot * Vector3.forward;
        Vector3 pos = CurrentPhase switch
        {
            Phase.Arriving => parkedPos + forward * (driveDistance * (1f - e)),
            Phase.Leaving => parkedPos + forward * (driveDistance * e),
            _ => parkedPos,
        };
        transform.SetPositionAndRotation(pos, parkedRot * modelTurn.Value); // facing, with the model's own turn

        if (door != null)
        {
            doorT = Mathf.MoveTowards(doorT, doorOpen.Value ? 1f : 0f, Time.deltaTime / doorSeconds);
            door.localPosition = doorClosedPos + doorOpenOffset * doorT;
        }
        UpdateSounds(t);
    }

    /* ========================= SOUNDS ========================= */

    private AudioSource engine, beeps, once;
    private int heardPhase = -1;
    private bool heardStop;

    private AudioSource MakeSource(string label, AudioClip loop)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform, false);
        var src = go.AddComponent<AudioSource>();
        ProgramZer0.SoundSystem.WorldAudio.ConfigureSource(src); // 3D, falloff, mixer
        src.playOnAwake = false;
        if (loop != null) { src.clip = loop; src.loop = true; }
        src.volume = volume;
        return src;
    }

    // Each machine plays them from the synced phase (no sound messages needed).
    private void UpdateSounds(float t)
    {
        var p = CurrentPhase;
        bool driving = (p == Phase.Arriving || p == Phase.Leaving) && t < 1f;
        bool backing = p == Phase.Arriving && t < 1f;
        if (engineLoop != null)
        {
            engine ??= MakeSource("Engine", engineLoop);
            if (driving && !engine.isPlaying) engine.Play();
            else if (!driving && engine.isPlaying) engine.Stop();
        }
        if (reversingLoop != null)
        {
            beeps ??= MakeSource("Reversing", reversingLoop);
            if (backing && !beeps.isPlaying) beeps.Play();
            else if (!backing && beeps.isPlaying) beeps.Stop();
        }
        // Stopped at the bay (driven in all the way), and pulling away: once each.
        bool stopped = p == Phase.Parked || (p == Phase.Arriving && t >= 1f);
        if (stopped && !heardStop && heardPhase >= 0 && parkSound != null) (once ??= MakeSource("Truck Sounds", null)).PlayOneShot(parkSound, volume);
        if (p == Phase.Leaving && heardPhase != (int)Phase.Leaving && heardPhase >= 0 && departSound != null)
            (once ??= MakeSource("Truck Sounds", null)).PlayOneShot(departSound, volume);
        heardStop = stopped;
        heardPhase = (int)p;
    }

    /* ========================= OPENING (F) ========================= */

    public string Prompt(ulong clientId)
    {
        if (!IsSpawned || doorOpen.Value || CurrentPhase != Phase.Parked) return null;
        return MallManager.GetTeamOf(clientId) == teamId.Value ? "F: Open the dock door" : null;
    }

    public void Use(ulong clientId) => RequestOpenRpc();

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestOpenRpc(RpcParams rpcParams = default)
    {
        if (CurrentPhase != Phase.Parked || doorOpen.Value) return;
        if (MallManager.GetTeamOf(rpcParams.Receive.SenderClientId) != teamId.Value) return;
        doorOpen.Value = true;
        Sfx.ForAll(Cue.DeliveryArrived, transform.position);
    }
}
