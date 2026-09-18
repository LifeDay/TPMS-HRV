# TPMS-HRV

Generates a 3D-printable counterflow core for a heat recovery ventilator
(HRV). The core is a gyroid, a TPMS (triply periodic minimal surface). Its
wall splits the enclosure into two interleaved channel networks, one for the
supply air and one for the exhaust air. Heat passes through the thin wall
between them.

The input is an enclosure volume exported from Onshape with its port faces
colour-coded. The output is a single watertight mesh, ready for Bambu Studio.
It is sized to print in one piece on a Bambu H2C.

Built with [PicoGK](https://github.com/leap71/PicoGK) (voxel geometry kernel)
on .NET 9.

## How it works

For each part, `Program.cs` runs these steps:

1. **Import** (`ObjImporter.cs`). Reads the OBJ/MTL, converts from metres to mm,
   and groups triangles by material colour.
2. **Classify ports** (`PortClassifier.cs`). Matches each group's diffuse
   colour against the port palette:

   | Port | Colour | Kd |
   |---|---|---|
   | SupplyIn | red | `1.000 0.000 0.000` |
   | SupplyOut | orange | `0.973 0.529 0.004` |
   | ExhaustIn | blue | `0.231 0.380 0.706` |
   | ExhaustOut | cyan | `0.616 0.812 0.929` |

   Any other colour is treated as enclosure body. An off-palette port is
   skipped without an error, so use these exact colours.
3. **Port slabs** (`SlabBuilder.cs`). Extrudes each port's faces inward as one
   closed prism, `fPortSealDepthMM` deep.
4. **Membrane** (`Gyroid.cs`). Builds the gyroid wall inside the volume. The
   field is gradient-normalised with one Newton step, which keeps the wall a
   uniform `fWallThicknessMM` thick along its normal.
5. **Port seals**. Inside each port's slab, fills the *other* stream's
   channels solid, so each port opens only onto its own stream.
6. **Skin**. Adds a `fSkinThicknessMM` outer shell, with cut-outs at the ports.
7. **Export**. Meshes the voxel core, then decimates it (`MeshDecimator.cs`,
   quadric edge collapse to an RMS surface tolerance). Writes STL, 3MF or VDB.

Each step prints a numbered checkpoint. A checkpoint that fails a hard check
throws and stops the run:

| Checkpoint | Checks |
|---|---|
| 0 | Toolchain smoke test (writes `out/checkpoint0_sphere.stl`) |
| 1–3 | Import: group and triangle counts, bounding box, colour → port role |
| 4 | Voxelised volume vs expected (warning only) |
| 5–7 | Port slabs written, ports mapped to faces, membrane and seals built |
| 7b | Every port has at least 5% open area for its assigned stream |
| 8 | Skin covers the body and leaves the ports open |
| 9 | Wall thickness at 200 random points: mean within 2% of nominal, every point within 5% |
| 10 | Decimated mesh has no open, non-manifold or degenerate edges and winds outward; reports volume change and surface deviation |

## Requirements

- Windows. It is the only platform tested so far.
- .NET 9 SDK. PicoGK 2.3.0 targets `net9.0` only and is restored from NuGet.
- RAM: about 9 GB for the real volume at 0.25 mm. See
  [Performance](#performance).

## Usage

```sh
dotnet run -c Release -- [--voxel <mm>] [--only <name>]... [--export stl|3mf|vdb|none]
                          [--decimate <mm>] [--cell <mm>] [--wall <mm>] [--seal <mm>]
```

| Option | Default | Meaning |
|---|---|---|
| `--voxel` | `0.5` | Voxel size in mm (production target is 0.15) |
| `--only` | all parts | Run only parts whose OBJ path contains `<name>`. Repeatable |
| `--export` | `stl` | Core output: `stl`, `3mf`, `vdb`, or `none` |
| `--decimate` | `0.02` | STL/3MF surface tolerance in mm. `0` keeps the raw voxel mesh |
| `--cell` | `8.0` | Gyroid wavelength λ in mm |
| `--wall` | `0.8` | Membrane thickness in mm |
| `--seal` | `0.75 × --cell` | Port seal depth in mm |

`--cell` is the main lever on output size: triangle count scales as **1/λ²**,
and unlike `--decimate` it costs no geometric accuracy — the wall stays at
nominal thickness. What it costs is heat-exchange area, which scales as 1/λ.
Measured on the 100mm cube at `--decimate 0.02`: λ 8→12 gave 2.22× fewer
triangles, λ 8→16 gave 4.26×.

Slicers have a practical ceiling on triangle count. Bambu Studio stalls
indefinitely above roughly 1M triangles on this geometry, regardless of
support and infill settings, so the real volume needs `--cell 16` to export
something sliceable:

```sh
dotnet run -c Release -- --only real --voxel 0.25 --export 3mf --cell 16 --decimate 0.05
```

To build the real enclosure for Bambu Studio:

```sh
dotnet run -c Release -- --only real --voxel 0.25 --export 3mf
```

Output goes to `out/<part>/`: the core (`core.3mf`, `core.stl` or `core.vdb`)
plus the port slab STLs from Checkpoint 5.

### Parts

The parts are listed in `aParts` at the top of `Program.cs`:

| OBJ | Output |
|---|---|
| `fixtures/test_cube_100mm.obj` | `out/test_cube_100mm` |
| `fixtures/test_cube_filleted_100mm.obj` | `out/test_cube_filleted_100mm` |
| `real-HRV-volume/real-HRV-volume.obj` | `out/real_hrv_volume` |

To add a part, export the enclosure from Onshape as OBJ with the port faces
painted in the palette colours, then add a row to `aParts`.

### Design parameters

The parameters are set in `Params.cs`:

| Constant | Value | Meaning |
|---|---|---|
| `fCellSizeMM` | 8.0 | Gyroid cell size (λ) |
| `fWallThicknessMM` | 0.8 | Membrane wall thickness |
| `fPortSealDepthMM` | 6.0 | How far each port seal reaches into the core |
| `fSkinThicknessMM` | 1.5 | Outer skin thickness |
| `fImportScale` | 1000 | OBJ metres → mm |
| `fColorTolerance` | 0.15 | Maximum RGB distance for a palette match |

## Performance

Measured on the real volume (232 × 131 × 140 mm) with a Ryzen 7 5700U and
15 GB RAM:

| Voxel | Output | Triangles | File | Deviation mean / p99 / max | Time | Peak RAM |
|---|---|---|---|---|---|---|
| 0.25 mm | raw STL | 94.0M | 4.7 GB | 0.003 / 0.016 / 0.093 mm | ~11 min | 8.1 GB |
| 0.25 mm | 3MF, 0.02 mm tolerance | 11.1M | 137 MB | 0.014 / 0.041 / 0.127 mm | ~13 min | 8.4 GB |
| 0.5 mm | 3MF, 0.02 mm tolerance | 11.5M | 131 MB | 0.018 / 0.061 / 0.214 mm | — | — |

Notes on these results:

- The measured wall is 0.801 ± 0.002 mm, against a nominal 0.8 mm.
- Decimation changes the volume by less than 0.03%.
- After decimation, the output size depends little on voxel size.
- Meshing is the step that limits memory. At 0.15 mm the real volume needs
  about 23 GB, so it does not fit on this machine yet. The 100 mm cube does.

## Status

Done:

- The full pipeline, Checkpoints 0–10.
- Decimated 3MF export.
- The real volume at 0.25 mm.

Still to do:

- Confirm the 3MF loads and slices well in Bambu Studio.
- Mesh the real volume at 0.15 mm in slabs.
- Load parameters from a config file for parameter sweeps.

See [todo.md](todo.md) for the full list, open questions and known PicoGK
issues. One known PicoGK issue is that `IntersectImplicit` crashes at voxel
sizes ≤ 0.33 mm, which the pipeline works around.

## Repository layout

| File | Purpose |
|---|---|
| `Program.cs` | Command line, pipeline, checkpoints, export |
| `ObjImporter.cs` | OBJ/MTL parser |
| `PortClassifier.cs` | Colour → port role |
| `PortAssertion.cs` | Maps port groups to bounding-box faces |
| `SlabBuilder.cs` | Closed prism extrusion of port faces |
| `Gyroid.cs` | Normalised gyroid field and stream implicits |
| `MeshDecimator.cs` | Parallel quadric decimation, topology checks, 3MF/STL writers |
| `Params.cs` | Design constants |
| `fixtures/` | 100 mm test cubes |
| `real-HRV-volume/` | The real enclosure export |
