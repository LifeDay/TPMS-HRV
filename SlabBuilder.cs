using System.Numerics;
using PicoGK;

namespace TpmsHrv;

/// <summary>
/// Builds a sealing slab as a union of per-triangle prisms extruded inward
/// from a port's face group. Overlapping prisms are fine - voxel booleans
/// don't care about mesh topology between prisms, only that each prism is
/// individually watertight.
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

        foreach (Tri t in oGroup.oTris)
        {
            Vector3 vecA = t.vecA + vecStart, vecB = t.vecB + vecStart, vecC = t.vecC + vecStart;

            Vector3 vecCross = Vector3.Cross(vecB - vecA, vecC - vecA);
            float fArea2 = vecCross.Length();
            if (fArea2 < fAreaEpsilon)
                continue; // degenerate triangle - skip

            Vector3 vecN = vecCross / fArea2;

            // Force winding to oppose the extrusion direction, regardless of
            // the source file's winding, so every prism is outward-wound.
            if (Vector3.Dot(vecN, vecExtrude) > 0)
                (vecB, vecC) = (vecC, vecB);

            Vector3 vecA2 = vecA + vecExtrude;
            Vector3 vecB2 = vecB + vecExtrude;
            Vector3 vecC2 = vecC + vecExtrude;

            // Near cap: original winding (normal already opposes vecExtrude).
            oMesh.nAddTriangle(vecA, vecB, vecC);
            // Far cap: reversed winding (normal now points along vecExtrude).
            oMesh.nAddTriangle(vecA2, vecC2, vecB2);

            // Three side quads, each split as a fan from the "near" edge vertex.
            // Order (p, p2, q2, q) keeps the outward normal consistent with the
            // caps above - see Task 5 derivation.
            AddSideQuad(oMesh, vecA, vecA2, vecB2, vecB);
            AddSideQuad(oMesh, vecB, vecB2, vecC2, vecC);
            AddSideQuad(oMesh, vecC, vecC2, vecA2, vecA);
        }

        return new Voxels(oMesh);
    }

    static void AddSideQuad(Mesh oMesh, Vector3 vecP, Vector3 vecP2, Vector3 vecQ2, Vector3 vecQ)
    {
        oMesh.nAddTriangle(vecP, vecP2, vecQ2);
        oMesh.nAddTriangle(vecP, vecQ2, vecQ);
    }

    static Vector3 vecAreaWeightedAverageNormal(ObjGroup oGroup)
    {
        Vector3 vecSum = Vector3.Zero;
        foreach (Tri t in oGroup.oTris)
            vecSum += Vector3.Cross(t.vecB - t.vecA, t.vecC - t.vecA); // magnitude = 2*area

        return vecSum.LengthSquared() > 0 ? Vector3.Normalize(vecSum) : Vector3.Zero;
    }
}
