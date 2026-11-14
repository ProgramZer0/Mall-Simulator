using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where things of one kind (wall openings, walls, decor) changed lately, so whatever depends on them
/// re-checks only when a change was near it, not whenever anything changed anywhere in the mall.
///
/// Version goes up with every change. Keep the Version you last saw; ChangedNear(seen, area) says
/// whether anything changed in that area since. Only the last 64 changes are kept: further back than
/// that, it answers yes, to be safe.
/// </summary>
public sealed class ChangeLog
{
    private const int Keep = 64;
    private readonly List<(int version, Bounds area)> recent = new();

    public int Version { get; private set; }

    /// <summary>Something changed in this area.</summary>
    public void Note(Bounds area)
    {
        Version++;
        recent.Add((Version, area));
        if (recent.Count > Keep) recent.RemoveAt(0);
    }

    /// <summary>Did anything change in this area since that version?</summary>
    public bool ChangedNear(int since, Bounds area)
    {
        if (since >= Version) return false;
        if (since < 0 || recent.Count == 0 || recent[0].version > since + 1) return true; // too long ago to tell
        for (int i = recent.Count - 1; i >= 0 && recent[i].version > since; i--)
            if (recent[i].area.Intersects(area)) return true;
        return false;
    }
}
