using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A local, non-networked stand-in for an item while its store is in edit mode.
/// Built from the item's world prefab with all networking and gameplay scripts removed,
/// but keeping its look, colliders and Rigidbody, so it can be clicked, collides with the
/// other copies, and can fall when gravity is switched on for it.
/// </summary>
public class EditLocalItem : MonoBehaviour
{
    private static Transform holder;

    private Rigidbody rb;
    private bool dragging;
    private StockContainer stockSource; // the prefab's, if this item is a shelf
    private List<GameObject> stockModels;

    /// <summary>Position of this item in the host's edit session list (-1: bought during this session).</summary>
    public int Index { get; private set; }
    public ItemDef Def { get; private set; }
    public bool Gravity { get; private set; }
    /// <summary>A window or door (it sits in a wall).</summary>
    public WallOpening Opening => GetComponent<WallOpening>();

    /// <summary>Its paint color now (PaintPalette id) and when editing started (repaints cost a fee).</summary>
    public byte Paint { get; private set; }
    public byte StartPaint { get; private set; }
    public bool Repainted => Paint != StartPaint;
    public bool CanPaint => PaintParts.IsPaintable(Def);

    /// <summary>Paint this copy (shown straight away; the host applies it on save).</summary>
    public void SetPaint(byte id)
    {
        if (!CanPaint || !PaintPalette.IsValid(id)) return;
        Paint = id;
        PaintParts.ApplyToCopy(Def.worldNetworkPrefab.gameObject, gameObject, id);
    }

    /// <summary>The color it had when editing started.</summary>
    public void SetStartPaint(byte id)
    {
        SetPaint(id);
        StartPaint = Paint;
    }

    public static EditLocalItem Create(ItemDef def, int index, Vector3 position, Quaternion rotation)
    {
        // Built under an inactive parent so none of the prefab's scripts ever wake up.
        var go = Instantiate(def.worldNetworkPrefab.gameObject, position, rotation, GetHolder());
        go.name = $"{def.worldNetworkPrefab.name} (editing)";

        DestroyAll<NetworkBehaviour>(go);
        DestroyAll<MonoBehaviour>(go);
        DestroyAll<NetworkObject>(go);
        DestroyAll<AudioSource>(go);

        go.transform.SetParent(null, true);

        var item = go.AddComponent<EditLocalItem>();
        item.Index = index;
        item.Def = def;
        item.rb = go.GetComponent<Rigidbody>();

        // Shelves: their stock zones are only markers, so they mustn't catch clicks here.
        item.stockSource = def.worldNetworkPrefab.GetComponent<StockContainer>();
        if (item.stockSource != null)
            foreach (var zone in item.stockSource.FindZonesOnCopy(go.transform))
                if (zone != null) zone.enabled = false;

        // Windows and doors keep cutting their hole while being moved around.
        if (def.worldNetworkPrefab.TryGetComponent<WallOpening>(out var opening))
            go.AddComponent<WallOpening>().CopyFrom(opening);

        item.ApplyKinematic();
        return item;
    }

    private static Transform GetHolder()
    {
        if (holder != null) return holder;
        var go = new GameObject("[EditItemBuilder]");
        go.SetActive(false);
        go.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(go);
        holder = go.transform;
        return holder;
    }

    private static void DestroyAll<T>(GameObject root) where T : Component
    {
        // Back to front, so components added by [RequireComponent] go after the ones needing them.
        var comps = root.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
            if (comps[i] != null) DestroyImmediate(comps[i]);
    }

    /// <summary>Show what's on this shelf, so the stock moves with it while editing.</summary>
    public void ShowStock(int[] itemIds, int[] counts, int[] orients, int[] seeds)
    {
        if (stockSource == null) return;
        if (stockModels != null) foreach (var m in stockModels) if (m != null) Destroy(m);
        stockModels = stockSource.BuildStockOnCopy(transform, itemIds, counts, orients, seeds);
    }

    public void SetPose(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        if (rb == null) return;

        rb.position = position;
        rb.rotation = rotation;
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    /// <summary>See-through (e.g. a window or door where it can't go).</summary>
    public bool Faded { get; private set; }

    private Renderer[] fadeRenderers;
    private Material[][] fadeOriginal;
    private Material[][] fadeMaterials;

    /// <summary>Show the item see-through, or back to normal.</summary>
    public void SetFaded(bool on, float alpha = 0.3f)
    {
        if (on == Faded) return;
        Faded = on;

        if (fadeRenderers == null)
        {
            fadeRenderers = GetComponentsInChildren<Renderer>(true);
            fadeOriginal = new Material[fadeRenderers.Length][];
            for (int i = 0; i < fadeRenderers.Length; i++) fadeOriginal[i] = fadeRenderers[i].sharedMaterials;
        }
        if (on && fadeMaterials == null)
        {
            fadeMaterials = new Material[fadeRenderers.Length][];
            for (int i = 0; i < fadeRenderers.Length; i++)
            {
                var src = fadeOriginal[i];
                fadeMaterials[i] = new Material[src.Length];
                for (int k = 0; k < src.Length; k++)
                    fadeMaterials[i][k] = src[k] != null ? GhostBuilder.MakeTransparent(src[k], alpha) : null;
            }
        }

        for (int i = 0; i < fadeRenderers.Length; i++)
            if (fadeRenderers[i] != null) fadeRenderers[i].sharedMaterials = on ? fadeMaterials[i] : fadeOriginal[i];
    }

    private void OnDestroy()
    {
        if (fadeMaterials == null) return;
        foreach (var set in fadeMaterials)
            foreach (var m in set)
                if (m != null) Destroy(m);
    }

    /// <summary>Gravity on: falls and settles. Off: hangs exactly where it's put.</summary>
    public void SetGravity(bool on)
    {
        Gravity = on;
        ApplyKinematic();
    }

    /// <summary>Held still while being dragged, even if gravity is on.</summary>
    public void SetDragging(bool on)
    {
        dragging = on;
        ApplyKinematic();
    }

    private void ApplyKinematic()
    {
        if (rb == null) return;

        bool kinematic = dragging || !Gravity;
        if (kinematic && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = kinematic;
        if (!kinematic) rb.WakeUp();
    }
}
