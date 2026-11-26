#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor only. TextMeshPro keeps a list of every text to tell when a font or material changes
/// (TMPro_EventManager). Now and then a destroyed text stays on that list, and TMP's editor helper then
/// throws "MissingReferenceException: The object of type 'TMPro.TextMeshPro' has been destroyed" every
/// time a font changes. This takes destroyed texts off those lists just before canvases render, and says
/// once in the Console which object it was (so the cause can be found). Builds aren't affected.
/// </summary>
[InitializeOnLoad]
public static class TmpDeadListenerCleaner
{
    private static readonly List<(object action, MethodInfo remove, FieldInfo list)> events = new();
    private static readonly Dictionary<int, string> names = new(); // what each live text was called
    private static readonly HashSet<string> reported = new();
    private static double nextNames;

    static TmpDeadListenerCleaner()
    {
        var manager = Type.GetType("TMPro.TMPro_EventManager, Unity.TextMeshPro");
        if (manager == null) return;
        foreach (var f in manager.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object action = f.GetValue(null);
            if (action == null || !action.GetType().Name.StartsWith("FastAction")) continue;
            var list = action.GetType().GetField("delegates", BindingFlags.NonPublic | BindingFlags.Instance);
            var remove = action.GetType().GetMethod("Remove");
            if (list != null && remove != null) events.Add((action, remove, list));
        }
        Canvas.preWillRenderCanvases += Clean;
    }

    private static readonly List<Delegate> dead = new();

    private static void Clean()
    {
        bool takeNames = EditorApplication.timeSinceStartup >= nextNames;
        if (takeNames) nextNames = EditorApplication.timeSinceStartup + 1.0;
        foreach (var (action, remove, list) in events)
        {
            if (list.GetValue(action) is not IEnumerable delegates) continue;
            dead.Clear();
            foreach (var item in delegates)
            {
                if (item is not Delegate d || d.Target is not UnityEngine.Object target) continue;
                int id = RuntimeHelpers(target);
                if (target == null) dead.Add(d); // destroyed, still listening
                else if (takeNames && !names.ContainsKey(id)) names[id] = Describe(target);
            }
            foreach (var d in dead)
            {
                try { remove.Invoke(action, new object[] { d }); } catch { /* leave it */ }
                int id = RuntimeHelpers((UnityEngine.Object)d.Target);
                string what = names.TryGetValue(id, out var n) ? n : "(a text made and destroyed within a second)";
                names.Remove(id);
                if (reported.Add(what))
                    Debug.LogWarning($"[TMP] A destroyed text was still on TextMeshPro's update list (now taken off): {what}. " +
                                     "Harmless; tell Claude this name if it keeps happening.");
            }
        }
    }

    // Its instance id (works on destroyed objects too).
    private static int RuntimeHelpers(UnityEngine.Object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    private static string Describe(UnityEngine.Object o)
    {
        if (o is not Component c) return o.name;
        string path = c.name;
        var t = c.transform.parent;
        for (int i = 0; t != null && i < 4; i++, t = t.parent) path = t.name + "/" + path;
        return path;
    }
}
#endif
