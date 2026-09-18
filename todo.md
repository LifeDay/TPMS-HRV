# TPMS-HRV — remaining work

Status: Sessions 1–2 done; Session 3 output format done. The pipeline
(importer, port seals, normalized gyroid, skin, wall-thickness check,
decimated STL/3MF export; Checkpoints 0–10) runs end-to-end at 0.5mm on both
cube fixtures and the real HRV volume, and on the real volume at 0.25mm. At
0.15mm, only the 100mm cube fits in memory.

Target: Bambu Studio, printed in one piece on a Bambu H2C (the real
volume is 232 × 131 × 140mm). Known-good recipe: `--cell 16 --decimate 0.05
--voxel 0.25 --export 3mf`, 1.0M triangles, ~10 min to slice in Bambu Studio.
OrcaSlicer handles at least 1.9M, so that recipe is a floor, not the target —
see Session 3. This file tracks what's left; completed work and its rationale
live in the commit messages. Windows is the only supported build
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

- Real volume at 0.25mm **does fit**: 94M triangles, **4.7GB STL** raw
  (about the same size as the cube at 0.15mm). Wall 0.801 ± 0.002mm. A coarser
  voxel size alone doesn't fix output size; decimation does (Session 3).

- Cube: 95M triangles, **4.7GB STL** (437MB at 0.5mm). As a VDB grid it's
  about 15× smaller than the STL (29MB vs 437MB at 0.5mm).
- Real volume: **doesn't fit in 15GB.** The wall check passed (0.800 ±
  0.001), but it then paged heavily and was stopped after 25+ minutes stuck in
  export. Meshing it would need roughly 2.6× the cube's 9GB, and the STL would
  be ~12GB. Whole-part production output isn't viable on this machine, so
  Session 3's tiling is what makes 0.15mm possible, not an optimization.

## Session 3 — Printable output at production resolution

The core prints in one piece, so seam lips and gasket grooves are out.
Tiling survives only as a way to fit 0.15mm in RAM.

- [x] Output format: decimate, then write 3MF (`--export 3mf`,
      `--decimate <mm>`, default 0.02). `MeshDecimator.cs`: quadric
      edge-collapse to an area-weighted RMS tolerance, run in parallel on 30mm
      chunks with chunk-shared vertices locked, then a second pass on a
      half-shifted grid to clean up the seams. Checkpoint 10 fails on open,
      non-manifold, or degenerate edges and on inward winding, and reports
      surface deviation against the core's trilinear SDF.

      | real volume | triangles | STL | 3MF | deviation mean / p99 / max |
      |---|---|---|---|---|
      | 0.25mm raw | 94.0M | 4.7GB | — | 0.003 / 0.016 / 0.093mm |
      | 0.25mm, 0.02 tol | 11.1M | 556MB | **137MB** | 0.014 / 0.041 / 0.127mm |
      | 0.5mm, 0.02 tol | 11.5M | — | 131MB | 0.018 / 0.061 / 0.214mm |

      Decimation takes ~70s at 0.25mm and changes volume by <0.03%. The
      decimated size barely depends on voxel size (same ~11M triangles at 0.5
      and 0.25), so 0.15mm output should be about the same size again;
      only meshing RAM is the limit.
- [x] **Sliced in Bambu Studio — it can't take 11M triangles.** The 0.25mm /
      0.02mm-tolerance file stalls indefinitely; so does the 100mm cube at
      4.2M. Slicer settings were not the cause (supports off, 0% infill).
      Measured ceiling on the cube:

      | cube file | triangles | slice |
      |---|---|---|
      | λ=12, tol 0.05 | 0.70M | ~3 min |
      | λ=16, tol 0.02 | 0.99M | ~4 min |
      | λ=12, tol 0.02 | 1.90M | wouldn't run |
      | λ=8, tol 0.02 | 4.20M | very slow |
      | λ=8, raw | 8.75M | hung |

      **Budget: ~1M triangles in Bambu Studio** (later found to be
      Bambu-specific — see below). Slice time is near-linear below it and falls
      off a cliff above. Triangle count is what matters — the raw 8.7M mesh
      is not pathological, just too big.

- [x] **λ is the cheap lever: triangle count goes as 1/λ².** Measured on the
      cube at tol 0.02: λ 8→12 gave 2.22× fewer (predicted 2.25×), 8→16 gave
      4.26× (predicted 4×). Costs no geometric accuracy at all — the wall stays
      at nominal, unlike loosening `--decimate`. It costs heat-exchange area,
      which goes as 1/λ.

- [x] **Real part now fits the budget**: `--cell 16 --decimate 0.05 --voxel
      0.25` → 1,008,412 triangles, 14MB 3MF, topology clean, wall 0.800 ±
      0.001mm, ports 42–45% open. Written to
      `out/real_hrv_volume/real_cell16_tol0.05.3mf`. **Not yet sliced.**

- [x] **Decimator bug, found and fixed.** It produced 1 non-manifold edge in
      1.5M on the real part while PicoGK's raw mesh was clean (0 of 76.8M).
      Cause: a collapse into a *locked* survivor was allowed, but
      `bLinkConditionHolds` only sees the current chunk's triangles, and a
      locked vertex has triangles in other chunks by definition — so the check
      passed on incomplete information and created a duplicate edge. Now both
      endpoints must be chunk-local; the half-shifted second pass still
      simplifies the seams. Cost: ~1,000 triangles in 4.2M.

- [x] **Budget confirmed on the real part.** `real_cell16_tol0.05.3mf`
      (1,008,412 triangles) sliced in Bambu Studio in ~10 min — slow but
      acceptable. The ~1M budget measured on the cube transfers to the real
      part.

- [x] **The ~1M ceiling is Bambu-specific.** OrcaSlicer shows the same
      high-triangle-count warning at ~1M but slices straight through it:
      `sweep_cell12_tol0.02.3mf` (1.90M) in ~5 min, where Bambu Studio needs
      ~10 min for 1.0M and wouldn't run 1.90M at all. The warning is cosmetic;
      Bambu's cliff is not. **Treat the ~1M budget as a Bambu Studio number
      only.** On Orca the budget is at least 1.9M and the real limit is
      unmeasured, so tol 0.02 (0.06mm deviation instead of 0.35, no wall
      thinning) and a smaller λ are both back on the table.

- [ ] **Find Orca's actual ceiling** — `sweep_cell8_tol0.02.3mf` (4.20M),
      `sweep_cell8_RAW.3mf` (8.75M). Bambu called 4.20M "very slow" and hung on
      8.75M. **Low priority now:** the thermal model below rules λ=8 out on its
      own (it chokes the 140mm fans), so the only λ we need to slice is 12,
      which Orca already does in 5 min. Worth knowing only if the envelope
      grows.

- [x] **λ=16 is thermally defensible but λ=12 is the right point.** Modelled
      counterflow ε–NTU on the measured geometry (envelope 2.559 L from
      `volume.stl`'s signed volume; gyroid midsurface area 3.091/λ per unit
      volume, validated against the λ=16 3MF — predicted 1.09 m² total wetted
      vs 1.094 m² measured). **The earlier 75% → 60% guess was much too
      optimistic.**

      | λ | exchange area | porosity | Dₕ | ε at 100 m³/h/stream |
      |---|---|---|---|---|
      | 8 | 0.99 m² | 0.61 | 3.0mm | 47–54% |
      | 12 | 0.66 m² | 0.72 | 5.0mm | 27–39% |
      | 16 | 0.49 m² | 0.77 | 7.0mm | 17–29% |

      With **2× 140mm case fans** (one per stream, ~140 m³/h free air, ~20 Pa
      static) the *fan*, not λ, sets the operating point, and λ trades flow
      against ε. Maximising recovered heat (ε × flow, the figure that matters)
      at ΔT=20K:

      | λ | Q | ε | recovered |
      |---|---|---|---|
      | 8 | 16 m³/h | 78% | 0.082 kW |
      | 12 | 42 m³/h | 47% | **0.131 kW** |
      | 16 | 65 m³/h | 28% | 0.124 kW |

      λ=8 chokes itself — great ε, no ventilation. λ=16 costs only ~5% of the
      recovered heat vs the optimum, so it is *acceptable*; λ=12 is better and
      OrcaSlicer already slices it (1.90M, 5 min). **Recipe: `--cell 12
      --decimate 0.02`, sliced in Orca.**

      Model is `analysis/thermal.py` (no dependencies; `--measure` re-derives
      the geometry constants from `out/`, which needs numpy).
      Assumptions, in order of how much they move the answer: Nu between
      8 (developing laminar, no enhancement) and 0.1·Re^0.7·Pr^(1/3) (TPMS fit
      with secondary flows) — this is the widest band, roughly ±1.7× on ε at
      λ=16; Darcy f·Re = 64·1.5 for tortuosity; k_wall = 0.20 W/mK (PETG); core
      pressure drop only, no ducting or filter. The ε figures are estimates
      from correlations, not measurements — firm them up with a rig or CFD
      before committing to a final λ.

- [ ] **The core is area-starved for whole-house duty — decide the ventilation
      target.** 80% effectiveness needs 2.2–6.6 m² depending on flow, i.e.
      2–7× even the λ=8 core; 2.5 L is simply a small exchanger (a 3mm plate
      core in the same envelope would give ~1.4 m², so this is a volume limit,
      not a gyroid limit). At 42 m³/h and 47% the current design is a
      single-room unit, not a house HRV. Either accept that, or grow the
      envelope — which conflicts with printing in one piece on the H2C.

- [ ] **Fan choice is worth more than λ.** Swapping the 140mm case fans for
      high-static 140s (~100 Pa) roughly doubles recovered heat (0.13 → 0.29 kW)
      and moves the optimum to λ≈10. Measure the real core Δp before buying
      fans — the model's friction factor is the second-biggest uncertainty.

- [ ] 0.15mm on the real volume: meshing needs ~23GB at once. Options: mesh
      the core in overlapping slabs and decimate each before the next (the
      decimator already locks seam vertices, so pieces can be stitched), or
      run on a machine with ≥32GB. Render the implicit per slab too —
      `voxIntersectImplicit` currently renders the gyroid over the whole
      part's bbox at once. **Lower priority now** — at λ=16 the 0.25mm output
      is already at the slicer's limit, so finer voxels buy nothing
      downstream.

## Session 4 — Parameter sweep

- [x] λ, wall thickness and seal depth are now CLI flags (`--cell`, `--wall`,
      `--seal`) rather than `Params.cs` constants. Pulled forward from this
      session because the slicer limit forced a λ sweep. `--seal` defaults to
      0.75 × `--cell` instead of a hardcoded 6.0mm — that constant was
      documented as ~0.75λ and would have silently under-sealed the ports at
      any other λ. At λ=8 it still evaluates to exactly 6.0, so old output is
      unchanged. A config file is still worth doing if the flag list grows.
- [ ] Generate the candidate set; export for slicing and mass/time estimation.

## Open questions

- [ ] **Checkpoint 9 verifies the wrong object.** It measures wall thickness on
      the *voxel core*, which is then discarded; what prints is the decimated
      mesh. At `--decimate 0.05` the mesh deviates up to 0.27–0.35mm from the
      SDF (the max is a noisy tail statistic, varying run to run at fixed
      mean/p99.9), which is 68–88% of the 0.8mm wall's 0.4mm half-thickness.
      Nothing perforated, but the ±5% wall guarantee Session 2 built does not
      survive export. Checkpoint 10 now *warns* when max deviation exceeds half
      the half-thickness; the real fix is to re-run the normal-ray measurement
      against the decimated mesh, which needs a mesh ray cast rather than the
      SDF sampling used today.
- [ ] **Deviation does not improve with voxel size at loose tolerances.** Going
      0.5mm → 0.25mm voxels cut the *raw* mesh's max deviation from 0.196 to
      0.106mm, but the decimated figure at tol 0.05 stayed put (0.316 → 0.352).
      Above ~0.05mm the decimation error dominates and finer voxels buy
      nothing. Don't reach for a smaller voxel to fix a tolerance problem.

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
- **Voxel-count volume ≠ mesh volume on thin walls.** `CalculateProperties`
  differs from the meshed volume by ±1% on the core (+1.1% at 0.5mm, −1.1%
  at 0.25mm). Compare mesh volumes to each other, not to the voxel count.
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
