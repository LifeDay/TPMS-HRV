using System.Numerics;
using PicoGK;

namespace TpmsHrv;

/// <summary>
/// Builds a sealing slab by extruding a port's face group inward into one
/// closed prism: the group itself as the near cap, a translated copy as the
/// far cap, and side walls along the group's boundary edges only.
///
/// Side walls must not be built along interior edges. A prism per triangle
/// gives the same inside/outside, but the coincident internal walls leave
/// near-zero distance values inside the solid, and anything that reads the
/// distance field (Offset) then erodes from those walls too. On the real
/// part's fan-triangulated port discs that ate ~40% of the skin cut-outs at
/// 0.25mm voxels.
///
/// Also used for the skin's port cut-outs, which need the prism to start
/// outside the part (fOutwardMM) so eroding it doesn't pull its near cap
/// back inside the surface.
/// </summary>
public static class SlabBuilder
{
    const float fAreaEpsilon = 1e-9f;

    public static Voxels voxSlabFromGroup(Library oLib, ObjGroup oGroup, float fDepthMM, float fOutwardMM = 0f)
    {
        Vector3 vecAvgNormal = vecAreaWeightedAverageNormal(oGroup);
        Vector3 vecStart = vecAvgNormal * fOutwardMM;
        Vector3 vecExtrude = -vecAvgNormal * (fDepthMM + fOutwardMM); // inward

        Mesh oMesh = new(oLib);

        // Directed edge -> use count. With every triangle wound the same way,
        // an interior edge shows up once in each direction; a boundary edge
        // shows up once in one direction only.
        var oEdges = new Dictionary<(Vector3, Vector3), int>();

        foreach (Tri t in oGroup.oTris)
        {
            Vector3 vecA = t.vecA + vecStart, vecB = t.vecB + vecStart, vecC = t.vecC + vecStart;

            Vector3 vecCross = Vector3.Cross(vecB - vecA, vecC - vecA);
            if (vecCross.Length() < fAreaEpsilon)
                continue; // degenerate triangle - skip

            // Force winding to oppose the extrusion direction, regardless of
            // the source file's winding, so the prism is outward-wound.
            if (Vector3.Dot(vecCross, vecExtrude) > 0)
                (vecB, vecC) = (vecC, vecB);

            // Near cap: original winding (normal already opposes vecExtrude).
            oMesh.nAddTriangle(vecA, vecB, vecC);
            // Far cap: reversed winding (normal now points along vecExtrude).
            oMesh.nAddTriangle(vecA + vecExtrude, vecC + vecExtrude, vecB + vecExtrude);

            AddEdge(oEdges, vecA, vecB);
            AddEdge(oEdges, vecB, vecC);
            AddEdge(oEdges, vecC, vecA);
        }

        // Side walls on boundary edges. Order (p, p2, q2, q) keeps the outward
        // normal consistent with the caps above - see Task 5 derivation.
        foreach (((Vector3 vecP, Vector3 vecQ), int nCount) in oEdges)
        {
            if (nCount <= 0)
                continue;

            Vector3 vecP2 = vecP + vecExtrude, vecQ2 = vecQ + vecExtrude;
            oMesh.nAddTriangle(vecP, vecP2, vecQ2);
            oMesh.nAddTriangle(vecP, vecQ2, vecQ);
        }

        return new Voxels(oMesh);
    }

    // Counts p->q as +1 and q->p as -1 under one key, so a matched interior
    // edge nets to zero and a boundary edge keeps its own direction.
    static void AddEdge(Dictionary<(Vector3, Vector3), int> oEdges, Vector3 vecP, Vector3 vecQ)
    {
        if (oEdges.TryGetValue((vecQ, vecP), out int nReverse))
        {
            if (nReverse == 1) oEdges.Remove((vecQ, vecP));
            else oEdges[(vecQ, vecP)] = nReverse - 1;
            return;
        }

        oEdges[(vecP, vecQ)] = oEdges.GetValueOrDefault((vecP, vecQ)) + 1;
    }

    static Vector3 vecAreaWeightedAverageNormal(ObjGroup oGroup)
    {
        Vector3 vecSum = Vector3.Zero;
        foreach (Tri t in oGroup.oTris)
            vecSum += Vector3.Cross(t.vecB - t.vecA, t.vecC - t.vecA); // magnitude = 2*area

        return vecSum.LengthSquared() > 0 ? Vector3.Normalize(vecSum) : Vector3.Zero;
    }
}
