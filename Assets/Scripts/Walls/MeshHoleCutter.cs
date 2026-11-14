using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Cuts holes straight through a mesh (for CuttableWall's Cut Models): triangles inside a hole go,
/// triangles across its edge are trimmed along it (positions, normals, tangents, UVs and colours
/// carried along), the rest stay as they are. The hole's sides aren't filled in (a window or door
/// frame covers them).
///
/// Holes are convex outlines in wall space (u along, v up): rectangles, arches, rounded tops. To Wall
/// gives each vertex's (u, v).
/// </summary>
public static class MeshHoleCutter
{
    private struct V
    {
        public Vector3 p, n;
        public Vector4 t;
        public Vector2 uv;
        public Color c;
        public Vector2 w; // wall space (u, v)

        public static V Lerp(V a, V b, float k) => new()
        {
            p = Vector3.Lerp(a.p, b.p, k),
            n = Vector3.Lerp(a.n, b.n, k),
            t = Vector4.Lerp(a.t, b.t, k),
            uv = Vector2.Lerp(a.uv, b.uv, k),
            c = Color.Lerp(a.c, b.c, k),
            w = Vector2.Lerp(a.w, b.w, k),
        };
    }

    // Reused buffers (main thread only).
    private static readonly List<Vector3> SrcP = new(), SrcN = new(), OutP = new(), OutN = new();
    private static readonly List<Vector4> SrcT = new(), OutT = new();
    private static readonly List<Vector2> SrcUV = new(), OutUV = new();
    private static readonly List<Color> SrcC = new(), OutC = new();
    private static readonly List<int> SrcTris = new();
    private static readonly List<List<int>> OutTris = new();

    /// <summary>
    /// Fill Into with Source minus the holes. To Wall maps a vertex (Source's local space) to wall space.
    /// With no holes touching it, Into is a plain copy.
    /// </summary>
    public static void Cut(Mesh source, Mesh into, Func<Vector3, Vector2> toWall, IReadOnlyList<Rect> holes)
    {
        var outlines = new List<Vector2[]>(holes.Count);
        foreach (var r in holes) outlines.Add(Outline(r));
        Cut(source, into, toWall, outlines);
    }

    /// <summary>A rectangle as an outline (counter-clockwise).</summary>
    public static Vector2[] Outline(Rect r) =>
        new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax) };

    /// <summary>The same outline counter-clockwise (holes are cut assuming that way round).</summary>
    public static Vector2[] CounterClockwise(Vector2[] outline)
    {
        float area = 0f;
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
            area += a.x * b.y - b.x * a.y;
        }
        if (area >= 0f) return outline;
        var r = (Vector2[])outline.Clone();
        Array.Reverse(r);
        return r;
    }

    /// <summary>Fill Into with Source minus the holes (convex outlines, counter-clockwise, in wall space).</summary>
    public static void Cut(Mesh source, Mesh into, Func<Vector3, Vector2> toWall, IReadOnlyList<Vector2[]> holes)
    {
        into.Clear();
        if (source == null) return;
        source.GetVertices(SrcP);
        source.GetNormals(SrcN);
        source.GetTangents(SrcT);
        source.GetUVs(0, SrcUV);
        source.GetColors(SrcC);
        bool hasN = SrcN.Count == SrcP.Count, hasT = SrcT.Count == SrcP.Count;
        bool hasUV = SrcUV.Count == SrcP.Count, hasC = SrcC.Count == SrcP.Count;

        int n = SrcP.Count;
        var wall = new Vector2[n];
        for (int i = 0; i < n; i++) wall[i] = toWall(SrcP[i]);

        OutP.Clear(); OutN.Clear(); OutT.Clear(); OutUV.Clear(); OutC.Clear();
        var remap = new int[n];
        for (int i = 0; i < n; i++) remap[i] = -1;

        int subCount = source.subMeshCount;
        while (OutTris.Count < subCount) OutTris.Add(new List<int>());
        var poly = new List<V>(8);
        var pieces = new List<List<V>>();

        for (int sub = 0; sub < subCount; sub++)
        {
            var outTris = OutTris[sub];
            outTris.Clear();
            if (source.GetTopology(sub) != MeshTopology.Triangles) continue;
            source.GetTriangles(SrcTris, sub);
            for (int k = 0; k + 2 < SrcTris.Count; k += 3)
            {
                int a = SrcTris[k], b = SrcTris[k + 1], c = SrcTris[k + 2];
                if (!TouchesAny(wall[a], wall[b], wall[c], holes))
                {
                    outTris.Add(Keep(a)); outTris.Add(Keep(b)); outTris.Add(Keep(c));
                    continue;
                }

                // Trim it: what's left outside every hole, as convex pieces.
                pieces.Clear();
                poly = new List<V> { Make(a), Make(b), Make(c) };
                pieces.Add(poly);
                foreach (var hole in holes) pieces = CutHole(pieces, hole);
                foreach (var piece in pieces)
                {
                    if (piece.Count < 3) continue;
                    int first = Add(piece[0]);
                    for (int i = 1; i + 1 < piece.Count; i++)
                    {
                        outTris.Add(first);
                        outTris.Add(Add(piece[i]));
                        outTris.Add(Add(piece[i + 1]));
                    }
                }
            }
        }

        into.indexFormat = OutP.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        into.SetVertices(OutP);
        if (hasN) into.SetNormals(OutN);
        if (hasT) into.SetTangents(OutT);
        if (hasUV) into.SetUVs(0, OutUV);
        if (hasC) into.SetColors(OutC);
        into.subMeshCount = subCount;
        for (int sub = 0; sub < subCount; sub++) into.SetTriangles(OutTris[sub], sub, false);
        into.RecalculateBounds();

        int Keep(int i)
        {
            if (remap[i] >= 0) return remap[i];
            remap[i] = OutP.Count;
            OutP.Add(SrcP[i]);
            if (hasN) OutN.Add(SrcN[i]);
            if (hasT) OutT.Add(SrcT[i]);
            if (hasUV) OutUV.Add(SrcUV[i]);
            if (hasC) OutC.Add(SrcC[i]);
            return remap[i];
        }

        V Make(int i) => new()
        {
            p = SrcP[i],
            n = hasN ? SrcN[i] : Vector3.zero,
            t = hasT ? SrcT[i] : Vector4.zero,
            uv = hasUV ? SrcUV[i] : Vector2.zero,
            c = hasC ? SrcC[i] : Color.white,
            w = wall[i],
        };

        int Add(V v)
        {
            int i = OutP.Count;
            OutP.Add(v.p);
            if (hasN) OutN.Add(v.n.normalized);
            if (hasT) OutT.Add(v.t);
            if (hasUV) OutUV.Add(v.uv);
            if (hasC) OutC.Add(v.c);
            return i;
        }
    }

    private static Rect BoundsOf(Vector2[] outline)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in outline)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static bool TouchesAny(Vector2 a, Vector2 b, Vector2 c, IReadOnlyList<Vector2[]> holes)
    {
        float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
        foreach (var h in holes)
        {
            var r = BoundsOf(h);
            if (maxX > r.xMin && minX < r.xMax && maxY > r.yMin && minY < r.yMax) return true;
        }
        return false;
    }

    // Each piece: what's outside each edge of the hole is kept; what's inside every edge (in the hole) goes.
    private static List<List<V>> CutHole(List<List<V>> pieces, Vector2[] hole)
    {
        var result = new List<List<V>>();
        var r = BoundsOf(hole);
        foreach (var piece in pieces)
        {
            if (!Overlaps(piece, r)) { result.Add(piece); continue; }
            var rest = piece;
            for (int i = 0; i < hole.Length && rest.Count >= 3; i++)
            {
                Split(rest, hole[i], hole[(i + 1) % hole.Length], out var inside, out var outside);
                if (outside.Count >= 3) result.Add(outside);
                rest = inside;
            }
        }
        return result;
    }

    private static bool Overlaps(List<V> piece, Rect hole)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in piece)
        {
            minX = Mathf.Min(minX, v.w.x); maxX = Mathf.Max(maxX, v.w.x);
            minY = Mathf.Min(minY, v.w.y); maxY = Mathf.Max(maxY, v.w.y);
        }
        return maxX > hole.xMin && minX < hole.xMax && maxY > hole.yMin && minY < hole.yMax;
    }

    // A convex polygon cut by the line through A and B: the part on its left (inside a counter-clockwise
    // hole) and on its right.
    private static void Split(List<V> poly, Vector2 a, Vector2 b, out List<V> inside, out List<V> outside)
    {
        inside = new List<V>(poly.Count + 1);
        outside = new List<V>(poly.Count + 1);
        Vector2 d = b - a;
        for (int i = 0; i < poly.Count; i++)
        {
            V p = poly[i], q = poly[(i + 1) % poly.Count];
            float sp = d.x * (p.w.y - a.y) - d.y * (p.w.x - a.x);
            float sq = d.x * (q.w.y - a.y) - d.y * (q.w.x - a.x);
            if (sp >= 0f) inside.Add(p); // on the line: both sides
            if (sp <= 0f) outside.Add(p);
            if ((sp > 0f && sq < 0f) || (sp < 0f && sq > 0f))
            {
                V m = V.Lerp(p, q, sp / (sp - sq));
                inside.Add(m);
                outside.Add(m);
            }
        }
    }

    /* ========================= FLAT (2D) ========================= */

    /// <summary>
    /// A rectangle minus a hole (convex, counter-clockwise), as triangles (every 3 points one triangle,
    /// counter-clockwise), all in wall space. For the solid corners around an arch in a plain box wall.
    /// </summary>
    public static void RectMinusHole(Rect rect, Vector2[] hole, List<Vector2> triangles)
    {
        var start = new List<V>(4);
        foreach (var p in Outline(rect)) start.Add(new V { w = p });
        var pieces = CutHole(new List<List<V>> { start }, hole);
        foreach (var piece in pieces)
            for (int i = 1; i + 1 < piece.Count; i++)
            {
                triangles.Add(piece[0].w);
                triangles.Add(piece[i].w);
                triangles.Add(piece[i + 1].w);
            }
    }
}
