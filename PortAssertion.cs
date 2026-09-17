using System.Numerics;
using PicoGK;

namespace TpmsHrv;

public enum EFace { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

/// <summary>
/// Classifies each port group's centroid against the volume's bounding box.
/// Used to pick which face to grid-sample in Checkpoint 7b's open-area
/// survey - that's the check that actually catches a plug wired to the
/// wrong stream now. (This used to also assert a fixed face-to-role table,
/// e.g. +X must be SupplyIn; dropped once the real ERV enclosure showed two
/// ports sharing one face, which that one-role-per-face model can't express.
/// Revisit with a real placement rule once more real parts exist to
/// generalize from.)
/// </summary>
public static class PortAssertion
{
    public static EFace eClassifyFace(Vector3 vecCentroid, BBox3 oBBox)
    {
        Vector3 vecCenter = oBBox.vecCenter();
        Vector3 vecHalfExtent = oBBox.vecSize() * 0.5f;
        Vector3 vecRel = vecCentroid - vecCenter;

        float fFracX = MathF.Abs(vecRel.X) / vecHalfExtent.X;
        float fFracY = MathF.Abs(vecRel.Y) / vecHalfExtent.Y;
        float fFracZ = MathF.Abs(vecRel.Z) / vecHalfExtent.Z;

        if (fFracX >= fFracY && fFracX >= fFracZ)
            return vecRel.X >= 0 ? EFace.PlusX : EFace.MinusX;

        if (fFracY >= fFracX && fFracY >= fFracZ)
            return vecRel.Y >= 0 ? EFace.PlusY : EFace.MinusY;

        return vecRel.Z >= 0 ? EFace.PlusZ : EFace.MinusZ;
    }
}
