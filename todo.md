# TPMS-HRV — remaining work

Status: Session 1 (importer + stub gyroid) implemented, verified against both
fixtures, committed as `eae0856`. This file tracks what's left.

## Session 1 loose ends

- [ ] **Visually confirm the debug STLs in a slicer** — `out/checkpoint0_sphere.stl`,
      the `group_*.stl` / `slab_*.stl` files, and `membrane.stl` for both fixtures.
      Numeric checks (bbox, volume %, position assertion) all pass, but the spec
      calls this "the single most likely thing in the session to be wrong" and it
      hasn't been eyeballed yet.
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
- [ ] Never run against the real ERV design volume yet — only the two 100mm cube
      fixtures. Swapping in the real OBJ will exercise the "coarse tessellation
      shows up as facet steps" trap the spec calls out; re-export the real volume
      at fine tessellation before first use.
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
      generation time + RAM — current 0.5mm membrane STL is already ~440MB
      per fixture, so this needs a real look at export format/streaming before
      dropping resolution 3x.

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
