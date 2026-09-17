# TPMS-HRV — remaining work

Status: Session 1 (importer + stub gyroid) implemented, verified against both
fixtures, committed as `eae0856`. This file tracks what's left.

## Session 1 loose ends

- [x] **Visually confirm the debug STLs** — no GUI slicer available in this
      headless session, so instead wrote a numpy-stl + matplotlib renderer
      (scratchpad, not committed) and eyeballed every debug STL for both
      fixtures:
      - `checkpoint0_sphere.stl` — clean r=25mm sphere, no artifacts.
      - `group_*.stl` — rendered all 6 axis-aligned cube faces: +X=red
        (SupplyIn), -X=orange (SupplyOut), +Z=blue (ExhaustIn), -Z=cyan
        (ExhaustOut), ±Y=unpainted body. Matches fixture spec on both the
        flat and filleted cube.
      - `slab_*.stl` — each sits exactly on its assigned port face, correct
        colors, both fixtures.
      - `membrane.stl` — full-mesh view was too sparse to read (decimated
        for render, 8.8M tris), so instead computed a true planar
        triangle/plane intersection at z=0 (not just nearest-triangle
        sampling) to get the actual cross-section contour: clean periodic
        gyroid wave pattern, double-wall outline visible with the expected
        (known, session-2-fix) uneven thickness from the un-normalized
        stub field. No holes, no degenerate geometry, same on both fixtures.
      Nothing looked wrong; no code changes needed from this pass.
- [x] Decide whether to delete `test_cube_100mm.zip` / `test_cube_filleted_100mm.zip`
      at repo root — deleted; fully redundant with `fixtures/*.obj`/`*.mtl`, which
      are already committed and are what the code actually reads.
- [x] Task 7's stream/port pairing (Supply*↔stream A, Exhaust*↔stream B) —
      investigated. The A/B↔Supply/Exhaust assignment itself is provably a free
      choice: the raw field obeys `d(-p) = -d(p)` (point inversion), which swaps
      stream A↔B while also swapping PlusX↔MinusX and PlusZ↔MinusZ, so flipping
      the assignment just mirrors the core — not a decision that needs "real"
      fluid-routing info. What *does* need real info, and isn't decidable yet, is
      whether a given port actually has open area for its assigned stream — that
      depends on the gyroid's phase relative to where the port sits, which is
      untestable on these fixtures (each "port" is an entire cube face, so every
      face always has a healthy mix of both streams). Added a standing check for
      this instead of a one-off decision: Checkpoint 7b in `Program.cs` grid-samples
      the raw field across each port face and asserts the assigned stream has
      ≥5% open area there. Passes trivially on both fixtures now; it's meant to
      be the thing that catches a misphased gyroid once real small-bore ports
      exist. **This is now the same blocker as the next item below** — real
      resolution needs the real ERV volume, not more thinking about the stub.
- [x] Run against the real HRV design volume (`real-HRV-volume/real-HRV-volume.obj`,
      added by user this session — note the corrected name; earlier "ERV" was a
      misnomer). Full pipeline now completes end-to-end (Checkpoints 0-7b all
      pass); several real findings and fixes came out of it:
      - **`ObjImporter.cs` mtllib fallback (fixed)**: the OBJ's internal `mtllib`
        line pointed at Onshape's original export filename, which doesn't survive
        a human renaming the `.obj`/`.mtl` pair afterward. Now falls back to
        `<objname>.mtl` when the referenced file is missing - the same
        exact-export-filename trap called out below, just showing up inside the
        file instead of as the path argument.
      - **Port palette mismatch (user fixed, not code)**: first export had
        ExhaustIn painted pure blue `(0,0,1)` instead of the palette's
        `(0.231,0.380,0.706)`, so it fell through to `None` and got silently
        skipped (no slab, no stream, no placement info). Caught by rendering the
        groups and noticing it geometrically mirrored SupplyIn on the same face
        - strong evidence before the user even confirmed it. User re-painted and
        re-exported; second run had all 4 ports on-palette.
      - **`PortAssertion.cs` per-face role table removed**: it hardcoded one
        port per cardinal face (`+X=SupplyIn` etc.), reverse-engineered from the
        synthetic cube fixtures. The real enclosure puts SupplyIn *and* ExhaustIn
        on the same `+Y` face (mirrored pair) - a layout that model can't
        express. Per user decision, dropped the assertion; Checkpoint 6 now just
        classifies each port to a face (still needed for Checkpoint 7b's
        per-face survey), and Checkpoint 7b's physics-based open-area check is
        the thing that actually validates stream/port wiring now. Revisit a real
        placement rule once more real parts exist to generalize from.
      - **Checkpoint 4's bbox-volume-% warning is a known false positive for
        non-cuboid parts**: real enclosure is a tapering wedge/funnel, correctly
        ~40% under its bounding-box volume by shape, not a winding bug -
        confirmed by rendering `volume.stl` (clean closed wedge, matches the
        painted groups). Left as a warning (non-blocking); worth tightening the
        heuristic later if it gets noisy on more real parts.
      - **Tessellation**: first export was coarse (972 tris) as the spec
        predicted; user re-exported finer (2348 tris) before this became a real
        problem. Rendered the body shell and a true membrane cross-section
        afterward - clean, no facet-stepping artifacts.
      - Real volume is ~2x a fixture's bbox (232 x 131 x 140mm) - membrane.stl
        came out to 1.2GB at the 0.5mm dev voxel size. Relevant to the Session 2
        resolution/export-format item below.
- [ ] Headless/Linux build path (mentioned in spec's environment prerequisites)
      is untested — this session was done on Windows.

## Session 2 — Real gyroid

- [ ] Gradient normalization so wall thickness is uniform (current `GyroidStub.cs`
      is explicitly the crude, un-normalized version called out in Task 7).
- [ ] Verify wall thickness at several sample points against nominal
      `Params.fWallThicknessMM`.
- [ ] Exterior skin generation via erode-and-subtract (`Params.fSkinThicknessMM`
      is defined but unused so far).
- [ ] Drop dev voxel size from 0.5mm to 0.15mm (production) and measure
      generation time + RAM — current 0.5mm membrane STL is already ~440MB per
      100mm cube fixture and **1.2GB for the real HRV volume** (232x131x140mm),
      so this needs a real look at export format/streaming before dropping
      resolution 3x on top of that.

## Session 3 — Modularization

- [ ] 100mm module tiling with seam lip and gasket groove so a 200mm core
      becomes eight prints.
- [ ] Solves memory, print risk, and leak-test cost simultaneously (per spec
      rationale) — worth re-checking the 0.15mm RAM numbers from Session 2
      against this before committing to a tile size.

## Session 4 — Parameter sweep

- [ ] Drive λ (`fCellSizeMM`), wall thickness, and seal depth from a config file
      instead of the `Params.cs` constants.
- [ ] Generate the candidate set; export for slicing and mass/time estimation.

## Known traps to keep watching (from spec section 6)

- ASCII STL loading isn't implemented in PicoGK (only binary loads) — not hit
  yet since this session only *writes* STL, never reads one back in.
- Onshape API returns meters, PicoGK is mm — only matters once/if a REST API
  import path replaces the OBJ export path.
- Don't let any code depend on exact Onshape export filenames (rev/part-number
  prefixes) — current code takes the fixture path as an explicit argument, so
  this is already fine, but keep it in mind if a directory-scan importer is
  added later.
