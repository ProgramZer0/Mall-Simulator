using System;
using UnityEngine;

/// <summary>
/// This player's own settings (not networked), saved between sessions (PlayerPrefs) and applied on
/// load. Change them through the setters (the phone's Settings app does); Changed fires after.
/// Mouse sensitivity, invert Y and field of view are read by PlayerController.
/// </summary>
public static class GameSettings
{
    public static event Action Changed;

    public static float MasterVolume { get; private set; } = 1f;
    /// <summary>Multiplier on the player's look speed (1 = as set on the player).</summary>
    public static float MouseSensitivity { get; private set; } = 1f;
    public static bool InvertY { get; private set; }
    /// <summary>Degrees; 0 = the camera's own.</summary>
    public static float FieldOfView { get; private set; }
    public static int Quality { get; private set; } = -1;
    public static bool VSync { get; private set; } = true;
    /// <summary>0 = no limit.</summary>
    public static int MaxFps { get; private set; }
    public static bool Fullscreen { get; private set; } = true;

    public static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 165, 240 };

    private static bool loaded;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void LoadOnStart() => Load();

    public static void Load()
    {
        loaded = true;
        MasterVolume = PlayerPrefs.GetFloat("set.volume", 1f);
        MouseSensitivity = PlayerPrefs.GetFloat("set.mouse", 1f);
        InvertY = PlayerPrefs.GetInt("set.invertY", 0) != 0;
        FieldOfView = PlayerPrefs.GetFloat("set.fov", 0f);
        Quality = PlayerPrefs.GetInt("set.quality", QualitySettings.GetQualityLevel());
        VSync = PlayerPrefs.GetInt("set.vsync", QualitySettings.vSyncCount > 0 ? 1 : 0) != 0;
        MaxFps = PlayerPrefs.GetInt("set.maxFps", 0);
        Fullscreen = PlayerPrefs.GetInt("set.fullscreen", Screen.fullScreen ? 1 : 0) != 0;
        Apply();
    }

    public static void SetMasterVolume(float v) { MasterVolume = Mathf.Clamp01(v); PlayerPrefs.SetFloat("set.volume", MasterVolume); Done(); }
    public static void SetMouseSensitivity(float v) { MouseSensitivity = Mathf.Clamp(v, 0.1f, 4f); PlayerPrefs.SetFloat("set.mouse", MouseSensitivity); Done(); }
    public static void SetInvertY(bool v) { InvertY = v; PlayerPrefs.SetInt("set.invertY", v ? 1 : 0); Done(); }
    public static void SetFieldOfView(float v) { FieldOfView = v <= 0f ? 0f : Mathf.Clamp(v, 50f, 110f); PlayerPrefs.SetFloat("set.fov", FieldOfView); Done(); }
    public static void SetQuality(int v) { Quality = Mathf.Clamp(v, 0, QualitySettings.names.Length - 1); PlayerPrefs.SetInt("set.quality", Quality); Done(); }
    public static void SetVSync(bool v) { VSync = v; PlayerPrefs.SetInt("set.vsync", v ? 1 : 0); Done(); }
    public static void SetMaxFps(int v) { MaxFps = Mathf.Max(0, v); PlayerPrefs.SetInt("set.maxFps", MaxFps); Done(); }
    public static void SetFullscreen(bool v) { Fullscreen = v; PlayerPrefs.SetInt("set.fullscreen", v ? 1 : 0); Done(); }

    /// <summary>Everything back to how it starts.</summary>
    public static void ResetAll()
    {
        foreach (var key in new[] { "set.volume", "set.mouse", "set.invertY", "set.fov", "set.quality", "set.vsync", "set.maxFps", "set.fullscreen" })
            PlayerPrefs.DeleteKey(key);
        Load();
        Changed?.Invoke();
    }

    private static void Done()
    {
        PlayerPrefs.Save();
        Apply();
        Changed?.Invoke();
    }

    private static void Apply()
    {
        if (!loaded) return;
        AudioListener.volume = MasterVolume;
        if (Quality >= 0 && Quality < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != Quality)
            QualitySettings.SetQualityLevel(Quality, true);
        QualitySettings.vSyncCount = VSync ? 1 : 0;
        Application.targetFrameRate = MaxFps > 0 ? MaxFps : -1;
        if (!Application.isEditor && Screen.fullScreen != Fullscreen) Screen.fullScreen = Fullscreen;
    }
}
