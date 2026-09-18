using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PicoGK;
using TpmsHrv;

// Usage: dotnet run -c Release -- [--voxel <mm>] [--only <name>]... [--export stl|3mf|vdb|none] [--decimate <mm>]
//   --voxel   voxel size in mm (default Params.fVoxelSizeMM; production is 0.15)
//   --only    run only parts whose OBJ path contains <name> (repeatable)
//   --export  core output format: stl (default), 3mf, vdb, or none to skip it
//   --decimate  stl/3mf surface tolerance in mm (default 0.02); 0 keeps the raw voxel mesh
//   --cell    gyroid lambda in mm (default Params.fCellSizeMM). Triangle count
//             scales as 1/lambda^2, so this is the cheapest way to cut slicer
//             load without touching wall accuracy - it costs heat-exchange area
//   --wall    membrane thickness in mm (default Params.fWallThicknessMM)
//   --seal    port seal depth in mm (default 0.75 * --cell, which is where
//             Params.fPortSealDepthMM's 6.0 came from at the 8mm default)
Options oOpts = Options.oParse(args);

// Headless Library (no viewer, no blocking on a window close) - checkpoints
// are verified by console assertions and exported files, not by eyeballing
// the built-in viewer.
using Library oLib = new(oOpts.fVoxelSizeMM);
Console.WriteLine($"voxel size = {oOpts.fVoxelSizeMM} mm, export = {oOpts.strExport}");

RunCheckpoint0(oLib);

(string strObj, string strOut)[] aParts =
{
    ("fixtures/test_cube_100mm.obj", "out/test_cube_100mm"),
    ("fixtures/test_cube_filleted_100mm.obj", "out/test_cube_filleted_100mm"),
    ("real-HRV-volume/real-HRV-volume.obj", "out/real_hrv_volume"),
};

foreach ((string strObj, string strOut) in aParts)
{
    if (oOpts.astrOnly.Count > 0 && !oOpts.astrOnly.Any(s => strObj.Contains(s, StringComparison.OrdinalIgnoreCase)))
        continue;

    RunSession(oLib, oOpts, strObj, strOut);
}

Console.WriteLine("\nAll sessions complete.");

// ---------------------------------------------------------------------------

static void RunCheckpoint0(Library oLib)
{
    Directory.CreateDirectory("out");
    Voxels voxSphere = Voxels.voxSphere(oLib, Vector3.Zero, 25f);
    voxSphere.mshAsMesh().SaveToStlFile("out/checkpoint0_sphere.stl");
    Console.WriteLine("[Checkpoint 0] wrote out/checkpoint0_sphere.stl - open it in a slicer to confirm the toolchain works.");
}

static void RunSession(Library oLib, Options oOpts, string strObjPath, string strOutDir)
{
    Directory.CreateDirectory(strOutDir);
    Console.WriteLine($"\n=== {Path.GetFileName(strObjPath)} ===");
    var oTimer = new StageTimer();

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
    oTimer.Mark("import + voxelize volume");

    // ---- Task 5: per-port sealing slabs ----
    var oSlabByGroup = new Dictionary<ObjGroup, Voxels>();
    foreach (ObjGroup g in oGroups)
    {
        if (oPortByGroup[g] == EPort.None)
            continue;

        Voxels voxSlab = SlabBuilder.voxSlabFromGroup(oLib, g, oOpts.fSealDepth);
        oSlabByGroup[g] = voxSlab;
        voxSlab.mshAsMesh().SaveToStlFile(Path.Combine(strOutDir, $"slab_{strSanitize(g.strName)}.stl"));
    }
    Console.WriteLine($"[Checkpoint 5] wrote {oSlabByGroup.Count} port slab STL(s) to {strOutDir}");

    // ---- Task 6: classify each port to a face ----
    // No longer asserts a fixed face-to-role table (e.g. "+X must be
    // SupplyIn") - the real HRV enclosure has two ports sharing one face,
    // which that model can't express. Checkpoint 7b's open-area survey is
    // the check that actually validates stream/port wiring now; this step
    // just picks which face each port sits on for that survey to sample.
    var oFaceByGroup = new Dictionary<ObjGroup, EFace>();
    foreach (ObjGroup g in oGroups)
    {
        EPort ePort = oPortByGroup[g];
        if (ePort == EPort.None)
            continue;

        Vector3 vecCentroid = ObjImporter.vecCentroid(g);
        EFace eFace = PortAssertion.eClassifyFace(vecCentroid, oBBoxVolume);
        oFaceByGroup[g] = eFace;
        Console.WriteLine($"  {g.strName,-45} role={ePort,-12} face={eFace}");
    }
    Console.WriteLine($"[Checkpoint 6] classified {oFaceByGroup.Count} colored port(s) to a face");
    oTimer.Mark("port slabs");

    // ---- Task 7: gyroid membrane + port seals ----
    var oField = new GyroidField(oOpts.fCellSizeMM);
    Voxels voxCore = voxIntersectImplicit(oLib, voxVolume, new GyroidSheetImplicit(oField, oOpts.fWallThicknessMM));
    oTimer.Mark("membrane");

    // Supply* <-> stream A, Exhaust* <-> stream B. Which of the gyroid's two
    // labyrinths (A or B) gets called "supply" is a free choice - the field
    // obeys d(-p) = -d(p), a point-inversion symmetry that swaps A<->B while
    // also swapping PlusX<->MinusX and PlusZ<->MinusZ, so flipping the
    // assignment just yields a mirrored, equally-valid core. That symmetry
    // does NOT guarantee a port actually has open area for its assigned
    // stream - that depends on the gyroid's phase relative to where the port
    // sits. Checkpoint 7b below is a standing survey for that.
    //
    // Each seal is the other stream's labyrinth within the slab. Rendering the
    // stream implicit into (slab & volume) rather than into the whole volume
    // keeps the grid small - a full-volume stream grid is as big as the
    // membrane's, which matters at production voxel size.
    foreach ((ObjGroup g, Voxels voxSlab) in oSlabByGroup)
    {
        EPort ePort = oPortByGroup[g];
        IImplicit xOtherStream = (ePort is EPort.SupplyIn or EPort.SupplyOut)
            ? new GyroidStreamBImplicit(oField, oOpts.fWallThicknessMM)
            : new GyroidStreamAImplicit(oField, oOpts.fWallThicknessMM);

        using Voxels voxSlabInVolume = voxSlab.voxBoolIntersect(voxVolume);
        using Voxels voxSeal = voxIntersectImplicit(oLib, voxSlabInVolume, xOtherStream);
        voxCore.BoolAdd(voxSeal);
    }
    Console.WriteLine("[Checkpoint 7] membrane + port seals built");
    oTimer.Mark("port seals");

    // ---- Task 7b: survey each port face for its assigned stream's open area ----
    // Samples the field on a grid across each port's face plane and reports
    // what fraction is stream A / stream B / membrane. On the test cubes every
    // face gets a healthy mix of both by construction (the whole face is the
    // "port"); its job is to catch a port that gyroid phase leaves sitting
    // entirely on membrane.
    float fHalfThicknessMM = 0.5f * oOpts.fWallThicknessMM;
    foreach (ObjGroup g in oGroups)
    {
        EPort ePort = oPortByGroup[g];
        if (ePort == EPort.None)
            continue;

        EFace eFace = oFaceByGroup[g];
        (float fFracA, float fFracB, float fFracMembrane) = oSurveyGyroidAtFace(
            eFace, oBBoxVolume, oField, fHalfThicknessMM, oOpts.fVoxelSizeMM * 2f, 64);

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

    // ---- Task 8: exterior skin via erode-and-subtract ----
    // skin = volume - volume.offset(-t), minus a cut-out at each port so the
    // ports stay open. A cut is the port's prism, started 2t outside the
    // surface and run 2t + 1 voxel inside it, then eroded by t: that leaves it
    // t outside, t + 1 voxel deep (clears the skin), and pulled in t from the
    // port's edges. The edge pull-in matters where a port meets a neighbouring
    // face - without it the cut would also strip a t-wide strip of that
    // face's skin and leave a slit along the edge.
    float fSkinMM = Params.fSkinThicknessMM;
    Voxels voxSkin;
    using (Voxels voxInner = voxVolume.voxOffset(-fSkinMM))
        voxSkin = voxVolume.voxBoolSubtract(voxInner);
    var oCutByGroup = new Dictionary<ObjGroup, Voxels>();
    foreach (ObjGroup g in oSlabByGroup.Keys)
    {
        Voxels voxCut = SlabBuilder.voxSlabFromGroup(oLib, g, 2f * fSkinMM + oOpts.fVoxelSizeMM, 2f * fSkinMM);
        voxCut.Offset(-fSkinMM);
        oCutByGroup[g] = voxCut;
        voxSkin.BoolSubtract(voxCut);
    }
    voxCore.BoolAdd(voxSkin);
    oTimer.Mark("skin");

    RunSkinCheck(oGroups, oPortByGroup, voxVolume, voxSkin, fSkinMM);

    // ---- Task 9: wall thickness vs nominal ----
    RunWallThicknessCheck(oLib, oField, voxCore, voxVolume, oSlabByGroup.Values.Concat(oCutByGroup.Values).ToList(),
                          oBBoxVolume, fSkinMM, oOpts.fVoxelSizeMM, oOpts.fWallThicknessMM);
    oTimer.Mark("checks");

    // Everything but the core is done with. PicoGK grids live in native
    // memory the GC doesn't see, and at 0.15mm on the real volume leaving
    // them to the finalizer pushed the process past physical RAM.
    foreach (Voxels vox in oSlabByGroup.Values.Concat(oCutByGroup.Values).Append(voxSkin).Append(voxVolume))
        vox.Dispose();

    // ---- Export ----
    voxCore.CalculateProperties(out float fCoreVolumeMM3, out _);
    Console.WriteLine($"[Export] core solid volume = {fCoreVolumeMM3:F0} mm^3 ({100f * fCoreVolumeMM3 / fVolumeMM3:F1}% of envelope), " +
                      $"core grid = {voxCore.nMemUsage() / 1e6:F0} MB, library total = {oLib.nTotalMemUsage() / 1e6:F0} MB");

    string strCorePath = oOpts.strExport switch
    {
        "stl"  => Path.Combine(strOutDir, "core.stl"),
        "3mf"  => Path.Combine(strOutDir, "core.3mf"),
        "vdb"  => Path.Combine(strOutDir, "core.vdb"),
        _      => "",
    };

    string? strFailure = null;
    if (oOpts.strExport is "stl" or "3mf")
    {
        // The deviation check samples the core's SDF, which outlives the grid.
        using TrilinearSdf? oCoreSdf = oOpts.fDecimateMM > 0 ? new TrilinearSdf(oLib, voxCore) : null;

        // The mesh is several times the grid's size, so free the grid first.
        IndexedMesh oMesh;
        using (Mesh mshCore = voxCore.mshAsMesh())
        {
            voxCore.Dispose();
            oTimer.Mark("mesh");
            oMesh = IndexedMesh.oFromPicoGK(mshCore);
        }
        Console.WriteLine($"  core mesh: {oMesh.nTriangles} triangles, {oMesh.nVertices} vertices");

        if (oCoreSdf != null)
        {
            fRunSurfaceDeviation("voxel mesh", oMesh, oCoreSdf);
            // Check the marching-cubes mesh BEFORE decimating. Without this, a
            // defect PicoGK handed us is indistinguishable from one the
            // decimator introduced, and the decimator gets blamed for both.
            (long nRawEdges, long nRawOpen, long nRawNM, long nRawDegen) = oMesh.oTopologyCheck();
            Console.WriteLine($"  voxel mesh topology: {nRawEdges} edges, {nRawOpen} open, {nRawNM} non-manifold, {nRawDegen} degenerate");
            int nBefore = oMesh.nTriangles;
            double dRawVolume = oMesh.dSignedVolume();
            MeshDecimator.Decimate(oMesh, oOpts.fDecimateMM, fChunkMM: 30f, nThreads: Math.Max(1, Environment.ProcessorCount / 2));
            oTimer.Mark("decimate");
            Console.WriteLine($"[Checkpoint 10] decimated to {oOpts.fDecimateMM}mm: {oMesh.nTriangles} triangles " +
                              $"({100.0 * oMesh.nTriangles / nBefore:F1}% of {nBefore}), {oMesh.nVertices} vertices");
            (long nEdges, long nOpen, long nNonManifold, long nDegenerate) = oMesh.oTopologyCheck();
            Console.WriteLine($"  topology: {nEdges} edges, {nOpen} open, {nNonManifold} non-manifold, {nDegenerate} degenerate triangles");
            double dMeshVolume = oMesh.dSignedVolume();
            Console.WriteLine($"  mesh volume: {dMeshVolume:F0} mm^3 ({100 * (dMeshVolume / dRawVolume - 1):+0.000;-0.000}% vs voxel mesh, " +
                              $"{100 * (dMeshVolume / fCoreVolumeMM3 - 1):+0.00;-0.00}% vs voxel count)");
            // Measure before judging: a run that trips a hard check is exactly
            // when the deviation figure matters most, so it must not be
            // stranded behind the throw.
            float fMaxDev = fRunSurfaceDeviation("decimated", oMesh, oCoreSdf);

            // Checkpoint 9 measures the VOXEL core, which is then discarded -
            // what prints is this mesh. Decimation moves the surface, and a
            // wall only has half its thickness to give on each side before it
            // perforates. Warn well before that: half of the half-thickness.
            float fHalfWall = 0.5f * oOpts.fWallThicknessMM;
            if (fMaxDev > 0.5f * fHalfWall)
                Console.WriteLine($"  WARNING: max deviation {fMaxDev:F4}mm is {100f * fMaxDev / fHalfWall:F0}% of the wall's " +
                                  $"{fHalfWall:F3}mm half-thickness - the printed wall is no longer within Checkpoint 9's tolerance. " +
                                  $"Lower --decimate or raise --wall.");

            if (nOpen > 0 || nNonManifold > 0 || nDegenerate > 0)
                strFailure = $"not a closed manifold ({nOpen} open, {nNonManifold} non-manifold, {nDegenerate} degenerate)";
            else if (dMeshVolume <= 0)
                strFailure = "winds inward";
        }

        // A failing mesh still gets written, under a name that can't be mistaken
        // for output: these runs cost double-digit minutes, and whether a defect
        // actually matters is a question you answer by loading the thing.
        if (strFailure != null)
            strCorePath = Path.Combine(Path.GetDirectoryName(strCorePath)!,
                                       Path.GetFileNameWithoutExtension(strCorePath) + ".FAILED" + Path.GetExtension(strCorePath));

        if (oOpts.strExport == "3mf")
            oMesh.SaveTo3mf(strCorePath);
        else
            oMesh.SaveToStl(strCorePath);
    }
    else
    {
        if (oOpts.strExport == "vdb")
            voxCore.SaveToVdbFile(strCorePath);
        voxCore.Dispose();
    }

    if (strCorePath != "")
    {
        oTimer.Mark($"write {Path.GetFileName(strCorePath)}");
        Console.WriteLine($"  wrote {strCorePath} ({new FileInfo(strCorePath).Length / 1e6:F0} MB)");
    }

    oTimer.Report();

    // Thrown last, so the timings and the salvaged file are both on record.
    if (strFailure != null)
        throw new Exception($"[Checkpoint 10] decimated mesh {strFailure} - wrote {strCorePath} for inspection");
}

/// <summary>
/// Samples inward from every triangle (area-weighted) at half the skin depth
/// and checks the skin is there on non-port surfaces and absent on ports.
/// Samples within t of a port's edge land on the deliberately-kept rim, so
/// the port fraction is expected to be small but not zero.
/// </summary>
static void RunSkinCheck(List<ObjGroup> oGroups, Dictionary<ObjGroup, EPort> oPortByGroup,
                         Voxels voxVolume, Voxels voxSkin, float fSkinMM)
{
    float fBodyArea = 0, fBodyCovered = 0, fPortArea = 0, fPortCovered = 0;
    foreach (ObjGroup g in oGroups)
    {
        bool bPort = oPortByGroup[g] != EPort.None;
        foreach (Tri t in g.oTris)
        {
            Vector3 vecCross = Vector3.Cross(t.vecB - t.vecA, t.vecC - t.vecA);
            float fArea = 0.5f * vecCross.Length();
            if (fArea < 1e-6f)
                continue;

            // Winding isn't guaranteed per group, so pick whichever side of
            // the triangle is inside the part.
            Vector3 vecN = Vector3.Normalize(vecCross);
            Vector3 vecCentroid = (t.vecA + t.vecB + t.vecC) / 3f;
            Vector3 vecProbe = vecCentroid - 0.5f * fSkinMM * vecN;
            if (!voxVolume.bIsInside(vecProbe))
                vecProbe = vecCentroid + 0.5f * fSkinMM * vecN;

            bool bCovered = voxSkin.bIsInside(vecProbe);
            if (bPort)
            {
                fPortArea += fArea;
                if (bCovered) fPortCovered += fArea;
            }
            else
            {
                fBodyArea += fArea;
                if (bCovered) fBodyCovered += fArea;
            }
        }
    }

    float fBodyPct = 100f * fBodyCovered / fBodyArea;
    float fPortPct = fPortArea > 0 ? 100f * fPortCovered / fPortArea : 0f;
    Console.WriteLine($"[Checkpoint 8] skin t={fSkinMM}mm covers {fBodyPct:F1}% of body surface (area-weighted), {fPortPct:F1}% of port surface");
    if (fBodyPct < 95f)
        throw new Exception($"skin covers only {fBodyPct:F1}% of the non-port surface");
    if (fPortPct > 10f)
        throw new Exception($"skin still covers {fPortPct:F1}% of the port surface - port cut-outs aren't clearing it");
}

/// <summary>
/// Picks random gyroid mid-surface points in the clear interior (away from
/// the skin and port seals, where the sheet merges into solid) and measures
/// the wall along the surface normal two ways: against the analytic field,
/// which checks the normalization math, and against the voxelized core
/// (trilinear SDF), which is what actually gets meshed and printed.
/// </summary>
static void RunWallThicknessCheck(Library oLib, GyroidField oField, Voxels voxCore, Voxels voxVolume, List<Voxels> avoxExclude,
                                  BBox3 oBBox, float fSkinMM, float fVoxelSizeMM, float fWallMM)
{
    const int nSamples = 200;
    float fNominal = fWallMM;
    float fReach = fNominal; // bisection bracket either side of the mid-surface
    float fClearance = fSkinMM + fNominal + 2f * fVoxelSizeMM;

    // Point-in-solid lookups are cheap; PicoGK's closest-point query scales
    // with grid size and made this check take ~15 minutes at 0.15mm, so the
    // clearance test is one erosion up front instead.
    using Voxels voxClear = voxVolume.voxOffset(-fClearance);

    // The sheet merges into solid inside a port seal, so keep samples out of
    // reach of one - dilated by the probe length plus two voxels, since
    // bIsInside itself is only good to the nearest voxel.
    List<Voxels> avoxKeepOut = avoxExclude.Select(v => v.voxOffset(fReach + 2f * fVoxelSizeMM)).ToList();

    // Voxels.bIsInside (and ScalarField's own lookups) snap to the nearest
    // voxel - +/-0.1mm per side at 0.15mm, as big as the errors we're looking
    // for - so the printed wall is measured on a trilinear SDF sample instead.
    using var oCoreSdf = new TrilinearSdf(oLib, voxCore);

    var oRand = new Random(1234);
    var afField = new List<float>();
    var afVoxel = new List<float>();
    var avecSample = new List<Vector3>();
    int nAttempts = 0;

    while (afField.Count < nSamples && nAttempts++ < 100 * nSamples)
    {
        Vector3 vecStart = oBBox.vecMin + oBBox.vecSize() * new Vector3(oRand.NextSingle(), oRand.NextSingle(), oRand.NextSingle());
        if (!oField.bProjectToSurface(vecStart, out Vector3 vecP))
            continue;

        if (!voxClear.bIsInside(vecP))
            continue;

        if (avoxKeepOut.Any(v => v.bIsInside(vecP)))
            continue;

        Vector3 vecN = oField.vecNormal(vecP);

        float fFieldT =
            fBisect(t =>  oField.fDistance(vecP + t * vecN) <= 0.5f * fNominal, fReach) +
            fBisect(t => -oField.fDistance(vecP - t * vecN) <= 0.5f * fNominal, fReach);
        float fVoxelT =
            fBisect(t => oCoreSdf.fValue(vecP + t * vecN) <= 0f, fReach) +
            fBisect(t => oCoreSdf.fValue(vecP - t * vecN) <= 0f, fReach);

        afField.Add(fFieldT);
        afVoxel.Add(fVoxelT);
        avecSample.Add(vecP);
    }

    foreach (Voxels vox in avoxKeepOut)
        vox.Dispose();

    if (afField.Count < nSamples)
        throw new Exception($"wall thickness check: only found {afField.Count}/{nSamples} interior sample points");

    Console.WriteLine($"[Checkpoint 9] wall thickness along normal, nominal {fNominal}mm, {nSamples} interior points:");
    Console.WriteLine($"  field : {strStats(afField)}");
    Console.WriteLine($"  voxels: {strStats(afVoxel)}");

    float fFieldWorst = afField.Max(t => MathF.Abs(t - fNominal));
    if (fFieldWorst > 0.02f * fNominal)
        throw new Exception($"field wall thickness off nominal by up to {fFieldWorst:F3}mm - normalization is wrong");

    // Tolerances are about the print, not the grid: measured this way the
    // wall holds 0.802 +/- 0.009mm even at the 0.5mm dev voxel size.
    int iWorst = Enumerable.Range(0, afVoxel.Count).MaxBy(i => MathF.Abs(afVoxel[i] - fNominal));
    float fVoxelWorst = MathF.Abs(afVoxel[iWorst] - fNominal);
    float fVoxelMeanErr = MathF.Abs(afVoxel.Average() - fNominal);
    Vector3 vecWorst = avecSample[iWorst];
    Console.WriteLine($"  worst voxel sample: {afVoxel[iWorst]:F3}mm at ({vecWorst.X:F2}, {vecWorst.Y:F2}, {vecWorst.Z:F2})");
    if (fVoxelMeanErr > 0.02f * fNominal || fVoxelWorst > 0.05f * fNominal)
        throw new Exception($"voxel wall thickness off nominal (mean err {fVoxelMeanErr:F3}mm, worst {fVoxelWorst:F3}mm)");
}

/// <summary>
/// Distance from the mesh to the core's voxel surface, sampled at triangle
/// centroids and edge midpoints (where flat triangles cut across a curved
/// surface) of a random subset of triangles.
/// </summary>
static float fRunSurfaceDeviation(string strLabel, IndexedMesh oMesh, TrilinearSdf oSdf)
{
    const int nTriSamples = 200_000;
    var oRand = new Random(4321);
    var afDev = new List<float>(4 * nTriSamples);
    Span<Vector3> avecPts = stackalloc Vector3[4];
    for (int i = 0; i < nTriSamples; i++)
    {
        int t = oRand.Next(oMesh.nTriangles);
        Vector3 a = oMesh.vecVertex(oMesh.anTris[3 * t]);
        Vector3 b = oMesh.vecVertex(oMesh.anTris[3 * t + 1]);
        Vector3 c = oMesh.vecVertex(oMesh.anTris[3 * t + 2]);
        avecPts[0] = (a + b + c) / 3f;
        avecPts[1] = 0.5f * (a + b);
        avecPts[2] = 0.5f * (b + c);
        avecPts[3] = 0.5f * (c + a);
        foreach (Vector3 vec in avecPts)
            afDev.Add(MathF.Abs(oSdf.fValue(vec)));
    }

    afDev.Sort();
    float fPct(double d) => afDev[(int)Math.Min(afDev.Count - 1, d * afDev.Count)];
    Console.WriteLine($"  surface deviation ({strLabel}, {afDev.Count} pts): mean {afDev.Average():F4}  " +
                      $"p99 {fPct(0.99):F4}  p99.9 {fPct(0.999):F4}  max {afDev[^1]:F4} mm");
    return afDev[^1];
}

/// <summary>Largest t in [0, fMax] with bInside(t) still true, assuming bInside(0) and a single crossing.</summary>
static float fBisect(Func<float, bool> bInside, float fMax)
{
    float fLo = 0, fHi = fMax;
    for (int i = 0; i < 30; i++)
    {
        float fMid = 0.5f * (fLo + fHi);
        if (bInside(fMid)) fLo = fMid; else fHi = fMid;
    }
    return fLo;
}

static string strStats(List<float> af)
{
    float fMean = af.Average();
    float fStd = MathF.Sqrt(af.Average(t => (t - fMean) * (t - fMean)));
    return $"mean {fMean:F3}  std {fStd:F3}  min {af.Min():F3}  max {af.Max():F3}";
}

/// <summary>
/// Stand-in for Voxels.voxIntersectImplicit, which throws a native
/// SEHException in PicoGK 2.3.0 at any voxel size below ~0.33mm (even for a
/// plain sphere implicit, on any grid size). Rendering the implicit over the
/// mask's bounding box and boolean-intersecting gives the same result and
/// works at every size tried, at the cost of evaluating the implicit over the
/// whole box instead of only inside the mask.
/// </summary>
static Voxels voxIntersectImplicit(Library oLib, Voxels voxMask, IImplicit xImplicit)
{
    Voxels voxRendered = new(oLib, xImplicit, voxMask.oCalculateBoundingBox());
    voxRendered.BoolIntersect(voxMask);
    return voxRendered;
}

static string strSanitize(string strName)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        strName = strName.Replace(c, '_');
    return strName;
}

/// <summary>
/// Samples the gyroid field on an nGridSteps x nGridSteps grid across one
/// face of oBBox (inset fInsetMM from the boundary so samples land inside the
/// solid rather than exactly on it) and returns the fraction of samples that
/// fall on stream A's side, stream B's side, and the membrane band.
/// </summary>
static (float fFracA, float fFracB, float fFracMembrane) oSurveyGyroidAtFace(
    EFace eFace, BBox3 oBBox, GyroidField oField, float fHalfThicknessMM,
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

            float d = oField.fDistance(vec);
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

sealed class Options
{
    public float fVoxelSizeMM = Params.fVoxelSizeMM;
    public List<string> astrOnly = new();
    public string strExport = "stl";
    public float fDecimateMM = 0.02f;
    public float fCellSizeMM = Params.fCellSizeMM;
    public float fWallThicknessMM = Params.fWallThicknessMM;
    // Null until resolved in oParse: the seal depth is defined relative to
    // lambda (~0.75*lambda), so it has to track --cell unless given outright.
    public float? fSealDepthMM = null;
    public float fSealDepth => fSealDepthMM ?? 0.75f * fCellSizeMM;

    public static Options oParse(string[] astrArgs)
    {
        var o = new Options();
        for (int i = 0; i < astrArgs.Length; i++)
        {
            string strArg = astrArgs[i];
            string strNext() => ++i < astrArgs.Length ? astrArgs[i] : throw new ArgumentException($"{strArg} needs a value");
            switch (strArg)
            {
                case "--voxel":  o.fVoxelSizeMM = float.Parse(strNext(), CultureInfo.InvariantCulture); break;
                case "--only":   o.astrOnly.Add(strNext()); break;
                case "--export": o.strExport = strNext(); break;
                case "--decimate": o.fDecimateMM = float.Parse(strNext(), CultureInfo.InvariantCulture); break;
                case "--cell":   o.fCellSizeMM = float.Parse(strNext(), CultureInfo.InvariantCulture); break;
                case "--wall":   o.fWallThicknessMM = float.Parse(strNext(), CultureInfo.InvariantCulture); break;
                case "--seal":   o.fSealDepthMM = float.Parse(strNext(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown argument {strArg}");
            }
        }

        if (o.strExport is not ("stl" or "3mf" or "vdb" or "none"))
            throw new ArgumentException($"--export must be stl, 3mf, vdb or none (got {o.strExport})");
        if (o.fCellSizeMM <= 0)
            throw new ArgumentException($"--cell must be positive (got {o.fCellSizeMM})");
        // The seal depth is ~0.75*lambda by design, so a cell bigger than the
        // slab is a silent mis-seal rather than an error. Catch it here.
        if (o.fWallThicknessMM <= 0 || o.fWallThicknessMM >= 0.5f * o.fCellSizeMM)
            throw new ArgumentException($"--wall must be >0 and well under half --cell (got {o.fWallThicknessMM} vs cell {o.fCellSizeMM})");
        return o;
    }
}

/// <summary>
/// Trilinearly interpolated signed distance of a voxel field, sampled from
/// voxel-centre values. Sub-voxel accurate (&lt;0.001mm radius error on a
/// test sphere at 0.15mm, vs +/-0.1mm from Voxels.bIsInside).
/// </summary>
sealed class TrilinearSdf : IDisposable
{
    readonly Library oLib;
    readonly ScalarField oField;
    readonly Vector3 vecOrigin;
    readonly Vector3 vecStep;

    public TrilinearSdf(Library oLib, Voxels vox)
    {
        this.oLib = oLib;
        oField = new ScalarField(vox);
        vecOrigin = oLib.vecVoxelsToMm(0, 0, 0);
        vecStep = oLib.vecVoxelsToMm(1, 1, 1) - vecOrigin;
    }

    public void Dispose() => oField.Dispose();

    public float fValue(Vector3 vec)
    {
        Vector3 vecIndex = (vec - vecOrigin) / vecStep;
        var vecBase = new Vector3(MathF.Floor(vecIndex.X), MathF.Floor(vecIndex.Y), MathF.Floor(vecIndex.Z));
        Vector3 vecT = vecIndex - vecBase;
        int nX = (int)vecBase.X, nY = (int)vecBase.Y, nZ = (int)vecBase.Z;

        float fSum = 0;
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        for (int dz = 0; dz < 2; dz++)
        {
            oField.bGetValue(oLib.vecVoxelsToMm(nX + dx, nY + dy, nZ + dz), out float fCorner);
            fSum += fCorner
                  * (dx == 1 ? vecT.X : 1f - vecT.X)
                  * (dy == 1 ? vecT.Y : 1f - vecT.Y)
                  * (dz == 1 ? vecT.Z : 1f - vecT.Z);
        }
        return fSum;
    }
}

/// <summary>Wall-clock per stage plus the process's peak working set (PicoGK's native grids count toward it).</summary>
sealed class StageTimer
{
    readonly Stopwatch oTotal = Stopwatch.StartNew();
    readonly List<(string strStage, double dSeconds, long nPeakBytes)> aoStages = new();
    double dLast;

    public void Mark(string strStage)
    {
        double dNow = oTotal.Elapsed.TotalSeconds;
        using Process oProc = Process.GetCurrentProcess();
        aoStages.Add((strStage, dNow - dLast, oProc.PeakWorkingSet64));
        dLast = dNow;
    }

    public void Report()
    {
        Console.WriteLine("[Timing] stage                          seconds   peak RAM");
        foreach ((string strStage, double dSeconds, long nPeakBytes) in aoStages)
            Console.WriteLine($"  {strStage,-32} {dSeconds,8:F1}   {nPeakBytes / 1e9,6:F2} GB");
        Console.WriteLine($"  {"total",-32} {oTotal.Elapsed.TotalSeconds,8:F1}");
    }
}
