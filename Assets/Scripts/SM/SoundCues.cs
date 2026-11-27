using System;
using System.Collections.Generic;
using ProgramZer0.SoundSystem;
using Unity.Netcode;
using UnityEngine;

/// <summary>Every game event that can make a sound. Clips go in SoundCues (one slot each).</summary>
public enum Cue
{
    None = 0,
    // Stock and items
    Stock, TakeStock, PickUp, PutDown, DeliveryArrived,
    // Checkout
    ScanBeep, KeypadBeep, TakeCard, TakeCash, CardApproved, DrawerOpen, DrawerClose, CountChange, SaleDone,
    // Doors and the store
    DoorOpen, DoorClose, DoorLock, DoorUnlock, StoreOpened, StoreClosed,
    // Tools
    Paint, PriceScan, PriceSet,
    // Phone and alerts (2D, this machine)
    PhoneUp, PhoneDown, Notification, HelpTip, LostChildAlert, LevelUp, MoneyIn, MoneyOut,
    // The mall
    Broke, BrokenFlicker, Reported, ShoplifterAlarm, CustomerHappy, CustomerUnhappy,
    // Added later (kept at the end so filled-in slots keep their place)
    PackageHandedOver, // the delivery guy hands a package over
    PointTheWay,       // a player points a customer the way (answering "where's...?")
}

/// <summary>
/// The game's sound slots: one per Cue (Stock, Scan Beep, Door Open...), filled in here. Empty slots
/// stay silent, so the game works with none. Put it on the scene object with the SoundManager.
/// - Clips: one is picked at random each time (never the same twice in a row).
/// - World: heard at its spot in the mall by anyone nearby (3D, WorldAudio's distances); off: a flat
///   sound on this machine (the phone, alerts, money).
/// - Min Gap: the same cue no sooner than this again (seconds), so a burst doesn't stack up.
/// Also Ambience: a loop that plays the whole time (mall chatter, air con), flat.
/// Volumes follow the SoundManager's sound setting and mute.
/// Code plays them with Sfx.Play (this machine) or Sfx.ForAll (everyone).
/// </summary>
public class SoundCues : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        [HideInInspector] public string name;
        public Cue cue;
        public AudioClip[] clips = Array.Empty<AudioClip>();
        [Range(0f, 1f)] public float volume = 0.7f;
        public bool world = true;
        [Min(0f)] public float minGap = 0.05f;
        [NonSerialized] public float lastAt = -10f;
        [NonSerialized] public int lastClip = -1;
    }

    public static SoundCues Instance { get; private set; }

    [SerializeField] private Slot[] slots = Array.Empty<Slot>();

    [Header("Ambience")]
    [SerializeField] private AudioClip ambience;
    [SerializeField, Range(0f, 1f)] private float ambienceVolume = 0.25f;

    private readonly Dictionary<Cue, Slot> byCue = new();
    private AudioSource flat, ambienceSource;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        Index();
        flat = gameObject.AddComponent<AudioSource>();
        flat.playOnAwake = false;
        flat.spatialBlend = 0f;
        if (ambience != null)
        {
            ambienceSource = gameObject.AddComponent<AudioSource>();
            ambienceSource.clip = ambience;
            ambienceSource.loop = true;
            ambienceSource.spatialBlend = 0f;
            ambienceSource.Play();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (ambienceSource != null) ambienceSource.volume = ambienceVolume * WorldAudio.VolumeScale;
    }

    private void Index()
    {
        byCue.Clear();
        foreach (var s in slots) if (s != null && s.cue != Cue.None) byCue[s.cue] = s;
    }

    // One slot per cue, in order, keeping what's filled in (new cues get a slot; flat ones start flat).
    private void OnValidate()
    {
        var old = new Dictionary<Cue, Slot>();
        if (slots != null) foreach (var s in slots) if (s != null && !old.ContainsKey(s.cue)) old[s.cue] = s;
        var list = new List<Slot>();
        foreach (Cue c in Enum.GetValues(typeof(Cue)))
        {
            if (c == Cue.None) continue;
            if (!old.TryGetValue(c, out var s)) s = new Slot { cue = c, world = !IsFlatByDefault(c) };
            s.name = c.ToString();
            list.Add(s);
        }
        slots = list.ToArray();
        Index();
    }

    private static bool IsFlatByDefault(Cue c) =>
        c is Cue.PhoneUp or Cue.PhoneDown or Cue.Notification or Cue.HelpTip or Cue.LostChildAlert or Cue.LevelUp
            or Cue.MoneyIn or Cue.MoneyOut;

    /// <summary>Play a cue on this machine: at a spot (world cues), or flat.</summary>
    public void Play(Cue cue, Vector3 at)
    {
        if (cue == Cue.None || !byCue.TryGetValue(cue, out var s) || s.clips == null || s.clips.Length == 0) return;
        if (Time.unscaledTime - s.lastAt < s.minGap) return;
        int i = s.clips.Length == 1 ? 0 : UnityEngine.Random.Range(0, s.clips.Length - 1);
        if (s.clips.Length > 1 && i >= s.lastClip && s.lastClip >= 0) i++; // never the same twice running
        var clip = s.clips[Mathf.Clamp(i, 0, s.clips.Length - 1)];
        if (clip == null) return;
        s.lastAt = Time.unscaledTime;
        s.lastClip = i;
        if (s.world) WorldAudio.PlayOneShot(clip, at, s.volume);
        else
        {
            float v = s.volume * WorldAudio.VolumeScale;
            if (v > 0f) flat.PlayOneShot(clip, v);
        }
    }
}

/// <summary>
/// Playing sounds (SoundCues) and player animations (AnimCues).
/// - Sfx.Play(cue, at): this machine only (things everyone already sees happen: a door opening).
/// - Sfx.ForAll(cue, at, anim, actor): everyone hears it (and sees the acting player's animation), from
///   the host or from the player doing it (it plays for them straight away).
/// </summary>
public static class Sfx
{
    /// <summary>This machine's client id (for ForAll's actor), or none.</summary>
    public static ulong Me => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;

    /// <summary>This machine only, at a spot (or flat, for flat cues).</summary>
    public static void Play(Cue cue, Vector3 at)
    {
        if (SoundCues.Instance != null) SoundCues.Instance.Play(cue, at);
    }

    /// <summary>This machine only, flat (the phone, alerts).</summary>
    public static void Play(Cue cue) => Play(cue, Camera.main != null ? Camera.main.transform.position : Vector3.zero);

    /// <summary>
    /// Everyone: the sound at a spot, and the acting player's animation (Actor: that player's client id;
    /// none: the sound alone). From the host, or from the acting player's own machine.
    /// </summary>
    public static void ForAll(Cue cue, Vector3 at, AnimCue anim = AnimCue.None, ulong actor = ulong.MaxValue, bool animOn = true)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) { Play(cue, at); return; }
        if (nm.IsServer)
        {
            var by = InventoryOf(actor) ?? InventoryOf(nm.LocalClientId) ?? AnyInventory();
            if (by == null) { Play(cue, at); return; }
            if (by.OwnerClientId != actor && actor != ulong.MaxValue) anim = AnimCue.None; // (they're gone)
            by.FxClientRpc(cue, at, anim, animOn, ulong.MaxValue);
            return;
        }
        // A client: here now, then everyone else through the host.
        Play(cue, at);
        var mine = InventoryOf(nm.LocalClientId);
        if (anim != AnimCue.None && mine != null) AnimCues.Fire(mine.gameObject, anim, animOn);
        if (mine != null) mine.FxServerRpc(cue, at, anim, animOn);
    }

    private static PlayerInventoryNet InventoryOf(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || clientId == ulong.MaxValue) return null;
        NetworkObject po = null;
        if (nm.IsServer) { if (nm.ConnectedClients.TryGetValue(clientId, out var c)) po = c.PlayerObject; }
        else if (clientId == nm.LocalClientId) po = nm.LocalClient?.PlayerObject;
        return po != null ? po.GetComponent<PlayerInventoryNet>() : null;
    }

    private static PlayerInventoryNet AnyInventory()
    {
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
            if (c.PlayerObject != null && c.PlayerObject.TryGetComponent<PlayerInventoryNet>(out var inv)) return inv;
        return null;
    }
}
