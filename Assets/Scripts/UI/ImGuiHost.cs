using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One place for the old-style (IMGUI) debug overlays and fallbacks. Any script with an OnGUI method
/// makes Unity set up IMGUI for it every frame (garbage and time) even when it draws nothing, so
/// scripts don't have OnGUI themselves: they call Set(this, draw) while they have something to show,
/// and Set(this, null) when they don't. With nothing to show, the host is switched off and costs
/// nothing.
///
/// Pass a delegate kept in a field (drawX ??= DrawX), not a fresh method group each frame, so calling
/// Set every frame makes no garbage.
/// </summary>
public sealed class ImGuiHost : MonoBehaviour
{
    private static ImGuiHost instance;
    private static readonly List<(object owner, Action draw)> Draws = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay() => Draws.Clear();

    /// <summary>Show (draw not null) or stop showing (null) this owner's overlay.</summary>
    public static void Set(object owner, Action draw)
    {
        int i = -1;
        for (int k = 0; k < Draws.Count; k++)
            if (ReferenceEquals(Draws[k].owner, owner)) { i = k; break; }
        if (draw == null)
        {
            if (i < 0) return;
            Draws.RemoveAt(i);
        }
        else if (i >= 0)
        {
            if (Draws[i].draw == draw) return;
            Draws[i] = (owner, draw);
        }
        else Draws.Add((owner, draw));

        if (instance == null)
        {
            if (Draws.Count == 0) return;
            var go = new GameObject("[ImGuiHost]") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ImGuiHost>();
        }
        instance.enabled = Draws.Count > 0;
    }

    private void OnGUI()
    {
        for (int i = Draws.Count - 1; i >= 0; i--)
        {
            var (owner, draw) = Draws[i];
            if (owner is UnityEngine.Object o && o == null) { Draws.RemoveAt(i); continue; } // destroyed
            try { draw(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        if (Draws.Count == 0) enabled = false;
    }
}
