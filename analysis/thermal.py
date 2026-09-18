#!/usr/bin/env python3
"""Counterflow effectiveness model for the gyroid HRV core.

Answers "is lambda = 16 thermally acceptable?" (todo.md, Session 3) with a
number instead of a guess, and backs the lambda / fan recommendation recorded
there.

The geometry constants below are MEASURED from the pipeline's own output, not
assumed. `measure()` re-derives them from out/real_hrv_volume/ (gitignored, so
regenerate with `dotnet run` first); it needs numpy, the model itself does not.

Validation: the area model predicts 1.09 m^2 total wetted area for the
lambda=16 real part; the exported 3MF measures 1.094 m^2.

Every effectiveness figure is a correlation estimate, not a measurement. The
Nusselt bracket is the dominant uncertainty (~+/-1.7x on effectiveness at
lambda=16) and the friction factor is second. Treat the ranking of lambda as
robust and the absolute numbers as provisional until there is a rig or CFD.

Usage:  python analysis/thermal.py [--measure]
"""
import sys

# --- geometry measured from out/real_hrv_volume/ -----------------------------
V_ENV    = 2.5588e-3        # m^3  enclosure interior, volume.stl signed volume
L_FLOW   = 0.2321           # m    long axis, taken as the counterflow length
A_CROSS  = 0.13105 * 0.140  # m^2  bbox cross-section normal to flow
V_SKIN   = 0.20e-3          # m^3  skin + port seal solid: 0.657 L measured core
                            #      solid at lambda=16 minus the gyroid sheet
A_SKIN   = 0.1355           # m^2  enclosure outer surface area
T_WALL   = 0.8e-3           # m    --wall default
GYROID_A = 3.091            # Schoen gyroid: area 3.091*a^2 per cubic cell side a

# --- air at 20 C, and the wall material -------------------------------------
RHO, CP, MU, K_AIR, PR = 1.20, 1005.0, 1.81e-5, 0.0257, 0.71
K_WALL = 0.20               # W/mK  PETG ~0.20, PLA ~0.13

# One fan per stream, so there is no parallel-flow benefit. Linear fan curve
# through (Q_max, 0) and (0, dp_max) -- crude but adequate at these pressures.
FANS = {
    '140mm case fan (NF-A14 class)':       (140 / 3600.0, 20.0),
    '140mm high-static (iPPC-3000 class)': (270 / 3600.0, 103.0),
}

# f*Re = 64*K_FRIC. K_FRIC covers gyroid tortuosity and shape; published TPMS
# Fanning f*Re of 20-30 implies K_FRIC ~ 1.25-1.9.
K_FRIC_NOMINAL = 1.5


def geom(lam_mm):
    """Per-stream flow geometry at gyroid cell size lam_mm."""
    lam = lam_mm * 1e-3
    sig = GYROID_A / lam              # midsurface area per unit volume, m^2/m^3
    a_mid = sig * V_ENV               # heat transfer area (sheet counted once)
    v_sheet = a_mid * T_WALL
    poros = (V_ENV - v_sheet - V_SKIN) / V_ENV
    v_str = V_ENV * poros / 2         # free volume per stream
    a_wet = a_mid + A_SKIN / 2        # wetted area per stream
    return dict(lam=lam_mm, sig=sig, a_mid=a_mid, poros=poros,
                dh=4 * v_str / a_wet, ac=A_CROSS * poros / 2)


def nusselt(re, mode):
    """Bracket, not a correlation: the floor assumes no enhancement at all."""
    if mode == 'floor':
        return 8.0                              # developing laminar duct
    if mode == 'tpms':
        return 0.1 * re ** 0.7 * PR ** (1 / 3)  # TPMS fit, secondary flows
    raise ValueError(mode)


def core_dp(g, q, k_fric=K_FRIC_NOMINAL):
    """Core-only Darcy pressure drop. Excludes ducting, filters, port losses."""
    u = q / g['ac']
    re = RHO * u * g['dh'] / MU
    f = 64 * k_fric / re
    return f * (L_FLOW / g['dh']) * 0.5 * RHO * u * u, re, u


def effectiveness(g, q, nu_mode):
    """Balanced counterflow (C* = 1), so eff = NTU / (1 + NTU)."""
    u = q / g['ac']
    re = RHO * u * g['dh'] / MU
    h = nusselt(re, nu_mode) * K_AIR / g['dh']
    u_overall = 1.0 / (2.0 / h + T_WALL / K_WALL)   # two films plus the wall
    ua = u_overall * g['a_mid']
    ntu = ua / (RHO * q * CP)
    return ntu / (1 + ntu), ntu, ua, h, re


def operating_point(g, q_max, dp_max, k_fric=K_FRIC_NOMINAL):
    """Where the fan curve meets the core's resistance."""
    lo, hi = 1e-9, q_max
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if core_dp(g, mid, k_fric)[0] > dp_max * (1 - mid / q_max):
            hi = mid
        else:
            lo = mid
    return 0.5 * (lo + hi)


def cmh(q):
    return q * 3600.0


def report():
    print('=== geometry per lambda (0.8mm wall, 2.559 L envelope) ===')
    print(f"{'lam':>5} {'area m2':>9} {'porosity':>9} {'Dh mm':>7} {'Ac cm2':>8}")
    for lam in (8, 12, 16):
        g = geom(lam)
        print(f"{lam:>5} {g['a_mid']:>9.3f} {g['poros']:>9.3f} "
              f"{g['dh'] * 1e3:>7.2f} {g['ac'] * 1e4:>8.1f}")

    print('\n=== effectiveness at fixed flow (ignores whether a fan can push it) ===')
    for qc in (50, 100, 150):
        q = qc / 3600.0
        print(f'\n  {qc} m3/h per stream:')
        print(f"    {'lam':>5} {'Re':>6} {'h':>7} {'UA':>6} {'NTU':>5} {'eff':>6} "
              f"{'dP Pa':>7}  bound")
        for lam in (8, 12, 16):
            g = geom(lam)
            for mode in ('floor', 'tpms'):
                eff, ntu, ua, h, re = effectiveness(g, q, mode)
                print(f"    {lam:>5} {re:>6.0f} {h:>7.1f} {ua:>6.1f} {ntu:>5.2f} "
                      f"{eff:>6.1%} {core_dp(g, q)[0]:>7.1f}  {mode}")

    print('\n=== recovered heat at the fan operating point, dT = 20 K ===')
    print('    the objective is eff x flow, not eff alone')
    for name, (q_max, dp_max) in FANS.items():
        print(f'\n  {name}')
        print(f"    {'lam':>5} {'Q m3/h':>8} {'dP Pa':>7} {'eff':>7} {'kW':>7}")
        best = None
        for lam in (8, 10, 12, 14, 16, 20, 24, 32):
            g = geom(lam)
            q = operating_point(g, q_max, dp_max)
            eff = 0.5 * (effectiveness(g, q, 'floor')[0]
                         + effectiveness(g, q, 'tpms')[0])
            kw = eff * RHO * q * CP * 20.0 / 1000.0
            print(f"    {lam:>5} {cmh(q):>8.1f} {core_dp(g, q)[0]:>7.1f} "
                  f"{eff:>7.1%} {kw:>7.3f}")
            if best is None or kw > best[1]:
                best = (lam, kw)
        print(f'    -> peak recovered heat at lambda = {best[0]}mm '
              f'({best[1]:.3f} kW)')

    print('\n=== area needed for 80% effectiveness (NTU = 4) ===')
    print('    shows the envelope, not lambda, is the binding limit')
    for qc in (50, 100, 150):
        q = qc / 3600.0
        g = geom(8)                     # lambda=8 film coefficient as reference
        for mode in ('floor', 'tpms'):
            _, _, _, h, _ = effectiveness(g, q, mode)
            u_overall = 1.0 / (2.0 / h + T_WALL / K_WALL)
            need = 4 * RHO * q * CP / u_overall
            print(f"  {qc:>4} m3/h [{mode:>5}]: need {need:>5.2f} m2  "
                  f"(have {geom(8)['a_mid']:.2f} at lambda=8, "
                  f"{geom(16)['a_mid']:.2f} at lambda=16) "
                  f"-> {need / geom(8)['a_mid']:.1f}x the lambda=8 core")


def measure():
    """Re-derive the geometry constants from the pipeline's exports."""
    import re as _re
    import struct
    import zipfile
    try:
        import numpy as np
    except ImportError:
        sys.exit('--measure needs numpy: pip install numpy')

    def props(v):
        """Signed volume, area and bbox of a triangle soup, shape (n, 3, 3)."""
        v0, v1, v2 = v[:, 0], v[:, 1], v[:, 2]
        vol = np.einsum('ij,ij->i', v0, np.cross(v1, v2)).sum() / 6.0
        area = 0.5 * np.linalg.norm(np.cross(v1 - v0, v2 - v0), axis=1).sum()
        pts = v.reshape(-1, 3)
        return vol, area, pts.min(0), pts.max(0)

    with open('out/real_hrv_volume/volume.stl', 'rb') as f:
        f.read(80)
        n = struct.unpack('<I', f.read(4))[0]
        dt = np.dtype([('n', '<3f4'), ('v', '(3,3)f4'), ('a', '<u2')])
        tris = np.fromfile(f, dtype=dt, count=n)
    vol, area, lo, hi = props(tris['v'].astype(np.float64))
    print(f'volume.stl: {n} tris, bbox {np.round(hi - lo, 2)} mm')
    print(f'  V_ENV  = {vol / 1e9:.4e} m^3   (constant: {V_ENV:.4e})')
    print(f'  A_SKIN = {area / 1e6:.4f} m^2  (constant: {A_SKIN:.4f})')

    path = 'out/real_hrv_volume/real_cell16_tol0.05.3mf'
    z = zipfile.ZipFile(path)
    doc = z.read(next(x for x in z.namelist()
                      if x.endswith('.model'))).decode('utf-8')
    verts = np.array(_re.findall(
        r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"', doc), dtype=np.float64)
    idx = np.array(_re.findall(
        r'<triangle v1="(\d+)" v2="(\d+)" v3="(\d+)"', doc), dtype=np.int64)
    vol, area, _, _ = props(verts[idx])
    print(f'\n{path}: {len(idx)} tris')
    print(f'  core solid volume  = {vol / 1e9:.4e} m^3')
    print(f'  total wetted area  = {area / 1e6:.4f} m^2')
    print(f'  model predicts       '
          f'{2 * geom(16)["a_mid"] + A_SKIN * 0.74:.4f} m^2'
          '  (2 x midsurface + skin less port openings)')


if __name__ == '__main__':
    if '--measure' in sys.argv:
        measure()
    else:
        report()
