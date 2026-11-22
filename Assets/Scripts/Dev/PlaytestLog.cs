using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A playtest log for builds: one text file per run, with
/// - the machine (CPU, cores, RAM, GPU, OS) and game version at the top;
/// - every Stats Every seconds: frame rate (average, worst frame, slow frames), main-thread CPU time,
///   memory and garbage collections, and the network (mode, players, each one's ping, spawned objects,
///   customers);
/// - as they happen: hitches (a frame over Hitch Ms, and whether a garbage collection or scene load was
///   in it), players joining and leaving (with why), scene loads, transport failures, errors and
///   exceptions (repeats counted, not spammed), [Connection] / [MallManager] messages;
/// - markers: a tester presses F2 when something odd happens ("MARK #3"), so it's easy to find.
///
/// Made by itself when the game starts (nothing to set up). Files go to Application.persistentDataPath/
/// PlaytestLogs (Windows: %USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;\PlaytestLogs), the
/// newest 20 are kept. Start the game with -noplaytestlog to turn it off.
/// </summary>
public class PlaytestLog : MonoBehaviour
{
    private const float StatsEvery = 10f;   // seconds
    private const float HitchMs = 150f;     // a frame this slow is logged on its own
    private const KeyCode MarkKey = KeyCode.F2;
    private const int KeepFiles = 20;

    public static string FilePath { get; private set; }

    private StreamWriter writer;
    private readonly StringBuilder sb = new();
    private NetworkManager hooked;
    private NetworkSceneManager hookedScenes;

    // This stats window.
    private float windowStart;
    private int frames, slowFrames, hitches;
    private float worstMs, sumMs;
    private long mainThreadNsSum;
    private int mainThreadSamples;
    private long allocSum;
    private int gcAtWindowStart, gcLastFrame;
    private int errorsInWindow, warningsInWindow;
    private bool sceneLoadedThisFrame;

    private ProfilerRecorder mainThread, gcAlloc, systemMemory;

    // Repeated messages are counted, not written again (within RepeatSeconds).
    private const float RepeatSeconds = 30f;
    private readonly Dictionary<string, (float first, int count)> repeats = new();

    private int marks;
    private float markShownUntil;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Create()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (string.Equals(a, "-noplaytestlog", StringComparison.OrdinalIgnoreCase)) return;
        var go = new GameObject("Playtest Log");
        go.hideFlags = HideFlags.DontSave;
        DontDestroyOnLoad(go);
        go.AddComponent<PlaytestLog>();
    }

    private void Awake()
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "PlaytestLogs");
            Directory.CreateDirectory(dir);
            var old = new List<string>(Directory.GetFiles(dir, "playtest_*.txt"));
            old.Sort();
            for (int i = 0; i < old.Count - (KeepFiles - 1); i++) { try { File.Delete(old[i]); } catch { } }
            FilePath = Path.Combine(dir, $"playtest_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            writer = new StreamWriter(FilePath, false, Encoding.UTF8);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PlaytestLog] Couldn't make the log file: {e.Message}");
            enabled = false;
            return;
        }

        Line("=== Mall Sim playtest log ===");
        Line($"Game {Application.productName} {Application.version}, Unity {Application.unityVersion}, " +
             $"{(Application.isEditor ? "editor" : Debug.isDebugBuild ? "development build" : "release build")}");
        Line($"Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local), {DateTime.UtcNow:HH:mm:ss} UTC");
        Line($"OS: {SystemInfo.operatingSystem}");
        Line($"CPU: {SystemInfo.processorType}, {SystemInfo.processorCount} threads, {SystemInfo.processorFrequency} MHz");
        Line($"RAM: {SystemInfo.systemMemorySize} MB");
        Line($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB, {SystemInfo.graphicsDeviceType})");
        Line($"Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:0} Hz, " +
             $"vSync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}, quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}'");
        Line($"Unity's own log: {Application.consoleLogPath}");
        Line($"Press {MarkKey} in game to mark a moment in this log.");
        Line("Times are seconds since the game started.");
        Line("");
        writer.Flush();
        Debug.Log($"[PlaytestLog] Writing to {FilePath}");

        mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
        gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
        systemMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory", 1);

        Application.logMessageReceived += OnLog;
        Application.lowMemory += OnLowMemory;
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartWindow();
        gcLastFrame = GC.CollectionCount(0);
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        Application.lowMemory -= OnLowMemory;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Unhook();
        mainThread.Dispose();
        gcAlloc.Dispose();
        systemMemory.Dispose();
        Close("closed");
    }

    private void OnApplicationQuit()
    {
        WriteStats();
        Close("quit");
    }

    private void OnApplicationFocus(bool focus) => Line(focus ? "Window focused" : "Window lost focus (alt-tabbed?)");

    private void Close(string why)
    {
        if (writer == null) return;
        Line($"=== Log {why} ===");
        try { writer.Flush(); writer.Dispose(); } catch { }
        writer = null;
    }

    /* ========================= EVERY FRAME ========================= */

    private void Update()
    {
        if (writer == null) return;
        HookNetwork();

        float ms = Time.unscaledDeltaTime * 1000f;
        if (Time.frameCount > 2)
        {
            frames++;
            sumMs += ms;
            if (ms > worstMs) worstMs = ms;
            if (ms > 33.4f) slowFrames++;
            if (mainThread.Valid && mainThread.Count > 0) { mainThreadNsSum += mainThread.LastValue; mainThreadSamples++; }
            if (gcAlloc.Valid && gcAlloc.Count > 0) allocSum += gcAlloc.LastValue;

            int gc = GC.CollectionCount(0);
            if (ms > HitchMs)
            {
                hitches++;
                string why = gc != gcLastFrame ? "garbage collection in it" : sceneLoadedThisFrame ? "a scene loaded" : "no GC or scene load";
                Line($"HITCH {ms:0} ms frame ({why}; {Counts()})");
            }
            gcLastFrame = gc;
        }
        sceneLoadedThisFrame = false;

        if (Input.GetKeyDown(MarkKey))
        {
            marks++;
            markShownUntil = Time.unscaledTime + 2.5f;
            Line($">>> MARK #{marks} <<<  (pressed {MarkKey}: something happened here) — {Counts()}, " +
                 $"{(frames > 0 ? sumMs / frames : 0f):0.0} ms/frame lately, open menus: {UIState.Describe()}");
            WriteNetworkDetail();
            writer.Flush();
        }

        if (Time.unscaledTime - windowStart >= StatsEvery) WriteStats();
    }

    private void OnGUI()
    {
        if (Time.unscaledTime > markShownUntil) return;
        GUI.color = Color.yellow;
        GUI.Label(new Rect(10, 10, 600, 24), $"Marked #{marks} in the playtest log");
    }

    /* ========================= STATS ========================= */

    private void StartWindow()
    {
        windowStart = Time.unscaledTime;
        frames = slowFrames = hitches = 0;
        worstMs = sumMs = 0f;
        mainThreadNsSum = 0;
        mainThreadSamples = 0;
        allocSum = 0;
        errorsInWindow = warningsInWindow = 0;
        gcAtWindowStart = GC.CollectionCount(0);
    }

    private void WriteStats()
    {
        if (writer == null) return;
        float secs = Mathf.Max(0.001f, Time.unscaledTime - windowStart);
        float avgMs = frames > 0 ? sumMs / frames : 0f;
        sb.Clear();
        sb.Append($"STATS {secs:0}s: {(avgMs > 0 ? 1000f / avgMs : 0f):0} fps (avg {avgMs:0.0} ms, worst {worstMs:0} ms, {slowFrames} under 30 fps");
        if (hitches > 0) sb.Append($", {hitches} hitches");
        sb.Append(')');
        if (mainThreadSamples > 0)
        {
            float cpu = mainThreadNsSum / (float)mainThreadSamples / 1e6f;
            sb.Append($" | CPU main thread {cpu:0.0} ms{(avgMs > 0 && cpu < avgMs * 0.6f ? " (waiting on GPU or vsync)" : "")}");
        }
        sb.Append($" | managed {GC.GetTotalMemory(false) / 1048576f:0} MB, allocating {allocSum / secs / 1024f:0} KB/s, " +
                  $"{GC.CollectionCount(0) - gcAtWindowStart} GCs");
        if (systemMemory.Valid && systemMemory.Count > 0) sb.Append($", process {systemMemory.LastValue / 1048576f:0} MB");
        sb.Append($" | {Counts()}");
        if (errorsInWindow > 0 || warningsInWindow > 0) sb.Append($" | {errorsInWindow} errors, {warningsInWindow} warnings");
        Line(sb.ToString());
        WriteNetworkDetail();
        FlushRepeats(false);
        writer.Flush();
        StartWindow();
    }

    // The short "where are we" summary.
    private string Counts()
    {
        var nm = NetworkManager.Singleton;
        string scene = SceneManager.GetActiveScene().name;
        if (nm == null || !nm.IsListening) return $"scene {scene}, offline";
        var conn = MallConnection.Instance;
        string mode = conn != null ? conn.CurrentMode.ToString() : nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";
        int players = nm.IsServer ? nm.ConnectedClientsIds.Count : CountPlayers();
        int objects = nm.SpawnManager != null ? nm.SpawnManager.SpawnedObjectsList.Count : 0;
        var cm = CustomerManager.Instance;
        return $"scene {scene}, {mode}, {players} players, {objects} net objects, {(cm != null ? cm.Count : 0)} customers";
    }

    private static int CountPlayers()
    {
        int n = 0;
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return 0;
        foreach (var o in nm.SpawnManager.SpawnedObjectsList) if (o.IsPlayerObject) n++;
        return n;
    }

    // Each player's ping (the host sees everyone; a client sees the host).
    private void WriteNetworkDetail()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return;
        var utp = nm.NetworkConfig.NetworkTransport as UnityTransport;
        sb.Clear();
        sb.Append("  NET ping:");
        if (nm.IsServer)
        {
            foreach (ulong id in nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId) { sb.Append($" {Name(id)} (host)"); continue; }
                sb.Append($" {Name(id)} {(utp != null ? utp.GetCurrentRtt(id) : 0)} ms");
            }
        }
        else sb.Append($" to host {(utp != null ? utp.GetCurrentRtt(NetworkManager.ServerClientId) : 0)} ms, me {Name(nm.LocalClientId)}");
        Line(sb.ToString());
    }

    private static string Name(ulong id)
    {
        string n = null;
        try { n = PlayerNameTag.NameOf(id); } catch { }
        return string.IsNullOrEmpty(n) ? $"#{id}" : $"{n}#{id}";
    }

    /* ========================= EVENTS ========================= */

    private void HookNetwork()
    {
        var nm = NetworkManager.Singleton;
        if (nm != hooked)
        {
            Unhook();
            hooked = nm;
            if (nm != null)
            {
                nm.OnClientConnectedCallback += OnConnected;
                nm.OnClientDisconnectCallback += OnDisconnected;
                nm.OnServerStarted += OnServerStarted;
                nm.OnClientStarted += OnClientStarted;
                nm.OnServerStopped += OnServerStopped;
                nm.OnClientStopped += OnClientStopped;
                nm.OnTransportFailure += OnTransportFailure;
            }
        }
        var scenes = nm != null ? nm.SceneManager : null;
        if (scenes != hookedScenes)
        {
            if (hookedScenes != null) hookedScenes.OnSceneEvent -= OnSceneEvent;
            hookedScenes = scenes;
            if (scenes != null) scenes.OnSceneEvent += OnSceneEvent;
        }
    }

    private void Unhook()
    {
        if (hookedScenes != null) { hookedScenes.OnSceneEvent -= OnSceneEvent; hookedScenes = null; }
        if (hooked == null) return;
        hooked.OnClientConnectedCallback -= OnConnected;
        hooked.OnClientDisconnectCallback -= OnDisconnected;
        hooked.OnServerStarted -= OnServerStarted;
        hooked.OnClientStarted -= OnClientStarted;
        hooked.OnServerStopped -= OnServerStopped;
        hooked.OnClientStopped -= OnClientStopped;
        hooked.OnTransportFailure -= OnTransportFailure;
        hooked = null;
    }

    private void OnServerStarted() => Line($"NET server started ({Counts()})");
    private void OnClientStarted() => Line("NET client started (connecting...)");
    private void OnServerStopped(bool wasHost) => Line($"NET server stopped{(wasHost ? " (was host)" : "")}");
    private void OnClientStopped(bool wasHost) => Line("NET client stopped");
    private void OnTransportFailure() { Line("NET TRANSPORT FAILURE (the connection broke)"); writer?.Flush(); }

    private void OnConnected(ulong id)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && !nm.IsServer && id == nm.LocalClientId) Line($"NET connected to the host as #{id}");
        else Line($"NET player joined: {Name(id)} ({Counts()})");
        writer?.Flush();
    }

    private void OnDisconnected(ulong id)
    {
        var nm = NetworkManager.Singleton;
        string reason = nm != null ? nm.DisconnectReason : null;
        if (nm != null && !nm.IsServer) Line($"NET DISCONNECTED from the host{(string.IsNullOrEmpty(reason) ? " (no reason given: lost connection?)" : $": {reason}")}");
        else Line($"NET player left: {Name(id)}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")} ({Counts()})");
        writer?.Flush();
    }

    private void OnSceneEvent(SceneEvent e)
    {
        switch (e.SceneEventType)
        {
            case SceneEventType.Load:
            case SceneEventType.LoadEventCompleted:
            case SceneEventType.Synchronize:
            case SceneEventType.SynchronizeComplete:
                string extra = "";
                if (e.SceneEventType == SceneEventType.LoadEventCompleted && e.ClientsThatTimedOut != null && e.ClientsThatTimedOut.Count > 0)
                    extra = $", TIMED OUT: {string.Join(", ", e.ClientsThatTimedOut.ConvertAll(Name))}";
                Line($"NET scene {e.SceneEventType} '{e.SceneName}' (client {Name(e.ClientId)}{extra})");
                break;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        sceneLoadedThisFrame = true;
        Line($"Scene loaded: {scene.name} ({mode})");
    }

    private void OnLowMemory() { Line("LOW MEMORY warning from the OS"); writer?.Flush(); }

    private void OnLog(string message, string stack, LogType type)
    {
        if (writer == null) return;
        bool problem = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        if (problem) errorsInWindow++;
        else if (type == LogType.Warning) warningsInWindow++;
        bool ours = message.StartsWith("[Connection]") || message.StartsWith("[MallManager]") || message.StartsWith("[Netcode]");
        if (!problem && type != LogType.Warning && !ours) return;

        string key = type + message;
        if (repeats.TryGetValue(key, out var r) && Time.unscaledTime - r.first < RepeatSeconds)
        {
            repeats[key] = (r.first, r.count + 1);
            return;
        }
        repeats[key] = (Time.unscaledTime, 0);
        string tag = type switch { LogType.Exception => "EXCEPTION", LogType.Error => "ERROR", LogType.Assert => "ASSERT", LogType.Warning => "warning", _ => "log" };
        Line($"{tag}: {message}");
        if (type == LogType.Exception && !string.IsNullOrEmpty(stack))
            foreach (var s in stack.Split('\n')) if (s.Trim().Length > 0) Line("    " + s.Trim());
        if (problem) writer.Flush();
    }

    // "… repeated N more times" for messages that came again.
    private void FlushRepeats(bool all)
    {
        if (repeats.Count == 0) return;
        var done = new List<string>();
        foreach (var kv in repeats)
        {
            if (!all && Time.unscaledTime - kv.Value.first < RepeatSeconds) continue;
            if (kv.Value.count > 0)
            {
                string msg = kv.Key.Length > 160 ? kv.Key.Substring(0, 160) + "…" : kv.Key;
                Line($"  (repeated {kv.Value.count} more times in {RepeatSeconds:0}s: {msg})");
            }
            done.Add(kv.Key);
        }
        foreach (var k in done) repeats.Remove(k);
    }

    private void Line(string text)
    {
        if (writer == null) return;
        try { writer.WriteLine($"[{Time.realtimeSinceStartup,8:0.0}] {text}"); } catch { }
    }
}
