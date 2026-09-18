using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using PicoGK;

namespace TpmsHrv;

/// <summary>
/// Indexed triangle mesh held in managed arrays, so it can be decimated and
/// written without going through PicoGK's per-call native mesh API.
/// </summary>
sealed class IndexedMesh
{
    public float[] afVerts; // x, y, z per vertex
    public int[] anTris;    // a, b, c per triangle

    public int nVertices => afVerts.Length / 3;
    public int nTriangles => anTris.Length / 3;

    public IndexedMesh(float[] afVerts, int[] anTris)
    {
        this.afVerts = afVerts;
        this.anTris = anTris;
    }

    public static IndexedMesh oFromPicoGK(Mesh msh)
    {
        int nV = msh.nVertexCount(), nT = msh.nTriangleCount();
        var afV = new float[3 * nV];
        var anT = new int[3 * nT];

        for (int i = 0; i < nV; i++)
        {
            Vector3 vec = msh.vecVertexAt(i);
            afV[3 * i] = vec.X; afV[3 * i + 1] = vec.Y; afV[3 * i + 2] = vec.Z;
        }

        for (int i = 0; i < nT; i++)
        {
            Triangle t = msh.oTriangleAt(i);
            anT[3 * i] = t.A; anT[3 * i + 1] = t.B; anT[3 * i + 2] = t.C;
        }

        return new IndexedMesh(afV, anT);
    }

    public Vector3 vecVertex(int n) => new(afVerts[3 * n], afVerts[3 * n + 1], afVerts[3 * n + 2]);

    /// <summary>Drops vertices no triangle uses and renumbers the rest.</summary>
    public void Compact()
    {
        var anMap = new int[nVertices];
        Array.Fill(anMap, -1);
        int nUsed = 0;
        foreach (int n in anTris)
            if (anMap[n] < 0)
                anMap[n] = nUsed++;

        var afNew = new float[3 * nUsed];
        for (int i = 0; i < anMap.Length; i++)
        {
            int j = anMap[i];
            if (j >= 0)
                Array.Copy(afVerts, 3 * i, afNew, 3 * j, 3);
        }

        for (int i = 0; i < anTris.Length; i++)
            anTris[i] = anMap[anTris[i]];
        afVerts = afNew;
    }

    /// <summary>
    /// Counts undirected edges by how many triangles use them. A closed
    /// manifold surface has every edge used exactly twice.
    /// </summary>
    public (long nEdges, long nOpen, long nNonManifold, long nDegenerate) oTopologyCheck()
    {
        var anKeys = new long[anTris.Length];
        long nDegenerate = 0;
        for (int t = 0; t < nTriangles; t++)
        {
            int a = anTris[3 * t], b = anTris[3 * t + 1], c = anTris[3 * t + 2];
            if (a == b || b == c || c == a)
                nDegenerate++;
            anKeys[3 * t]     = nEdgeKey(a, b);
            anKeys[3 * t + 1] = nEdgeKey(b, c);
            anKeys[3 * t + 2] = nEdgeKey(c, a);
        }
        Array.Sort(anKeys);

        long nEdges = 0, nOpen = 0, nNonManifold = 0;
        for (int i = 0; i < anKeys.Length;)
        {
            int j = i;
            while (j < anKeys.Length && anKeys[j] == anKeys[i])
                j++;
            nEdges++;
            if (j - i == 1) nOpen++;
            else if (j - i > 2) nNonManifold++;
            i = j;
        }
        return (nEdges, nOpen, nNonManifold, nDegenerate);
    }

    /// <summary>Enclosed volume; positive when triangles wind outward, as 3MF requires.</summary>
    public double dSignedVolume()
    {
        double dSum = 0;
        for (int t = 0; t < nTriangles; t++)
        {
            Vector3 a = vecVertex(anTris[3 * t]), b = vecVertex(anTris[3 * t + 1]), c = vecVertex(anTris[3 * t + 2]);
            dSum += Vector3.Dot(a, Vector3.Cross(b, c));
        }
        return dSum / 6;
    }

    static long nEdgeKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Writes a single-object 3MF (the core 3MF spec: a zip holding the model
    /// XML plus the two packaging parts). Units are millimetres.
    /// </summary>
    public void SaveTo3mf(string strPath)
    {
        using var oFile = new FileStream(strPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var oZip = new ZipArchive(oFile, ZipArchiveMode.Create);

        void WriteText(string strEntry, string strText)
        {
            using var oWriter = new StreamWriter(oZip.CreateEntry(strEntry, CompressionLevel.Optimal).Open());
            oWriter.Write(strText);
        }

        WriteText("[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/>
            </Types>
            """);
        WriteText("_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/>
            </Relationships>
            """);

        using var oModel = new StreamWriter(oZip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal).Open(),
                                            new System.Text.UTF8Encoding(false), 1 << 20);
        oModel.Write(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="millimeter" xml:lang="en-US" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
            <resources><object id="1" type="model"><mesh><vertices>

            """);
        CultureInfo oInv = CultureInfo.InvariantCulture;
        for (int i = 0; i < nVertices; i++)
        {
            oModel.Write("<vertex x=\"");
            oModel.Write(afVerts[3 * i].ToString("0.#####", oInv));
            oModel.Write("\" y=\"");
            oModel.Write(afVerts[3 * i + 1].ToString("0.#####", oInv));
            oModel.Write("\" z=\"");
            oModel.Write(afVerts[3 * i + 2].ToString("0.#####", oInv));
            oModel.Write("\"/>\n");
        }
        oModel.Write("</vertices><triangles>\n");
        for (int t = 0; t < nTriangles; t++)
        {
            oModel.Write("<triangle v1=\"");
            oModel.Write(anTris[3 * t]);
            oModel.Write("\" v2=\"");
            oModel.Write(anTris[3 * t + 1]);
            oModel.Write("\" v3=\"");
            oModel.Write(anTris[3 * t + 2]);
            oModel.Write("\"/>\n");
        }
        oModel.Write(
            """
            </triangles></mesh></object></resources>
            <build><item objectid="1"/></build>
            </model>
            """);
    }

    public void SaveToStl(string strPath)
    {
        using var oFile = new FileStream(strPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var oWriter = new BinaryWriter(oFile);
        oWriter.Write(new byte[80]);
        oWriter.Write((uint)nTriangles);
        Span<Vector3> avecRecord = stackalloc Vector3[4];
        for (int t = 0; t < nTriangles; t++)
        {
            Vector3 a = vecVertex(anTris[3 * t]), b = vecVertex(anTris[3 * t + 1]), c = vecVertex(anTris[3 * t + 2]);
            Vector3 n = Vector3.Cross(b - a, c - a);
            float fLen = n.Length();
            avecRecord[0] = fLen > 0 ? n / fLen : Vector3.Zero;
            avecRecord[1] = a; avecRecord[2] = b; avecRecord[3] = c;
            foreach (Vector3 v in avecRecord)
            {
                oWriter.Write(v.X); oWriter.Write(v.Y); oWriter.Write(v.Z);
            }
            oWriter.Write((ushort)0);
        }
    }
}

/// <summary>
/// Quadric-error edge-collapse decimation (Garland-Heckbert, in the style of
/// sp4cerat's Fast-Quadric-Mesh-Simplification), run to a distance tolerance
/// instead of a target count.
///
/// A full-resolution core is ~100M triangles, too big for the per-vertex and
/// per-triangle working data in RAM at once, so the mesh is cut into spatial
/// chunks that are simplified in parallel. Any vertex used by triangles in
/// more than one chunk is locked, so chunks stitch back together exactly. A
/// second pass on a grid shifted by half a chunk then simplifies the seams
/// the first pass had to leave alone.
/// </summary>
static class MeshDecimator
{
    public static void Decimate(IndexedMesh oMesh, float fToleranceMM, float fChunkMM, int nThreads)
    {
        RunPass(oMesh, fToleranceMM, fChunkMM, Vector3.Zero, nThreads);
        RunPass(oMesh, fToleranceMM, fChunkMM, new Vector3(0.5f * fChunkMM), nThreads);
        oMesh.Compact();
    }

    static void RunPass(IndexedMesh oMesh, float fToleranceMM, float fChunkMM, Vector3 vecShift, int nThreads)
    {
        float[] afV = oMesh.afVerts;
        int[] anT = oMesh.anTris;
        int nT = oMesh.nTriangles, nV = oMesh.nVertices;

        Vector3 vecMin = new(float.MaxValue), vecMax = new(float.MinValue);
        for (int i = 0; i < nV; i++)
        {
            Vector3 v = oMesh.vecVertex(i);
            vecMin = Vector3.Min(vecMin, v);
            vecMax = Vector3.Max(vecMax, v);
        }

        // Chunk grid. The shift moves every chunk boundary by the same amount,
        // so shifted cells land where the previous pass's seams were.
        Vector3 vecOrigin = vecMin - vecShift;
        Vector3 vecCells = (vecMax - vecOrigin) / fChunkMM;
        int nX = (int)vecCells.X + 1, nY = (int)vecCells.Y + 1, nZ = (int)vecCells.Z + 1;
        int nChunks = nX * nY * nZ;

        var anChunkOfTri = new int[nT];
        Parallel.For(0, nT, t =>
        {
            Vector3 vecC = (oMesh.vecVertex(anT[3 * t]) + oMesh.vecVertex(anT[3 * t + 1]) + oMesh.vecVertex(anT[3 * t + 2])) / 3f;
            Vector3 vecCell = (vecC - vecOrigin) / fChunkMM;
            int x = Math.Clamp((int)vecCell.X, 0, nX - 1);
            int y = Math.Clamp((int)vecCell.Y, 0, nY - 1);
            int z = Math.Clamp((int)vecCell.Z, 0, nZ - 1);
            anChunkOfTri[t] = (z * nY + y) * nX + x;
        });

        // Owner chunk per vertex, or -2 if shared between chunks (locked).
        var anOwner = new int[nV];
        Array.Fill(anOwner, -1);
        for (int t = 0; t < nT; t++)
        {
            int c = anChunkOfTri[t];
            for (int k = 0; k < 3; k++)
            {
                ref int nOwner = ref anOwner[anT[3 * t + k]];
                if (nOwner == -1) nOwner = c;
                else if (nOwner != c) nOwner = -2;
            }
        }

        // Counting sort of triangles by chunk.
        var anStart = new int[nChunks + 1];
        foreach (int c in anChunkOfTri)
            anStart[c + 1]++;
        for (int c = 0; c < nChunks; c++)
            anStart[c + 1] += anStart[c];
        var anOrder = new int[nT];
        var anCursor = (int[])anStart.Clone();
        for (int t = 0; t < nT; t++)
            anOrder[anCursor[anChunkOfTri[t]]++] = t;
        anChunkOfTri = null!;

        var aanOut = new int[nChunks][];
        Parallel.For(0, nChunks, new ParallelOptions { MaxDegreeOfParallelism = nThreads }, c =>
        {
            if (anStart[c] == anStart[c + 1])
            {
                aanOut[c] = Array.Empty<int>();
                return;
            }

            var oChunk = new ChunkSimplifier(afV, anT, anOrder, anStart[c], anStart[c + 1], anOwner, fToleranceMM);
            oChunk.Run();
            aanOut[c] = oChunk.anWriteBack(afV);
        });

        var anNew = new int[aanOut.Sum(a => (long)a.Length)];
        int nAt = 0;
        foreach (int[] an in aanOut)
        {
            an.CopyTo(anNew, nAt);
            nAt += an.Length;
        }
        oMesh.anTris = anNew;
    }
}

[InlineArray(3)]
struct Int3 { int n; }

[InlineArray(3)]
struct Double3 { double d; }

/// <summary>Symmetric 4x4 quadric, upper triangle row by row.</summary>
struct Quadric
{
    public double m0, m1, m2, m3, m4, m5, m6, m7, m8, m9;

    public static Quadric oFromPlane(Vector3D n, double d, double w) => new()
    {
        m0 = w * n.X * n.X, m1 = w * n.X * n.Y, m2 = w * n.X * n.Z, m3 = w * n.X * d,
        m4 = w * n.Y * n.Y, m5 = w * n.Y * n.Z, m6 = w * n.Y * d,
        m7 = w * n.Z * n.Z, m8 = w * n.Z * d,
        m9 = w * d * d,
    };

    public static Quadric operator +(in Quadric a, in Quadric b) => new()
    {
        m0 = a.m0 + b.m0, m1 = a.m1 + b.m1, m2 = a.m2 + b.m2, m3 = a.m3 + b.m3, m4 = a.m4 + b.m4,
        m5 = a.m5 + b.m5, m6 = a.m6 + b.m6, m7 = a.m7 + b.m7, m8 = a.m8 + b.m8, m9 = a.m9 + b.m9,
    };

    /// <summary>Weighted sum of squared distances from p to every plane in the quadric.</summary>
    public readonly double dError(Vector3D p) =>
        m0 * p.X * p.X + 2 * m1 * p.X * p.Y + 2 * m2 * p.X * p.Z + 2 * m3 * p.X
      + m4 * p.Y * p.Y + 2 * m5 * p.Y * p.Z + 2 * m6 * p.Y
      + m7 * p.Z * p.Z + 2 * m8 * p.Z
      + m9;

    /// <summary>Point minimizing the error, if the 3x3 system is well-conditioned.</summary>
    public readonly bool bOptimum(double dWeight, out Vector3D p)
    {
        double dDet = Det3(m0, m1, m2, m1, m4, m5, m2, m5, m7);
        if (Math.Abs(dDet) < 1e-9 * dWeight * dWeight * dWeight)
        {
            p = default;
            return false;
        }

        p = new Vector3D(
            -Det3(m3, m1, m2, m6, m4, m5, m8, m5, m7) / dDet,
            -Det3(m0, m3, m2, m1, m6, m5, m2, m8, m7) / dDet,
            -Det3(m0, m1, m3, m1, m4, m6, m2, m5, m8) / dDet);
        return true;
    }

    static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}

readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator *(double s, Vector3D a) => new(s * a.X, s * a.Y, s * a.Z);
    public double Dot(Vector3D b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vector3D Cross(Vector3D b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length() => Math.Sqrt(Dot(this));
    public Vector3D Normalized() { double l = Length(); return l > 0 ? (1 / l) * this : this; }
}

/// <summary>Simplifies one chunk in local numbering, never moving a locked vertex.</summary>
sealed class ChunkSimplifier
{
    struct Vert
    {
        public Vector3D p;
        public Quadric q;
        public double dArea;
        public int nTStart, nTCount;
        public bool bLocked;
    }

    struct Tri
    {
        public Int3 v;
        public Double3 err;
        public double dMinErr;
        public Vector3D n;
        public bool bDeleted, bDirty;
    }

    struct Ref
    {
        public int nTri, nCorner;
    }

    // Ramp the collapse threshold up to the tolerance over this many passes,
    // so cheap collapses go first (a stand-in for a priority queue).
    const int nRampPasses = 12;
    const int nMaxPasses = 60;

    readonly Vert[] aVerts;
    readonly int[] anGlobal;
    Tri[] aTris;
    int nTris;
    readonly List<Ref> aRefs = new();
    readonly double dTol2;

    readonly List<int> anScratchA = new(), anScratchB = new();
    bool[] abDel0 = new bool[64], abDel1 = new bool[64];

    public ChunkSimplifier(float[] afV, int[] anT, int[] anOrder, int nFrom, int nTo, int[] anOwner, double dToleranceMM)
    {
        dTol2 = dToleranceMM * dToleranceMM;
        var oLocal = new Dictionary<int, int>();
        var anGlobalList = new List<int>();
        aTris = new Tri[nTo - nFrom];

        for (int i = nFrom; i < nTo; i++)
        {
            int t = anOrder[i];
            ref Tri tri = ref aTris[nTris++];
            for (int k = 0; k < 3; k++)
            {
                int g = anT[3 * t + k];
                if (!oLocal.TryGetValue(g, out int l))
                {
                    l = anGlobalList.Count;
                    oLocal[g] = l;
                    anGlobalList.Add(g);
                }
                tri.v[k] = l;
            }
            if (tri.v[0] == tri.v[1] || tri.v[1] == tri.v[2] || tri.v[2] == tri.v[0])
                tri.bDeleted = true;
        }

        anGlobal = anGlobalList.ToArray();
        aVerts = new Vert[anGlobal.Length];
        for (int l = 0; l < anGlobal.Length; l++)
        {
            int g = anGlobal[l];
            aVerts[l].p = new Vector3D(afV[3 * g], afV[3 * g + 1], afV[3 * g + 2]);
            aVerts[l].bLocked = anOwner[g] == -2;
        }
    }

    public void Run()
    {
        // Plane quadrics from the original triangles.
        for (int i = 0; i < nTris; i++)
        {
            ref Tri t = ref aTris[i];
            if (t.bDeleted)
                continue;
            Vector3D p0 = aVerts[t.v[0]].p;
            Vector3D vecCross = (aVerts[t.v[1]].p - p0).Cross(aVerts[t.v[2]].p - p0);
            double dArea = 0.5 * vecCross.Length();
            t.n = vecCross.Normalized();
            var q = Quadric.oFromPlane(t.n, -t.n.Dot(p0), dArea);
            for (int k = 0; k < 3; k++)
            {
                aVerts[t.v[k]].q = aVerts[t.v[k]].q + q;
                aVerts[t.v[k]].dArea += dArea;
            }
        }

        RebuildRefs(bFirst: true);
        for (int i = 0; i < nTris; i++)
            UpdateErrors(ref aTris[i]);

        for (int nPass = 0; nPass < nMaxPasses; nPass++)
        {
            if (nPass > 0 && nPass % 5 == 0)
                RebuildRefs(bFirst: false);

            for (int i = 0; i < nTris; i++)
                aTris[i].bDirty = false;

            double dThreshold = dTol2 * Math.Pow(2, Math.Min(0, nPass - nRampPasses));
            int nCollapsed = 0;

            for (int i = 0; i < nTris; i++)
            {
                ref Tri t = ref aTris[i];
                if (t.bDeleted || t.bDirty || t.dMinErr > dThreshold)
                    continue;

                for (int j = 0; j < 3; j++)
                {
                    if (t.err[j] > dThreshold)
                        continue;

                    int i0 = t.v[j], i1 = t.v[(j + 1) % 3];
                    // Both ends must be chunk-local. Collapsing into a locked
                    // survivor used to be allowed - it doesn't move, so the
                    // geometry stays consistent across the seam - but the link
                    // condition below can only see THIS chunk's triangles, and
                    // a locked vertex has triangles in other chunks by
                    // definition. If the survivor is adjacent to one of the
                    // merged vertex's neighbours through a triangle we can't
                    // see, the check passes on incomplete information and the
                    // collapse creates a duplicate edge. That produced exactly
                    // one non-manifold edge in 1.5M on the real part. The
                    // half-shifted second pass unlocks these vertices and
                    // simplifies the seams then, so little is given up.
                    if (aVerts[i0].bLocked || aVerts[i1].bLocked)
                        continue;

                    dCollapseError(i0, i1, out Vector3D p);
                    ref Vert v0 = ref aVerts[i0];
                    ref Vert v1 = ref aVerts[i1];

                    EnsureScratch(v0.nTCount, v1.nTCount);
                    if (bFlipped(p, i0, i1, v0, abDel0) || bFlipped(p, i1, i0, v1, abDel1))
                        continue;
                    if (!bLinkConditionHolds(i0, i1))
                        continue;

                    v0.p = p;
                    v0.q = v0.q + v1.q;
                    v0.dArea += v1.dArea;
                    int nStart = aRefs.Count;
                    UpdateTriangles(i0, v0, abDel0);
                    UpdateTriangles(i0, v1, abDel1);
                    int nCount = aRefs.Count - nStart;

                    // Reuse v0's slot in the ref list if the new fan fits.
                    if (nCount <= v0.nTCount)
                    {
                        for (int k = 0; k < nCount; k++)
                            aRefs[v0.nTStart + k] = aRefs[nStart + k];
                        CollectionsMarshal.SetCount(aRefs, nStart);
                    }
                    else
                    {
                        v0.nTStart = nStart;
                    }
                    v0.nTCount = nCount;
                    nCollapsed++;
                    break;
                }
            }

            if (nPass >= nRampPasses && nCollapsed == 0)
                break;
        }
    }

    /// <summary>Writes moved vertex positions to the shared array and returns the surviving triangles in global ids.</summary>
    public int[] anWriteBack(float[] afV)
    {
        for (int l = 0; l < aVerts.Length; l++)
        {
            if (aVerts[l].bLocked)
                continue;
            int g = anGlobal[l];
            afV[3 * g] = (float)aVerts[l].p.X;
            afV[3 * g + 1] = (float)aVerts[l].p.Y;
            afV[3 * g + 2] = (float)aVerts[l].p.Z;
        }

        var anOut = new List<int>(3 * nTris);
        for (int i = 0; i < nTris; i++)
        {
            if (aTris[i].bDeleted)
                continue;
            for (int k = 0; k < 3; k++)
                anOut.Add(anGlobal[aTris[i].v[k]]);
        }
        return anOut.ToArray();
    }

    Vector3D vecNormal(in Tri t)
    {
        Vector3D p0 = aVerts[t.v[0]].p;
        return (aVerts[t.v[1]].p - p0).Cross(aVerts[t.v[2]].p - p0).Normalized();
    }

    void UpdateErrors(ref Tri t)
    {
        if (t.bDeleted)
            return;
        for (int j = 0; j < 3; j++)
            t.err[j] = dCollapseError(t.v[j], t.v[(j + 1) % 3], out _);
        t.dMinErr = Math.Min(t.err[0], Math.Min(t.err[1], t.err[2]));
    }

    double dCollapseError(int i0, int i1, out Vector3D p)
    {
        ref Vert v0 = ref aVerts[i0];
        ref Vert v1 = ref aVerts[i1];
        Quadric q = v0.q + v1.q;
        // Area-weighted mean squared distance: the tolerance bounds the RMS
        // distance over the merged patch. A plain sum grows with every merge,
        // and the voxel mesh's own ~0.01mm noise then blocks almost all of them.
        double dArea = Math.Max(v0.dArea + v1.dArea, 1e-12);

        if (v0.bLocked && v1.bLocked)
        {
            p = v0.p;
            return double.MaxValue;
        }
        if (v0.bLocked || v1.bLocked)
        {
            p = v0.bLocked ? v0.p : v1.p;
            return q.dError(p) / dArea;
        }

        // On flat or cylindrical patches the optimum is ill-defined and can
        // land far off along the surface, so only trust it near the edge.
        Vector3D pMid = 0.5 * (v0.p + v1.p);
        if (q.bOptimum(dArea, out p) && (p - pMid).Length() <= (v1.p - v0.p).Length())
            return q.dError(p) / dArea;

        double e0 = q.dError(v0.p) / dArea, e1 = q.dError(v1.p) / dArea, eMid = q.dError(pMid) / dArea;
        if (e0 <= e1 && e0 <= eMid) { p = v0.p; return e0; }
        if (e1 <= eMid) { p = v1.p; return e1; }
        p = pMid;
        return eMid;
    }

    void EnsureScratch(int n0, int n1)
    {
        if (abDel0.Length < n0) abDel0 = new bool[2 * n0];
        if (abDel1.Length < n1) abDel1 = new bool[2 * n1];
    }

    /// <summary>
    /// True if moving vertex i0 to p would flip or squash one of its
    /// triangles. Marks the triangles that the collapse deletes (those that
    /// also contain i1) in abDeleted.
    /// </summary>
    bool bFlipped(Vector3D p, int i0, int i1, in Vert v0, bool[] abDeleted)
    {
        for (int k = 0; k < v0.nTCount; k++)
        {
            Ref r = aRefs[v0.nTStart + k];
            ref Tri t = ref aTris[r.nTri];
            if (t.bDeleted)
                continue;

            int id1 = t.v[(r.nCorner + 1) % 3];
            int id2 = t.v[(r.nCorner + 2) % 3];
            if (id1 == i1 || id2 == i1)
            {
                abDeleted[k] = true;
                continue;
            }

            Vector3D d1 = (aVerts[id1].p - p).Normalized();
            Vector3D d2 = (aVerts[id2].p - p).Normalized();
            if (Math.Abs(d1.Dot(d2)) > 0.999)
                return true;

            Vector3D n = d1.Cross(d2).Normalized();
            abDeleted[k] = false;
            if (n.Dot(t.n) < 0.2)
                return true;
        }
        return false;
    }

    /// <summary>
    /// An edge collapse keeps the surface manifold only if the two endpoints
    /// share exactly the two neighbours opposite the edge.
    /// </summary>
    bool bLinkConditionHolds(int i0, int i1)
    {
        anScratchA.Clear();
        CollectNeighbours(i0, i1, anScratchA);
        anScratchB.Clear();
        CollectNeighbours(i1, i0, anScratchB);

        int nShared = 0;
        foreach (int n in anScratchB)
            if (anScratchA.Contains(n))
                nShared++;
        return nShared == 2;
    }

    void CollectNeighbours(int iVert, int iExclude, List<int> anOut)
    {
        ref Vert v = ref aVerts[iVert];
        for (int k = 0; k < v.nTCount; k++)
        {
            Ref r = aRefs[v.nTStart + k];
            ref Tri t = ref aTris[r.nTri];
            if (t.bDeleted)
                continue;
            for (int c = 0; c < 3; c++)
            {
                int n = t.v[c];
                if (n != iVert && n != iExclude && !anOut.Contains(n))
                    anOut.Add(n);
            }
        }
    }

    void UpdateTriangles(int i0, in Vert v, bool[] abDeleted)
    {
        for (int k = 0; k < v.nTCount; k++)
        {
            Ref r = aRefs[v.nTStart + k];
            ref Tri t = ref aTris[r.nTri];
            if (t.bDeleted)
                continue;
            if (abDeleted[k])
            {
                t.bDeleted = true;
                continue;
            }

            t.v[r.nCorner] = i0;
            t.bDirty = true;
            t.n = vecNormal(t);
            UpdateErrors(ref t);
            aRefs.Add(r);
        }
    }

    void RebuildRefs(bool bFirst)
    {
        if (!bFirst)
        {
            int nKept = 0;
            for (int i = 0; i < nTris; i++)
                if (!aTris[i].bDeleted)
                    aTris[nKept++] = aTris[i];
            nTris = nKept;
        }

        for (int l = 0; l < aVerts.Length; l++)
        {
            aVerts[l].nTStart = 0;
            aVerts[l].nTCount = 0;
        }
        for (int i = 0; i < nTris; i++)
        {
            if (aTris[i].bDeleted)
                continue;
            for (int k = 0; k < 3; k++)
                aVerts[aTris[i].v[k]].nTCount++;
        }

        int nAt = 0;
        for (int l = 0; l < aVerts.Length; l++)
        {
            aVerts[l].nTStart = nAt;
            nAt += aVerts[l].nTCount;
            aVerts[l].nTCount = 0;
        }

        aRefs.Clear();
        CollectionsMarshal.SetCount(aRefs, nAt);
        for (int i = 0; i < nTris; i++)
        {
            if (aTris[i].bDeleted)
                continue;
            for (int k = 0; k < 3; k++)
            {
                ref Vert v = ref aVerts[aTris[i].v[k]];
                aRefs[v.nTStart + v.nTCount++] = new Ref { nTri = i, nCorner = k };
            }
        }

        if (!bFirst)
            return;

        // Lock any vertex on an open edge of this chunk (an edge only one
        // triangle uses). Chunk seams are already locked by ownership; this
        // also covers holes in the input.
        for (int l = 0; l < aVerts.Length; l++)
        {
            anScratchA.Clear();
            anScratchB.Clear();
            ref Vert v = ref aVerts[l];
            for (int k = 0; k < v.nTCount; k++)
            {
                ref Tri t = ref aTris[aRefs[v.nTStart + k].nTri];
                for (int c = 0; c < 3; c++)
                {
                    int n = t.v[c];
                    if (n == l)
                        continue;
                    int iAt = anScratchA.IndexOf(n);
                    if (iAt < 0) { anScratchA.Add(n); anScratchB.Add(1); }
                    else anScratchB[iAt]++;
                }
            }
            for (int i = 0; i < anScratchA.Count; i++)
                if (anScratchB[i] == 1)
                {
                    v.bLocked = true;
                    aVerts[anScratchA[i]].bLocked = true;
                }
        }
    }
}
