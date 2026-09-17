# TPMS-HRV — remaining work

Status: Sessions 1–2 done. The pipeline (importer, port seals, normalized
gyroid, skin, wall-thickness check; Checkpoints 0–9) runs end-to-end at 0.5mm
on both cube fixtures and the real HRV volume. At 0.15mm, only the 100mm cube
fits in memory. This file tracks what's left; completed work and
its rationale live in the commit messages. Windows is the only supported build
platform for now; the spec's headless/Linux path is out of scope.

## Session 2 — Real gyroid

- [x] Gradient normalization (`Gyroid.cs`): d/|∇d| plus one Newton step.
      Wall is 0.799 ± 0.000mm along the normal for nominal 0.8 (the raw field
      gave 0.60–0.73; first-order alone gave a uniform but 3.5%-thin 0.772).
- [x] Wall-thickness check (Checkpoint 9): 200 random interior points,
      measured on the analytic field and on the voxel core. Voxel core:
      0.802 ± 0.009mm at 0.5mm voxels, 0.800 ± 0.001mm at 0.15mm (cube and real
      volume). Enforced: mean within 2% of nominal, every sample within 5%.
- [x] Exterior skin (Checkpoint 8): erode-and-subtract, with a port cut-out
      that is pulled in 1.5mm from each port edge, so neighbouring faces keep
      their skin up to the corner.
- [x] Measured 0.15mm (see below). Pipeline now takes
      `--voxel`, `--only`, `--export stl|vdb|none` (defaults unchanged: 0.5mm,
      all parts, STL), and prints time and peak RAM for each stage.

### 0.15mm results (Ryzen 7 5700U, 15GB RAM)

| part | stage | time | peak RAM |
|---|---|---|---|
| 100mm cube | voxel stages + checks | 4.1 min | 3.95GB |
| 100mm cube | meshing | 10.3 min | 8.96GB |
| 100mm cube | write STL | 1.1 min | — |
| real volume | voxel stages + checks | ~20 min | ~11GB working set, 18GB+ committed |
| real volume @ 0.25mm | voxel stages + checks | 2.5 min | 2.48GB |
| real volume @ 0.25mm | meshing | 7.9 min | 8.05GB |
| real volume @ 0.25mm | write STL | 1.2 min | — |

- Real volume at 0.25mm **does fit**: 94M triangles, **4.7GB STL** (about
  the same size as the cube at 0.15mm). Wall 0.801 ± 0.002mm. So 0.25mm is a
  working whole-part fallback, but the STL is still big enough that most
  slicers will struggle — a smaller voxel size alone doesn't fix output size.

- Cube: 95M triangles, **4.7GB STL** (437MB at 0.5mm). As a VDB grid it's
  about 15× smaller than the STL (29MB vs 437MB at 0.5mm).
- Real volume: **doesn't fit in 15GB.** The wall check passed (0.800 ±
  0.001), but it then paged heavily and was stopped after 25+ minutes stuck in
  export. Meshing it would need roughly 2.6× the cube's 9GB, and the STL would
  be ~12GB. Whole-part production output isn't viable on this machine, so
  Session 3's tiling is what makes 0.15mm possible, not an optimization.

## Session 3 — Modularization

- [ ] 100mm module tiling with seam lip and gasket groove so a 200mm core
      becomes eight prints.
- [ ] Tile size: a 100mm cube at 0.15mm peaks at 9GB with STL export, so
      100mm is the upper limit on this machine. Render each tile's implicit
      separately. `voxIntersectImplicit` currently renders the gyroid over the
      whole part's bbox at once.
- [ ] Output format per tile: STL is ~4.7GB per 100mm tile. Options: keep
      `.vdb` as the archived output and mesh on demand, write 3MF (indexed +
      zipped), or decimate (voxel meshes of smooth TPMS are heavily
      over-tessellated). Check what the target slicer accepts before choosing.

## Session 4 — Parameter sweep

- [ ] Drive λ (`fCellSizeMM`), wall thickness, and seal depth from a config file
      instead of the `Params.cs` constants.
- [ ] Generate the candidate set; export for slicing and mass/time estimation.

## Open questions

- [ ] **Port placement rule.** `PortAssertion.cs`'s per-face role table was
      dropped: it assumed one port per cardinal face, but the real enclosure
      puts SupplyIn and ExhaustIn on the same `+Y` face. Checkpoint 7b's
      open-area survey validates stream/port wiring for now. Revisit a real
      placement rule once there are more real parts to generalize from.
- [ ] **Checkpoint 4's >5% bbox-volume warning is a known false positive for
      non-cuboid parts** — the real enclosure is a tapering wedge, correctly
      ~40% under its bbox volume by shape, not a winding bug. Left non-blocking;
      worth tightening the heuristic if it gets noisy on more real parts.
- [ ] **Checkpoint 7b samples the whole bbox face, not the port patch.** On
      the real volume, SupplyIn and ExhaustIn both sit on `+Y`, so they get
      the same survey. Sampling each port group's own triangles (inset
      inward) would test each port's actual opening.
- [ ] **Report the PicoGK bug upstream:** `Voxels.IntersectImplicit` throws a
      native `SEHException` at any voxel size ≤ ~0.33mm (2.3.0, the latest
      release), even for a plain sphere. The pipeline works around it
      (`voxIntersectImplicit` in `Program.cs`); drop the workaround once
      it's fixed.

## Known traps to keep watching (spec section 6)

- **PicoGK point queries snap to the nearest voxel.** `Voxels.bIsInside` and
  `ScalarField.bGetValue`/`fSignedDistance` err by up to ±0.67 voxel, and
  `bRayCastToSurface` is biased about −0.11mm at 0.15mm. Any measurement uses
  `TrilinearSdf` instead. `vecClosestPointOnSurface` is slower on bigger grids
  (it made Checkpoint 9 take 15 min at 0.15mm), so keep it out of loops.
- **Don't voxelize meshes with internal walls if anything reads the distance
  field.** Per-triangle prisms gave the right inside/outside but bad distances,
  so `Offset(-t)` eroded the real part's skin cut-outs from the inside (23%
  of the port surface still skinned at 0.25mm, fine at 0.5/0.15 by luck).
  `SlabBuilder` now extrudes each group as one closed prism.
- **Free PicoGK grids explicitly.** They live in native memory the GC can't
  see.

- **Port colors must match the palette exactly.** An off-palette port (e.g.
  pure blue `(0,0,1)` for ExhaustIn) falls through to `None` and is silently
  skipped — no slab, no stream, no placement info. Hit once already on the
  first real export.
- ASCII STL loading isn't implemented in PicoGK (binary loads only) — not hit
  yet, since the pipeline only *writes* STL.
- Onshape API returns meters, PicoGK is mm — only matters if a REST API import
  path ever replaces the OBJ export path.
- Don't let code depend on exact Onshape export filenames (rev/part-number
  prefixes). Handled in two places so far: the fixture path is an explicit
  argument, and `ObjImporter.cs` falls back to `<objname>.mtl` when the OBJ's
  own `mtllib` line points at a stale export name. Keep it in mind if a
  directory-scan importer is added.
