using System.Numerics;
using PicoGK;
using TpmsHrv;

// Headless Library (no viewer, no blocking on a window close) - this session's
// checkpoints are verified by console assertions and exported STLs, not by
// eyeballing the built-in viewer.
using Library oLib = new(Params.fVoxelSizeMM);

RunCheckpoint0(oLib);

RunSession(oLib, "fixtures/test_cube_100mm.obj", "out/test_cube_100mm");
RunSession(oLib, "fixtures/test_cube_filleted_100mm.obj", "out/test_cube_filleted_100mm");

Console.WriteLine("\nAll sessions complete.");

// ---------------------------------------------------------------------------

static void RunCheckpoint0(Library oLib)
{
    Directory.CreateDirectory("out");
    Voxels voxSphere = Voxels.voxSphere(oLib, Vector3.Zero, 25f);
    voxSphere.mshAsMesh().SaveToStlFile("out/checkpoint0_sphere.stl");
    Console.WriteLine("[Checkpoint 0] wrote out/checkpoint0_sphere.stl - open it in a slicer to confirm the toolchain works.");
}

static void RunSession(Library oLib, string strObjPath, string strOutDir)
{
    Directory.CreateDirectory(strOutDir);
    Console.WriteLine($"\n=== {Path.GetFileName(strObjPath)} ===");

    // ---- Task 1: OBJ/MTL parse ----
    List<ObjGroup> oGroupsRaw = ObjImporter.oLoad(strObjPath);
    (Vector3 vecMinRaw, Vector3 vecMaxRaw) = ObjImporter.oBoundingBox(oGroupsRaw);
    int nTriTotal = oGroupsRaw.Sum(g => g.oTris.Count);
    Console.WriteLine($"[Checkpoint 1] groups={oGroupsRaw.Count} triangles={nTriTotal} " +
                       $"bbox(raw units)=({vecMinRaw.X:F4},{vecMinRaw.Y:F4},{vecMinRaw.Z:F4}) .. " +
                       $"({vecMaxRaw.X:F4},{vecMaxRaw.Y:F4},{vecMaxRaw.Z:F4})");

    // ---- Task 2: resolve units/orientation ----
    List<ObjGroup> oGroups = oGroupsRaw.Select(g => new ObjGroup
    {
        strName = g.strName,
        vecKd = g.vecKd,
        oTris = g.oTris.Select(t => new Tri
        {
            vecA = t.vecA * Params.fImportScale,
            vecB = t.vecB * Params.fImportScale,
            vecC = t.vecC * Params.fImportScale,
        }).ToList(),
    }).ToList();

    (Vector3 vecMin, Vector3 vecMax) = ObjImporter.oBoundingBox(oGroups);
    Vector3 vecSizeMM = vecMax - vecMin;
    Console.WriteLine($"[Checkpoint 2] bbox(mm) = {vecSizeMM.X:F2} x {vecSizeMM.Y:F2} x {vecSizeMM.Z:F2}");

    // ---- Task 3: palette match ----
    Console.WriteLine("[Checkpoint 3] group                                      Kd                       role          tris");
    var oPortByGroup = new Dictionary<ObjGroup, EPort>();
    foreach (ObjGroup g in oGroups)
    {
        EPort ePort = PortClassifier.ePortFromKd(g.vecKd, Params.fColorTolerance);
        oPortByGroup[g] = ePort;
        Console.WriteLine($"  {g.strName,-45} ({g.vecKd.X:F3},{g.vecKd.Y:F3},{g.vecKd.Z:F3})  {ePort,-12}  {g.oTris.Count}");
    }

    // ---- Task 4: groups -> mesh -> voxels ----
    // PicoGK's RenderMesh (used by the Voxels(Mesh) ctor) requires a CLOSED
    // surface. Each material group alone is an open patch, so we build one
    // Mesh per group only for the debug STL export, then merge every group's
    // triangles into a single combined mesh - which together IS closed - and
    // voxelize that once. (Voxelizing each open group separately and then
    // boolean-unioning the results is undefined per PicoGK's own docs; it
    // happened to look fine on the flat-faced cube by luck and fell apart on
    // the filleted one, which is what caught it.)
    Mesh oCombinedMesh = new(oLib);
    foreach (ObjGroup g in oGroups)
    {
        Mesh oGroupMesh = new(oLib);
        foreach (Tri t in g.oTris)
        {
            oGroupMesh.nAddTriangle(t.vecA, t.vecB, t.vecC);
            oCombinedMesh.nAddTriangle(t.vecA, t.vecB, t.vecC);
        }

        oGroupMesh.SaveToStlFile(Path.Combine(strOutDir, $"group_{strSanitize(g.strName)}.stl"));
    }

    Voxels voxVolume = new(oCombinedMesh);
    voxVolume.CalculateProperties(out float fVolumeMM3, out BBox3 oBBoxVolume);
    float fExpectedVolumeMM3 = vecSizeMM.X * vecSizeMM.Y * vecSizeMM.Z;
    float fPctOff = 100f * MathF.Abs(fVolumeMM3 - fExpectedVolumeMM3) / fExpectedVolumeMM3;
    Console.WriteLine($"[Checkpoint 4] voxel volume = {fVolumeMM3:F0} mm^3 (expected ~{fExpectedVolumeMM3:F0}, {fPctOff:F1}% off)");
    if (fPctOff > 5f)
        Console.WriteLine("  WARNING: >5% off expected volume - check triangle winding before continuing.");

    voxVolume.mshAsMesh().SaveToStlFile(Path.Combine(strOutDir, "volume.stl"));

    // ---- Task 5: per-port sealing slabs ----
    var oSlabByGroup = new Dictionary<ObjGroup, Voxels>();
    foreach (ObjGroup g in oGroups)
    {
        if (oPortByGroup[g] == EPort.None)
            continue;

        Voxels voxSlab = SlabBuilder.voxSlabFromGroup(oLib, g, Params.fPortSealDepthMM);
        oSlabByGroup[g] = voxSlab;
        voxSlab.mshAsMesh().SaveToStlFile(Path.Combine(strOutDir, $"slab_{strSanitize(g.strName)}.stl"));
    }
    Console.WriteLine($"[Checkpoint 5] wrote {oSlabByGroup.Count} port slab STL(s) to {strOutDir}");

    // ---- Task 6: position assertion ----
    var oFaceByGroup = new Dictionary<ObjGroup, EFace>();
    foreach (ObjGroup g in oGroups)
    {
        EPort ePort = oPortByGroup[g];
        if (ePort == EPort.None)
            continue;

        Vector3 vecCentroid = ObjImporter.vecCentroid(g);
        EFace eFace = PortAssertion.eClassifyFace(vecCentroid, oBBoxVolume);
        oFaceByGroup[g] = eFace;
        PortAssertion.AssertPlacement(g.strName, ePort, eFace);
    }
    Console.WriteLine("[Checkpoint 6] all colored ports sit on their expected face - OK");

    // ---- Task 7: stub gyroid + end-to-end boolean ----
    var oRawGyroid = new GyroidRawImplicit(Params.fCellSizeMM);
    Voxels voxMembrane = voxVolume.voxIntersectImplicit(new GyroidSheetImplicit(oRawGyroid, Params.fWallThicknessMM));
    Voxels voxStreamA = voxVolume.voxIntersectImplicit(new GyroidStreamAImplicit(oRawGyroid, Params.fWallThicknessMM));
    Voxels voxStreamB = voxVolume.voxIntersectImplicit(new GyroidStreamBImplicit(oRawGyroid, Params.fWallThicknessMM));

    // Supply* <-> stream A, Exhaust* <-> stream B. Which of the raw gyroid's
    // two labyrinths (A or B) gets called "supply" is a free choice - the raw
    // field obeys d(-p) = -d(p) (see GyroidRawImplicit), a point-inversion
    // symmetry that swaps A<->B while also swapping PlusX<->MinusX and
    // PlusZ<->MinusZ, so flipping the assignment just yields a mirrored,
    // equally-valid core. That symmetry does NOT guarantee a port actually
    // has open area for its assigned stream, though - that depends on the
    // gyroid's phase relative to where the port sits, which is meaningless to
    // check on these fixtures (each "port" is a whole cube face) but becomes
    // the real constraint once the real ERV volume's small port bores are in
    // play. Checkpoint 7b below is a standing survey for that.
    foreach ((ObjGroup g, Voxels voxSlab) in oSlabByGroup)
    {
        EPort ePort = oPortByGroup[g];
        Voxels voxOtherStream = (ePort is EPort.SupplyIn or EPort.SupplyOut) ? voxStreamB : voxStreamA;
        voxMembrane.BoolAdd(voxSlab.voxBoolIntersect(voxOtherStream));
    }

    voxMembrane.mshAsMesh().SaveToStlFile(Path.Combine(strOutDir, "membrane.stl"));
    Console.WriteLine($"[Checkpoint 7] wrote membrane.stl to {strOutDir}");

    // ---- Task 7b: survey each port face for its assigned stream's open area ----
    // Samples the raw field on a grid across each port's face plane and
    // reports what fraction is stream A / stream B / membrane. On these test
    // cubes every face gets a healthy mix of both by construction (the whole
    // face is the "port"), so this always passes here - its job is to be the
    // check that actually bites once ports are small bores on the real
    // volume and gyroid phase can leave one sitting entirely on membrane.
    float fHalfThicknessMM = 0.5f * Params.fWallThicknessMM;
    foreach (ObjGroup g in oGroups)
    {
        EPort ePort = oPortByGroup[g];
        if (ePort == EPort.None)
            continue;

        EFace eFace = oFaceByGroup[g];
        (float fFracA, float fFracB, float fFracMembrane) = oSurveyGyroidAtFace(
            eFace, oBBoxVolume, oRawGyroid, fHalfThicknessMM, Params.fVoxelSizeMM * 2f, 64);

        float fFracOpen = (ePort is EPort.SupplyIn or EPort.SupplyOut) ? fFracA : fFracB;
        Console.WriteLine($"  {g.strName,-45} face={eFace,-7} openStream={fFracOpen * 100f:F1}%  membrane={fFracMembrane * 100f:F1}%");

        if (fFracOpen < 0.05f)
        {
            throw new Exception(
                $"{g.strName}: assigned stream has only {fFracOpen * 100f:F1}% open area at {eFace} " +
                "- gyroid phase is misaligned with this port.");
        }
    }
    Console.WriteLine("[Checkpoint 7b] every port's assigned stream has adequate open area at its face - OK");
}

static string strSanitize(string strName)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        strName = strName.Replace(c, '_');
    return strName;
}

/// <summary>
/// Samples the raw gyroid field on an nGridSteps x nGridSteps grid across one
/// face of oBBox (inset fInsetMM from the boundary so samples land inside the
/// solid rather than exactly on it) and returns the fraction of samples that
/// fall on stream A's side, stream B's side, and the membrane band.
/// </summary>
static (float fFracA, float fFracB, float fFracMembrane) oSurveyGyroidAtFace(
    EFace eFace, BBox3 oBBox, GyroidRawImplicit oRaw, float fHalfThicknessMM,
    float fInsetMM, int nGridSteps)
{
    Vector3 vecMin = oBBox.vecMin, vecMax = oBBox.vecMax;
    int nA = 0, nB = 0, nMembrane = 0;

    for (int i = 0; i < nGridSteps; i++)
    {
        float fU = (i + 0.5f) / nGridSteps;
        for (int j = 0; j < nGridSteps; j++)
        {
            float fV = (j + 0.5f) / nGridSteps;
            Vector3 vec = eFace switch
            {
                EFace.PlusX  => new Vector3(vecMax.X - fInsetMM, fLerp(vecMin.Y, vecMax.Y, fU), fLerp(vecMin.Z, vecMax.Z, fV)),
                EFace.MinusX => new Vector3(vecMin.X + fInsetMM, fLerp(vecMin.Y, vecMax.Y, fU), fLerp(vecMin.Z, vecMax.Z, fV)),
                EFace.PlusY  => new Vector3(fLerp(vecMin.X, vecMax.X, fU), vecMax.Y - fInsetMM, fLerp(vecMin.Z, vecMax.Z, fV)),
                EFace.MinusY => new Vector3(fLerp(vecMin.X, vecMax.X, fU), vecMin.Y + fInsetMM, fLerp(vecMin.Z, vecMax.Z, fV)),
                EFace.PlusZ  => new Vector3(fLerp(vecMin.X, vecMax.X, fU), fLerp(vecMin.Y, vecMax.Y, fV), vecMax.Z - fInsetMM),
                EFace.MinusZ => new Vector3(fLerp(vecMin.X, vecMax.X, fU), fLerp(vecMin.Y, vecMax.Y, fV), vecMin.Z + fInsetMM),
                _ => throw new ArgumentOutOfRangeException(nameof(eFace)),
            };

            float d = oRaw.d(vec);
            if (d > fHalfThicknessMM)
                nA++;
            else if (d < -fHalfThicknessMM)
                nB++;
            else
                nMembrane++;
        }
    }

    int nTotal = nGridSteps * nGridSteps;
    return ((float)nA / nTotal, (float)nB / nTotal, (float)nMembrane / nTotal);
}

static float fLerp(float a, float b, float t) => a + (b - a) * t;
