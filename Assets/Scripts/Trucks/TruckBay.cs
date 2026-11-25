using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where a delivery truck parks at a loading dock. Put it on the dock floor where the back of the truck
/// should stop, its blue arrow (Z) pointing out of the dock (the way the truck drives off). It belongs to the
/// LoadingDockZone it's in (Dock, if not set); any store whose shipment spots are in that dock can have its
/// trucks park here (one truck per store at a time).
///
/// Door (optional): the dock's rolling door for this bay (a plain model with a collider, no NetworkObject or
/// DoorNet). Shut until the truck parked here is opened: its team presses F on the door (or the truck), it
/// rolls up by Door Open Offset (its own space), and comes down when the truck leaves. Door sounds optional.
/// </summary>
public class TruckBay : MonoBehaviour, IPlayerUsable
{
    public static readonly List<TruckBay> All = new();

    [Header("Dock door (optional)")]
    [SerializeField] private Transform door;
    [SerializeField] private Vector3 doorOpenOffset = new(0f, 2.6f, 0f);
    [SerializeField, Min(0.05f)] private float doorSeconds = 1.5f;
    [Tooltip("Once as it starts to roll up, and as it starts to come down.")]
    [SerializeField] private AudioClip doorOpenSound, doorCloseSound;
    [Tooltip("Plays while it's moving (the rattle).")]
    [SerializeField] private AudioClip doorMovingLoop;
    [SerializeField, Range(0f, 1f)] private float doorVolume = 0.8f;

    private Vector3 doorClosedPos;
    private float doorT;
    private bool doorWasOpen;
    private AudioSource doorAudio, doorLoop;

    private void Awake()
    {
        if (door == null) return;
        doorClosedPos = door.localPosition;
        // F on the door itself (it may not be under the bay).
        if (!door.IsChildOf(transform) && !door.TryGetComponent<TruckBayDoor>(out _)) door.gameObject.AddComponent<TruckBayDoor>().bay = this;
    }

    /// <summary>The truck parked at (or driving in or out of) this bay, if any.</summary>
    public DeliveryTruck Truck
    {
        get
        {
            int i = Index;
            foreach (var t in DeliveryTruck.All) if (t != null && t.BayIndex == i) return t;
            return null;
        }
    }

    public string Prompt(ulong clientId) { var t = Truck; return t != null ? t.Prompt(clientId) : null; }

    public void Use(ulong clientId) { var t = Truck; if (t != null) t.Use(clientId); }

    // Every machine moves the door from the truck's synced door state (no networking of its own).
    private void Update()
    {
        if (door == null) return;
        var t = Truck;
        bool open = t != null && t.DoorOpen;
        if (open != doorWasOpen)
        {
            doorWasOpen = open;
            var clip = open ? doorOpenSound : doorCloseSound;
            if (clip != null) Source(ref doorAudio, "Dock Door", null).PlayOneShot(clip, doorVolume);
        }
        float was = doorT;
        doorT = Mathf.MoveTowards(doorT, open ? 1f : 0f, Time.deltaTime / doorSeconds);
        door.localPosition = doorClosedPos + doorOpenOffset * doorT;
        if (doorMovingLoop != null)
        {
            bool moving = !Mathf.Approximately(was, doorT);
            var loop = Source(ref doorLoop, "Dock Door Rattle", doorMovingLoop);
            if (moving && !loop.isPlaying) loop.Play();
            else if (!moving && loop.isPlaying) loop.Stop();
        }
    }

    private AudioSource Source(ref AudioSource src, string label, AudioClip loop)
    {
        if (src != null) return src;
        var go = new GameObject(label);
        go.transform.SetParent(door, false);
        src = go.AddComponent<AudioSource>();
        ProgramZer0.SoundSystem.WorldAudio.ConfigureSource(src);
        src.playOnAwake = false;
        src.volume = doorVolume;
        if (loop != null) { src.clip = loop; src.loop = true; }
        return src;
    }

    [Tooltip("The dock this bay serves. Empty: the LoadingDockZone it stands in.")]
    [SerializeField] private LoadingDockZone dock;

    public LoadingDockZone Dock => dock != null ? dock : dock = LoadingDockZone.GetDockAt(transform.position + Vector3.up * 0.1f);

    /// <summary>Its place in All (the same on every machine: sorted by name, then position).</summary>
    public int Index => All.IndexOf(this);

    private void OnEnable()
    {
        All.Add(this);
        All.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.name, b.name);
            return c != 0 ? c : a.transform.position.sqrMagnitude.CompareTo(b.transform.position.sqrMagnitude);
        });
    }

    private void OnDisable() => All.Remove(this);

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(new Vector3(0f, 1.5f, 4f), new Vector3(2.6f, 3f, 8f)); // about where a truck goes
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 2f);
    }
}
