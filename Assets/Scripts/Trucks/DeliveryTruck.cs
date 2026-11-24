using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A delivery truck (see EconomyManager → Trucks). It backs into a TruckBay with the shipment inside its
/// Cargo Area, behind a rolling Door. The team it's for opens the door (F); the boxes are theirs to unload
/// until the truck leaves. Its comings and goings are driven by the host (EconomyManager); every machine
/// moves it the same way from the synced phase and start time (no NetworkTransform needed).
///
/// Setup (one prefab per size, small, medium, large): a NetworkObject (in the NetworkManager's prefab list)
/// with this on the root; the root's pivot at the back of the truck at floor level, the truck pointing
/// along +Z (it drives off forward); Cargo Area: a BoxCollider over the cargo floor (turned off at runtime);
/// Door: the rolling door (with a collider, so nothing's reachable while it's shut).
/// </summary>
public class DeliveryTruck : NetworkBehaviour, IPlayerUsable
{
    public enum Phase : byte { Arriving, Parked, Leaving }

    public static readonly List<DeliveryTruck> All = new();

    [Tooltip("A BoxCollider over the cargo floor: the shipment is set down in it.")]
    public BoxCollider cargoArea;
    [Tooltip("The rolling door. It moves by Door Open Offset (its own space) to open.")]
    [SerializeField] private Transform door;
    [SerializeField] private Vector3 doorOpenOffset = new(0f, 2.4f, 0f);
    [SerializeField, Min(0.05f)] private float doorSeconds = 0.8f;
    [Tooltip("How far it drives in and out (meters), and how long that takes.")]
    [SerializeField, Min(0f)] private float driveDistance = 12f;
    [SerializeField, Min(0.1f)] private float driveSeconds = 3f;

    private readonly NetworkVariable<int> orderId = new();
    private readonly NetworkVariable<ulong> teamId = new();
    private readonly NetworkVariable<byte> phase = new();
    private readonly NetworkVariable<double> phaseStart = new();
    private readonly NetworkVariable<bool> doorOpen = new();
    private readonly NetworkVariable<int> bay = new(-1);
    private readonly NetworkVariable<Vector3> parkedAt = new();      // where it parks (for anyone joining mid-drive)
    private readonly NetworkVariable<Quaternion> parkedFacing = new(Quaternion.identity);

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
    }

    public override void OnNetworkDespawn() => All.Remove(this);

    /// <summary>Host: set up a new truck for an order (right after spawning it at its bay).</summary>
    public void ServerSetup(int order, ulong team, int bayIndex)
    {
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
        if (cargoArea == null) return false;
        Vector3 p = cargoArea.transform.InverseTransformPoint(world) - cargoArea.center;
        Vector3 h = cargoArea.size * 0.5f + new Vector3(0.05f, 1.5f, 0.05f); // a bit of room, and anything stacked
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
        transform.SetPositionAndRotation(pos, parkedRot);

        if (door != null)
        {
            doorT = Mathf.MoveTowards(doorT, doorOpen.Value ? 1f : 0f, Time.deltaTime / doorSeconds);
            door.localPosition = doorClosedPos + doorOpenOffset * doorT;
        }
    }

    /* ========================= OPENING (F) ========================= */

    public string Prompt(ulong clientId)
    {
        if (!IsSpawned || doorOpen.Value || CurrentPhase != Phase.Parked) return null;
        return MallManager.GetTeamOf(clientId) == teamId.Value ? "F: Open the truck" : null;
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
