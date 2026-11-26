"""Corridor planner: candidate bundle paths through an occupancy grid (no SolidWorks, no HTTP).

Input is the payload of the execution server's `occupancy_grid` tool:
    {"origin": [x0, y0, z0] (m), "resolution": r (m), "dims": [nx, ny, nz],
     "occupied_rle": [[start, count], ...]}            linear index i = ix + nx * (iy + ny * iz)
Voxel (ix, iy, iz) covers [x0 + ix * r, x0 + (ix + 1) * r) per axis (origin = the grid's min
corner). A voxel is occupied when any surface touches it; closed solids are therefore only a
shell, so the free space is the part reachable from the start (flood fill), and the path never
leaves the grid.

Pipeline (plan_corridor):
1. Decode the RLE into a padded grid and pack it into one Python int (one bit per voxel): bulk
   operations (dilation, flood fill, erosion) are shifts / ANDs / ORs on that int, C speed.
2. Configuration space: occupied voxels dilated by R = bundle radius + clearance. A voxel is
   blocked when the CENTRE-TO-CUBE distance to an occupied voxel is < R (the surface may be
   anywhere inside that voxel); built as x-runs of growing width OR-ed over a disc of (dy, dz)
   offsets. Between voxel centres a sampled point can be up to ~0.87 voxel closer than R: keep
   `clearance` >= one voxel when it matters, and probe with check_path_clearance.
3. Leads: the path leaves the start along start_dir for `lead` (default bend_radius, so a first
   90 deg bend fits) and arrives at the end along end_dir. A lead point inside blocked space
   slides further along its ray (warning). Free space = voxels 6-connected to the start lead
   (flood fill with the carry trick for x-runs).
4. Search: A* with 26-neighbour moves (no corner cutting) on a coarse grid (cells of f^3 voxels,
   a cell free only when all its voxels are free; relaxed next to the leads), cost = length x
   (1 + w_clear x nearness to obstacles + w_div x previous-candidate cells) + turn penalty
   (proportional to the turn angle, against the parent move). A coarse failure retries finer.
5. String pulling on the FINE grid (greedy furthest line of sight, exponential + binary search),
   a local fine A* where a coarse step is not visible.
6. Fillets: every interior corner gets a tangent arc of bend_radius; its tangent length
   r * tan(theta / 2) must fit both legs (adjacent corners share their leg, the end points have
   none). Repairs: slide a lead point along its ray, drop or merge corners, push corners outward
   along their bisector (lengthens the shared leg, moves the arc off the obstacle it wraps).
7. Verification: the filleted curve (arcs included) is sampled every 0.4 voxel against the free
   space; min clearance = distance to the nearest occupied voxel centre - half a voxel - bundle
   radius (voxel accurate). Samples within the blocked zone next to the end points (the
   connectors the bundle leaves / enters, touched by design) are not checked.
Candidates differ by turn penalty, clearance weight and a penalty on earlier candidates' cells;
near duplicates are dropped; ranked by length + one bend radius per corner.
"""

import heapq
import math
import time
from array import array

STEP = 0.4  # sampling step along segments / arcs, voxels

_TO01 = bytes.maketrans(b"01", b"\x00\x01")
_FROM01 = bytes.maketrans(b"\x00\x01", b"01")
SQ2 = math.sqrt(2.0)
SQ3 = math.sqrt(3.0)


# ---------------------------------------------------------------------------
# Small vector helpers (lists of 3 floats)
# ---------------------------------------------------------------------------
def _sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def _add(a, b):
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def _mul(a, s):
    return [a[0] * s, a[1] * s, a[2] * s]


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _norm(a):
    return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])


def _unit(a):
    n = _norm(a)
    return [a[0] / n, a[1] / n, a[2] / n] if n > 1e-12 else [0.0, 0.0, 0.0]


def _dist(a, b):
    return _norm(_sub(a, b))


# ---------------------------------------------------------------------------
# Bit sets <-> byte maps
# ---------------------------------------------------------------------------
def bits_to_bytes(x: int, n: int) -> bytearray:
    """Bit i of x -> byte i (0 / 1), n bytes."""
    if x <= 0:
        return bytearray(n)
    b = format(x, "b")[::-1].encode("ascii").translate(_TO01)
    if len(b) < n:
        return bytearray(b) + bytearray(n - len(b))
    return bytearray(b[:n])


def bytes_to_bits(b) -> int:
    """Byte map of 0 / 1 values -> int with bit i = byte i."""
    s = bytes(b).translate(_FROM01)[::-1]
    return int(s, 2) if s else 0


def decode_rle(rle, n: int) -> bytearray:
    """occupied_rle ([[start, count], ...] or a flat [s, c, s, c, ...]) -> byte map of n voxels."""
    occ = bytearray(n)
    if not rle:
        return occ
    pairs = rle if isinstance(rle[0], (list, tuple)) else list(zip(rle[0::2], rle[1::2]))
    for s, c in pairs:
        s, c = int(s), int(c)
        if c <= 0:
            continue
        if s < 0 or s + c > n:
            raise ValueError(f"occupied_rle run [{s}, {c}] outside the grid ({n} voxels)")
        occ[s:s + c] = b"\x01" * c
    return occ


def encode_rle(occ) -> list:
    """Byte map -> [[start, count], ...] (for tests and saved grids)."""
    out, i, n = [], 0, len(occ)
    while i < n:
        j = occ.find(1, i) if isinstance(occ, (bytes, bytearray)) else -1
        if j < 0:
            break
        k = occ.find(0, j)
        if k < 0:
            k = n
        out.append([j, k - j])
        i = k
    return out


def _ks_fill(gen: int, pro: int, step: int, length: int, up: bool) -> int:
    """Kogge-Stone occluded fill: gen spread along runs of `pro` bits spaced `step` apart
    (toward higher bits when up), in log2(length) shift rounds."""
    s = step
    span = 1
    while span < length:
        if up:
            gen |= pro & (gen << s)
            pro &= pro << s
        else:
            gen |= pro & (gen >> s)
            pro &= pro >> s
        s *= 2
        span *= 2
    return gen


def _erode_runs(e: int, f: int, step: int) -> int:
    """Bit i set iff bits i, i+step, ..., i+(f-1)*step are all set."""
    cov, cur = 1, e
    while cov * 2 <= f:
        cur &= cur >> (cov * step)
        cov *= 2
    if cov < f:
        cur &= cur >> ((f - cov) * step)
    return cur


# ---------------------------------------------------------------------------
# Fillet geometry (pure; any length unit)
# ---------------------------------------------------------------------------
def corner_tangents(points, radii) -> list:
    """[(deflection rad, tangent length r * tan(theta / 2)), ...] for each interior point.
    radii: one per interior point, or a number. A 180 deg reversal has an infinite tangent."""
    n = len(points)
    out = []
    for k in range(1, n - 1):
        r = radii if isinstance(radii, (int, float)) else radii[k - 1]
        u1 = _unit(_sub(points[k], points[k - 1]))
        u2 = _unit(_sub(points[k + 1], points[k]))
        c = max(-1.0, min(1.0, _dot(u1, u2)))
        th = math.acos(c)
        t = math.inf if th > math.pi - 1e-9 else r * math.tan(th / 2.0)
        out.append((th, t))
    return out


def fillet_check(points, radii, tol: float = 1e-9) -> dict:
    """Do the fillets fit? Every leg must hold the tangent lengths of both its corners (the end
    points have none): slack = leg - t_a - t_b >= 0.
    Returns {ok, corners: [{deflection_deg, tangent}], legs: [{length, need, slack}], worst_leg}."""
    tans = corner_tangents(points, radii)
    t = [0.0] + [x[1] for x in tans] + [0.0]
    legs = []
    for k in range(len(points) - 1):
        length = _dist(points[k], points[k + 1])
        need = t[k] + t[k + 1]
        legs.append({"length": length, "need": need, "slack": length - need})
    worst = min(range(len(legs)), key=lambda k: legs[k]["slack"]) if legs else None
    return {
        "ok": all(leg["slack"] >= -tol for leg in legs),
        "corners": [{"deflection_deg": math.degrees(th), "tangent": tt} for th, tt in tans],
        "legs": legs,
        "worst_leg": worst,
    }


def _corner_arc(prev, v, nxt, r):
    """(theta, t, T1, T2, C, e1, u1) of the fillet at v; T1 = None when straight."""
    u1 = _unit(_sub(v, prev))
    u2 = _unit(_sub(nxt, v))
    c = max(-1.0, min(1.0, _dot(u1, u2)))
    th = math.acos(c)
    if th < 1e-7:
        return th, 0.0, None, None, None, None, u1
    if th > math.pi - 1e-9:
        return th, math.inf, None, None, None, None, u1
    t = r * math.tan(th / 2.0)
    t1 = _sub(v, _mul(u1, t))
    t2 = _add(v, _mul(u2, t))
    bis = _unit(_sub(u2, u1))
    cen = _add(v, _mul(bis, r / math.cos(th / 2.0)))
    e1 = _unit(_sub(t1, cen))
    return th, t, t1, t2, cen, e1, u1


def _arc_samples(th, cen, e1, u1, r, step):
    n = max(2, int(math.ceil(r * th / step)))
    out = []
    for k in range(n + 1):
        ph = th * k / n
        c, s = math.cos(ph) * r, math.sin(ph) * r
        out.append([cen[0] + e1[0] * c + u1[0] * s, cen[1] + e1[1] * c + u1[1] * s,
                    cen[2] + e1[2] * c + u1[2] * s])
    return out


def filleted_samples(points, radius, step):
    """Samples of the filleted polyline: [(point, part)], part 'lead_s' (straight part of the first
    leg), 'lead_e' (straight part of the last leg) or 'body'; plus the curve length."""
    n = len(points)
    arcs = [None] * n
    tan = [0.0] * n
    for k in range(1, n - 1):
        th, t, t1, t2, cen, e1, u1 = _corner_arc(points[k - 1], points[k], points[k + 1], radius)
        tan[k] = t
        if t1 is not None:
            arcs[k] = (th, cen, e1, u1)
    out, length = [], 0.0
    for k in range(n - 1):
        a, b = points[k], points[k + 1]
        u = _unit(_sub(b, a))
        p = _add(a, _mul(u, tan[k]))
        q = _sub(b, _mul(u, tan[k + 1]))
        seg = max(0.0, _dist(a, b) - tan[k] - tan[k + 1])
        length += seg
        part = "lead_s" if k == 0 else ("lead_e" if k == n - 2 else "body")
        m = max(1, int(math.ceil(seg / step)))
        for i in range(m + 1):
            out.append((_add(p, _mul(_sub(q, p), i / m)), part))
        if k + 1 < n - 1 and arcs[k + 1] is not None:
            th, cen, e1, u1 = arcs[k + 1]
            length += radius * th
            out.extend((s, "body") for s in _arc_samples(th, cen, e1, u1, radius, step))
    return out, length


# ---------------------------------------------------------------------------
# A* on a padded linear grid (pad cells are 0 in `free`, so no bounds checks)
# ---------------------------------------------------------------------------
_MOVES = [(dx, dy, dz) for dz in (-1, 0, 1) for dy in (-1, 0, 1) for dx in (-1, 0, 1)
          if (dx, dy, dz) != (0, 0, 0)]


def _turn_table(turn_cost: float) -> list:
    rows = []
    for a in _MOVES:
        ua = _unit(list(a))
        row = []
        for b in _MOVES:
            ub = _unit(list(b))
            ang = math.acos(max(-1.0, min(1.0, _dot(ua, ub))))
            row.append(turn_cost * ang / (math.pi / 2.0))
        rows.append(row)
    rows.append([0.0] * 26)  # 26: no direction yet
    return rows


def nearest_move(d) -> int:
    u = _unit(list(d))
    return max(range(26), key=lambda k: _dot(u, _unit(list(_MOVES[k]))))


def astar(free, dims, start: int, goal: int, *, cost=None, cost_tab=None, pen=None,
          pen_w: float = 0.0, turn_cost: float = 0.0, start_move: int = 26,
          heur_w: float = 1.0, max_expansions: int = 2_000_000, deadline: float = math.inf):
    """Cells start -> goal on a padded grid (free[i] = 1 for free cells; the outer layer must be 0).
    Returns (path cell list | None, expansions, reason)."""
    X, Y, Z = dims
    S = X * Y
    N = X * Y * Z
    if not free[start] or not free[goal]:
        return None, 0, "start or goal cell not free"
    moves = []
    for k, (dx, dy, dz) in enumerate(_MOVES):
        req = tuple(ex + X * ey + S * ez for ex in {0, dx} for ey in {0, dy} for ez in {0, dz}
                    if (ex, ey, ez) not in ((0, 0, 0), (dx, dy, dz)))
        moves.append((k, dx + X * dy + S * dz, math.sqrt(dx * dx + dy * dy + dz * dz), req))
    tt = _turn_table(turn_cost)
    ctab = cost_tab if cost_tab is not None else [0.0] * 256
    if cost is None:
        cost = bytes(N)
    if pen is None:
        pen = bytes(N)
    gx, r = goal % X, goal // X
    gy, gz = r % Y, r // Y

    def h(m):
        x, rr = m % X, m // X
        a, b, c = abs(x - gx), abs(rr % Y - gy), abs(rr // Y - gz)
        hi = max(a, b, c)
        lo = min(a, b, c)
        mid = a + b + c - hi - lo
        return SQ3 * lo + SQ2 * (mid - lo) + (hi - mid)

    inf = math.inf
    g = array("d", [inf]) * N
    parent = array("i", [-1]) * N
    dirs = bytearray([26]) * N
    closed = bytearray(N)
    g[start] = 0.0
    dirs[start] = start_move
    heap = [(heur_w * h(start), 0.0, start)]
    push, pop = heapq.heappush, heapq.heappop
    exp = 0
    while heap:
        _, gc, n = pop(heap)
        if closed[n]:
            continue
        closed[n] = 1
        if n == goal:
            path = [n]
            while n != start:
                n = parent[n]
                path.append(n)
            path.reverse()
            return path, exp, ""
        exp += 1
        if exp > max_expansions:
            return None, exp, f"search budget exhausted ({max_expansions} expansions)"
        if (exp & 1023) == 0 and time.perf_counter() > deadline:
            return None, exp, "time budget exhausted"
        trow = tt[dirs[n]]
        for k, d, length, req in moves:
            m = n + d
            if not free[m] or closed[m]:
                continue
            if req:
                ok = True
                for q in req:
                    if not free[n + q]:
                        ok = False
                        break
                if not ok:
                    continue
            ng = gc + length * (1.0 + ctab[cost[m]] + pen_w * pen[m]) + trow[k]
            if ng < g[m]:
                g[m] = ng
                parent[m] = n
                dirs[m] = k
                push(heap, (ng + heur_w * h(m), ng, m))
    return None, exp, "no path (start and goal not connected)"


# ---------------------------------------------------------------------------
# The planner
# ---------------------------------------------------------------------------
class CorridorError(ValueError):
    pass


class _Planner:
    def __init__(self, grid: dict, diameter: float, bend_radius: float, clearance: float,
                 coarse_cells: int):
        try:
            self.origin = [float(v) for v in grid["origin"]]
            self.res = float(grid["resolution"])
            nx, ny, nz = (int(v) for v in grid["dims"])
        except (KeyError, TypeError, ValueError) as exc:
            raise CorridorError(f"grid: needs origin, resolution, dims ({exc})") from exc
        if self.res <= 0 or min(nx, ny, nz) <= 0:
            raise CorridorError("grid: resolution and dims must be positive")
        self.nreal = (nx, ny, nz)
        self.radius = diameter / 2.0
        self.R = diameter / 2.0 + clearance
        self.rv = self.R / self.res
        self.rb = bend_radius / self.res
        nreal = nx * ny * nz
        self.f0 = max(1, int(math.ceil((nreal / max(1000, coarse_cells)) ** (1.0 / 3.0) - 1e-9)))
        P = max(int(math.ceil(self.rv + 0.5)) + 2, self.f0 + 1, 2)
        self.P = P
        X, Y, Z = nx + 2 * P, ny + 2 * P, nz + 2 * P
        self.X, self.Y, self.Z, self.S, self.N = X, Y, Z, X * Y, X * Y * Z
        self.full = (1 << self.N) - 1
        raw = decode_rle(grid.get("occupied_rle") or [], nreal)
        self.occupied_count = raw.count(1)
        occ = bytearray(self.N)
        inside = bytearray(self.N)
        ones = b"\x01" * nx
        for iz in range(nz):
            for iy in range(ny):
                src = nx * (iy + ny * iz)
                dst = P + X * ((iy + P) + Y * (iz + P))
                occ[dst:dst + nx] = raw[src:src + nx]
                inside[dst:dst + nx] = ones
        self.occ = occ
        self.occ_bits = bytes_to_bits(occ)
        self.inside_bits = bytes_to_bits(inside)
        self.reach = None
        self.zones = []
        self.timing = {}

    # -- coordinates ------------------------------------------------------
    def to_u(self, p):
        return [(p[i] - self.origin[i]) / self.res + self.P for i in range(3)]

    def to_w(self, u):
        return [(u[i] - self.P) * self.res + self.origin[i] for i in range(3)]

    def in_region(self, u) -> bool:
        return all(self.P <= u[i] < self.P + self.nreal[i] for i in range(3))

    def vidx(self, u) -> int:
        x, y, z = u
        if x < 0 or y < 0 or z < 0:
            return -1
        ix, iy, iz = int(x), int(y), int(z)
        if ix >= self.X or iy >= self.Y or iz >= self.Z:
            return -1
        return ix + self.X * (iy + self.Y * iz)

    # -- configuration space ---------------------------------------------
    def inflate(self):
        t0 = time.perf_counter()
        rv, X, S = self.rv, self.X, self.S
        r2 = rv * rv
        K = int(math.ceil(rv + 0.5))
        xs = [self.occ_bits]
        out = 0
        for dz in range(-K, K + 1):
            ez = max(abs(dz) - 0.5, 0.0)
            for dy in range(-K, K + 1):
                ey = max(abs(dy) - 0.5, 0.0)
                rem = r2 - ey * ey - ez * ez
                if rem <= 0:
                    if dy or dz:
                        continue
                    w = 0
                else:
                    w = max(0, int(math.ceil(math.sqrt(rem) + 0.5)) - 1)
                while len(xs) <= w:
                    v = xs[-1]
                    xs.append(v | (v << 1) | (v >> 1))
                sh = dy * X + dz * S
                v = xs[w]
                out |= (v << sh) if sh >= 0 else (v >> -sh)
        self.blocked_bits = out & self.full
        self.free_bits = self.inside_bits & ~self.blocked_bits
        self.free = bits_to_bytes(self.free_bits, self.N)
        self.timing["inflate_s"] = round(time.perf_counter() - t0, 3)

    def flood(self, seed_idx: int):
        """6-connected flood fill of the free space from one voxel. Each pass fills whole runs
        along x (upward: the carry of M + F; downward: Kogge-Stone), then along y and z
        (Kogge-Stone both ways), so the pass count is the number of axis changes, not the
        path length. Pad voxels are not free, so runs never wrap across rows."""
        t0 = time.perf_counter()
        M = self.free_bits
        X, S = self.X, self.S
        F = (1 << seed_idx) & M
        it = 0
        while True:
            it += 1
            F2 = F | (((M + F) ^ M) & M)
            F2 = _ks_fill(F2, M, 1, self.X, up=False)
            F2 = _ks_fill(F2, M, X, self.Y, up=True)
            F2 = _ks_fill(F2, M, X, self.Y, up=False)
            F2 = _ks_fill(F2, M, S, self.Z, up=True)
            F2 = _ks_fill(F2, M, S, self.Z, up=False)
            if F2 == F:
                break
            F = F2
        self.reach_bits = F
        self.reach = bits_to_bytes(F, self.N)
        self.timing["flood_s"] = round(time.perf_counter() - t0, 3)
        self.timing["flood_iterations"] = it

    # -- free-space tests (voxel units) -----------------------------------
    def _in_zone(self, x, y, z) -> bool:
        for c, r2 in self.zones:
            dx, dy, dz = x - c[0], y - c[1], z - c[2]
            if dx * dx + dy * dy + dz * dz < r2:
                return True
        return False

    def point_free(self, u, space=None) -> bool:
        sp = self.reach if space is None else space
        i = self.vidx(u)
        return i >= 0 and sp[i] == 1

    def seg_free(self, a, b) -> bool:
        reach, X, Y, Z, S = self.reach, self.X, self.Y, self.Z, self.S
        zones = self.zones
        dx, dy, dz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        n = int(math.sqrt(dx * dx + dy * dy + dz * dz) / STEP) + 1
        for k in range(n + 1):
            t = k / n
            x, y, z = a[0] + dx * t, a[1] + dy * t, a[2] + dz * t
            if zones and self._in_zone(x, y, z):
                continue
            if x < 0 or y < 0 or z < 0:
                return False
            ix, iy, iz = int(x), int(y), int(z)
            if ix >= X or iy >= Y or iz >= Z or not reach[ix + X * iy + S * iz]:
                return False
        return True

    def pts_free(self, pts) -> bool:
        for p in pts:
            if self.zones and self._in_zone(p[0], p[1], p[2]):
                continue
            if not self.point_free(p):
                return False
        return True

    def arc_free(self, P, c) -> bool:
        th, t, t1, t2, cen, e1, u1 = _corner_arc(P[c - 1], P[c], P[c + 1], self.rb)
        if t1 is None:
            return t != math.inf
        return self.pts_free(_arc_samples(th, cen, e1, u1, self.rb, STEP))

    # -- ray walk for the lead points --------------------------------------
    def walk_ray(self, origin_u, d, lead_v, cap_v, space):
        """(first s from which the ray stays free up to the lead point, lead s) or None."""
        step = 0.25
        last_blocked = -1.0
        s = 0.0
        while s <= cap_v:
            u = _add(origin_u, _mul(d, s))
            if not self.point_free(u, space):
                last_blocked = s
            elif s >= lead_v and s - last_blocked >= 1.0:
                return (last_blocked + step if last_blocked >= 0 else 0.0), s
            s += step
        return None

    # -- coarse grid ---------------------------------------------------------
    def coarse(self, f: int, ends_u):
        """(free, dims, to_cell, centre_of, inside_bits) of the search grid with cells of f^3 voxels."""
        if f == 1:
            X, Y, P = self.X, self.Y, self.P

            def to_cell(u):
                return int(u[0]) + X * (int(u[1]) + Y * int(u[2]))

            def centre(c):
                return [c % X + 0.5, (c // X) % Y + 0.5, c // (X * Y) + 0.5]
            return bytearray(self.reach), (self.X, self.Y, self.Z), to_cell, centre, self.inside_bits
        X, Y, S, P = self.X, self.Y, self.S, self.P
        e = _erode_runs(self.reach_bits, f, 1)
        e = _erode_runs(e, f, X)
        e = _erode_runs(e, f, S)
        eb = bits_to_bytes(e, self.N)
        cx, cy, cz = (int(math.ceil(n / f)) for n in self.nreal)
        GX, GY, GZ = cx + 2, cy + 2, cz + 2
        G = bytearray(GX * GY * GZ)
        inside_b = bytearray(GX * GY * GZ)
        ones = b"\x01" * cx
        for kz in range(cz):
            jz = P + kz * f
            for ky in range(cy):
                jy = P + ky * f
                base = P + X * (jy + Y * jz)
                row = eb[base:base + f * cx:f]
                gb = 1 + GX * ((ky + 1) + GY * (kz + 1))
                G[gb:gb + len(row)] = row
                inside_b[gb:gb + cx] = ones

        def to_cell(u):
            g = [min(max(int((u[i] - P) // f) + 1, 1), (cx, cy, cz)[i]) for i in range(3)]
            return g[0] + GX * (g[1] + GY * g[2])

        def centre(c):
            g = (c % GX, (c // GX) % GY, c // (GX * GY))
            return [P + (g[i] - 1) * f + f / 2.0 for i in range(3)]

        # Relaxed next to the lead points: a cell is free when its centre voxel is.
        for u in ends_u:
            c0 = to_cell(u)
            x0, y0, z0 = c0 % GX, (c0 // GX) % GY, c0 // (GX * GY)
            for dz in range(-2, 3):
                for dy in range(-2, 3):
                    for dx in range(-2, 3):
                        x, y, z = x0 + dx, y0 + dy, z0 + dz
                        if 1 <= x <= cx and 1 <= y <= cy and 1 <= z <= cz:
                            c = x + GX * (y + GY * z)
                            if self.point_free(centre(c)):
                                G[c] = 1
            G[c0] = 1
        return G, (GX, GY, GZ), to_cell, centre, bytes_to_bits(inside_b)

    @staticmethod
    def clearance_cost(G, dims, D: int, inside: int) -> bytes:
        """Per cell: how near the nearest non-free cell INSIDE the region is, D (adjacent) .. 0
        (D or more cells away; box distance). The region boundary is not an obstacle."""
        GX, GY, GZ = dims
        n = len(G)
        S = GX * GY
        full = (1 << n) - 1
        cur = inside & (full ^ bytes_to_bits(G))
        if not cur:
            return bytes(n)
        acc = 0
        for _ in range(D):
            cur = cur | (cur << 1) | (cur >> 1)
            cur = cur | (cur << GX) | (cur >> GX)
            cur = (cur | (cur << S) | (cur >> S)) & inside
            acc += int.from_bytes(bits_to_bytes(cur, n), "little")
        return acc.to_bytes(n, "little")

    # -- local fine search between two visible-but-not points --------------
    def local_path(self, a, b, margin: int):
        lo = [max(0, int(min(a[i], b[i])) - margin) for i in range(3)]
        hi = [min((self.X, self.Y, self.Z)[i] - 1, int(max(a[i], b[i])) + margin) for i in range(3)]
        bx, by, bz = hi[0] - lo[0] + 1, hi[1] - lo[1] + 1, hi[2] - lo[2] + 1
        GX, GY, GZ = bx + 2, by + 2, bz + 2
        sub = bytearray(GX * GY * GZ)
        X, Y = self.X, self.Y
        for z in range(bz):
            for y in range(by):
                src = lo[0] + X * ((lo[1] + y) + Y * (lo[2] + z))
                dst = 1 + GX * ((y + 1) + GY * (z + 1))
                sub[dst:dst + bx] = self.reach[src:src + bx]

        def cell(u):
            return (int(u[0]) - lo[0] + 1) + GX * ((int(u[1]) - lo[1] + 1) + GY * (int(u[2]) - lo[2] + 1))
        path, _, _ = astar(sub, (GX, GY, GZ), cell(a), cell(b), turn_cost=0.5, heur_w=1.2,
                           max_expansions=200_000)
        if not path:
            return None
        out = []
        for c in path[1:-1]:
            x, y, z = c % GX, (c // GX) % GY, c // (GX * GY)
            out.append([x - 1 + lo[0] + 0.5, y - 1 + lo[1] + 0.5, z - 1 + lo[2] + 0.5])
        return out

    # -- string pulling ------------------------------------------------------
    def string_pull(self, W, f):
        out = [W[0]]
        i = 0
        repairs = 0
        while i < len(W) - 1:
            if not self.seg_free(W[i], W[i + 1]):
                repairs += 1
                if repairs > 200:
                    return None, "string pulling: too many local repairs"
                mid = self.local_path(W[i], W[i + 1], 2 * f + 4)
                if mid is None:
                    mid = self.local_path(W[i], W[i + 1], 6 * f + 12)
                if not mid:
                    return None, f"string pulling: no fine path near {self._mm(W[i])} mm"
                W = W[:i + 1] + mid + W[i + 1:]
                if not self.seg_free(W[i], W[i + 1]):
                    return None, f"string pulling: blocked step near {self._mm(W[i])} mm"
            n = len(W) - 1
            good = i + 1
            probe = i + 2
            bad = None
            while probe <= n:
                if self.seg_free(W[i], W[probe]):
                    good = probe
                    probe = i + 2 * (probe - i)
                else:
                    bad = probe
                    break
            if bad is None and good < n:
                if self.seg_free(W[i], W[n]):
                    good = n
                else:
                    bad = n
            if bad is not None:
                while bad - good > 1:
                    mid_i = (good + bad) // 2
                    if self.seg_free(W[i], W[mid_i]):
                        good = mid_i
                    else:
                        bad = mid_i
            out.append(W[good])
            i = good
        return out, ""

    def _mm(self, u):
        return [round(v * 1000.0, 1) for v in self.to_w(u)]


class _Path:
    """A polyline [S, Ls, ..., Lg, G] in voxel units with the two lead rays."""

    def __init__(self, pts, ds, de, smin_s, smin_e):
        self.p = pts
        self.ds, self.de = ds, de
        self.smin_s, self.smin_e = smin_s, smin_e


def _out_dir(P, c):
    u1 = _unit(_sub(P[c], P[c - 1]))
    u2 = _unit(_sub(P[c + 1], P[c]))
    return _unit(_sub(u1, u2))


class _Repair:
    def __init__(self, pl: _Planner, path: _Path, min_corner_deg: float):
        self.pl = pl
        self.path = path
        self.min_corner = math.radians(min_corner_deg)
        self.rb = pl.rb

    def moved(self, P, c, delta):
        """Vertex c moved by delta (voxels): along the ray for a lead point, else outward."""
        n = len(P)
        if c == 1:
            s = _dot(_sub(P[1], P[0]), self.path.ds) + delta
            if s < self.path.smin_s:
                return None
            return _add(P[0], _mul(self.path.ds, s))
        if c == n - 2:
            s = _dot(_sub(P[-1], P[-2]), self.path.de) + delta
            if s < self.path.smin_e:
                return None
            return _sub(P[-1], _mul(self.path.de, s))
        o = _out_dir(P, c)
        if _norm(o) < 1e-9:
            return None
        return _add(P[c], _mul(o, abs(delta)))

    def legs_ok(self, P, idx):
        """Segments touching the vertices idx are free."""
        segs = set()
        for c in idx:
            if c > 0:
                segs.add(c - 1)
            if c < len(P) - 1:
                segs.add(c)
        return all(self.pl.seg_free(P[k], P[k + 1]) for k in sorted(segs))

    def drop_small(self, P):
        changed = True
        while changed and len(P) > 4:
            changed = False
            tans = corner_tangents(P, self.rb)
            for c in range(2, len(P) - 2):
                if tans[c - 1][0] < self.min_corner and self.pl.seg_free(P[c - 1], P[c + 1]):
                    P = P[:c] + P[c + 1:]
                    changed = True
                    break
        return P

    def fix_leg(self, P, k, need, length):
        n = len(P)
        lead_s, lead_e = 1, n - 2
        deficit = need - length
        # The first / last leg: slide the lead point outward along its ray.
        if k == 0 or k == n - 2:
            c = 1 if k == 0 else n - 2
            for extra in (deficit * 1.05 + 0.25, deficit * 1.5 + 1.0, deficit * 2.5 + 2.0):
                q = list(P)
                v = self.moved(P, c, extra)
                if v is None:
                    continue
                q[c] = v
                if self.legs_ok(q, [c]):
                    return q
            return None
        a, b = k, k + 1
        # Drop a corner whose neighbours see each other.
        for c in (a, b):
            if c not in (lead_s, lead_e) and self.pl.seg_free(P[c - 1], P[c + 1]):
                return P[:c] + P[c + 1:]
        # Merge a and b into one corner on the line of the leg before / after.
        if not (a == lead_s and b == lead_e):
            m = _closest_on_lines(P[a - 1], P[a], P[b], P[b + 1], keep_first=(a == lead_s),
                                  keep_second=(b == lead_e))
            if m is not None:
                q = P[:a] + [m] + P[b + 1:]
                if (a != lead_s or _dot(_sub(m, P[0]), self.path.ds) >= self.path.smin_s) and \
                        (b != lead_e or _dot(_sub(P[-1], m), self.path.de) >= self.path.smin_e):
                    if self.pl.point_free(m) and self.legs_ok(q, [a]):
                        return q
        # Push the two corners apart (outward along their bisectors / along the lead rays).
        deltas = [0.5, 1, 1.5, 2, 3, 4, 6, 8, 11, 15, 20, 27, 35]
        lim = 2.0 * self.rb + 4.0
        signs_a = (1.0, -1.0) if a == lead_s else (1.0,)
        signs_b = (1.0, -1.0) if b == lead_e else (1.0,)
        for d in deltas:
            if d > lim:
                break
            for sa in signs_a:
                for sb in signs_b:
                    q = list(P)
                    va = self.moved(P, a, sa * d)
                    vb = self.moved(P, b, sb * d)
                    if va is None or vb is None:
                        continue
                    q[a], q[b] = va, vb
                    chk = fillet_check(q[a - 1:b + 2], self.rb)
                    if chk["legs"][1]["slack"] >= 0 and self.legs_ok(q, [a, b]):
                        return q
        return None

    def fix_arc(self, P, c):
        n = len(P)
        th = corner_tangents(P[c - 1:c + 2], self.rb)[0][0]
        lim = self.rb * (1.0 / max(math.cos(th / 2.0), 0.05) - 1.0) * 1.5 + 4.0
        deltas = [0.5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 13, 16, 20, 25, 32, 40]
        signs = (1.0, -1.0) if c in (1, n - 2) else (1.0,)
        for d in deltas:
            if d > lim:
                break
            for sg in signs:
                v = self.moved(P, c, sg * d)
                if v is None:
                    continue
                q = list(P)
                q[c] = v
                chk = fillet_check(q[max(0, c - 2):min(n, c + 3)], self.rb)
                if chk["ok"] and self.legs_ok(q, [c]) and self.pl.arc_free(q, c):
                    return q
        if c not in (1, n - 2) and self.pl.seg_free(P[c - 1], P[c + 1]):
            return P[:c] + P[c + 1:]
        return None

    def run(self, P):
        for _ in range(80):
            P = self.drop_small(P)
            chk = fillet_check(P, self.rb)
            k = chk["worst_leg"]
            leg = chk["legs"][k]
            if leg["slack"] < -1e-6:
                q = self.fix_leg(P, k, leg["need"], leg["length"])
                if q is None:
                    where = self.pl._mm(P[k])
                    return None, (f"bend radius does not fit: leg {k} near {where} mm is "
                                  f"{leg['length'] * self.pl.res * 1000:.1f} mm, the corners need "
                                  f"{min(leg['need'], 1e9) * self.pl.res * 1000:.1f} mm")
                P = q
                continue
            bad = next((c for c in range(1, len(P) - 1) if not self.pl.arc_free(P, c)), None)
            if bad is None:
                return P, ""
            q = self.fix_arc(P, bad)
            if q is None:
                return None, f"fillet arc at corner {bad} near {self.pl._mm(P[bad])} mm hits an obstacle"
            P = q
        return None, "fillet repair did not converge"


def _closest_on_lines(a0, a1, b0, b1, keep_first=False, keep_second=False):
    """Closest points of line a0-a1 and line b0-b1: the one on a (keep_first), on b
    (keep_second), else their midpoint. None for parallel lines."""
    u = _sub(a1, a0)
    v = _sub(b1, b0)
    w = _sub(a0, b0)
    a, b, c = _dot(u, u), _dot(u, v), _dot(v, v)
    d, e = _dot(u, w), _dot(v, w)
    den = a * c - b * b
    if den < 1e-9 * max(a * c, 1e-12):
        return None
    s = (b * e - c * d) / den
    t = (a * e - b * d) / den
    pa = _add(a0, _mul(u, s))
    pb = _add(b0, _mul(v, t))
    if keep_first:
        return pa
    if keep_second:
        return pb
    return _mul(_add(pa, pb), 0.5)


def _clearance(pl: _Planner, samples, offsets, cap):
    """(min distance in voxels to an occupied voxel centre, at sample) over samples."""
    occ, X, Y, Z = pl.occ, pl.X, pl.Y, pl.Z
    best, where = cap, None
    for u in samples:
        jx, jy, jz = int(u[0]), int(u[1]), int(u[2])
        for dist, dx, dy, dz in offsets:
            if dist - 0.87 >= best:
                break
            x, y, z = jx + dx, jy + dy, jz + dz
            if 0 <= x < X and 0 <= y < Y and 0 <= z < Z and occ[x + X * (y + Y * z)]:
                ex, ey, ez = x + 0.5 - u[0], y + 0.5 - u[1], z + 0.5 - u[2]
                dd = math.sqrt(ex * ex + ey * ey + ez * ez)
                if dd < best:
                    best, where = dd, u
    return best, where


def _resample(pts, n=40):
    """n points evenly spaced along a polyline (for duplicate detection)."""
    seg = [_dist(pts[k], pts[k + 1]) for k in range(len(pts) - 1)]
    total = sum(seg) or 1.0
    out, k, acc = [], 0, 0.0
    for i in range(n):
        s = total * i / (n - 1)
        while k < len(seg) - 1 and acc + seg[k] < s:
            acc += seg[k]
            k += 1
        t = 0.0 if seg[k] == 0 else min(1.0, (s - acc) / seg[k])
        out.append(_add(pts[k], _mul(_sub(pts[k + 1], pts[k]), t)))
    return out


_ATTEMPTS = [
    {"turn": 1.0, "clear": 0.6, "div": 0.0},
    {"turn": 1.0, "clear": 0.6, "div": 2.0},
    {"turn": 3.0, "clear": 1.2, "div": 2.0},
    {"turn": 0.4, "clear": 0.3, "div": 3.0},
    {"turn": 2.0, "clear": 2.0, "div": 4.0},
    {"turn": 1.0, "clear": 0.6, "div": 6.0},
]


def plan_corridor(grid: dict, start, start_dir, end, end_dir, diameter: float,
                  bend_radius: float, clearance: float = 0.001, lead: float | None = None,
                  max_candidates: int = 3, time_budget_s: float = 60.0,
                  coarse_cells: int = 60_000, min_corner_deg: float = 10.0) -> dict:
    """Up to max_candidates feasible filleted polylines start -> end through the grid.
    Points / lengths in meters (the grid's frame). Returns {candidates, warnings, rejected, stats}."""
    t_all = time.perf_counter()
    deadline = t_all + time_budget_s
    warnings, rejected = [], []
    if diameter <= 0 or bend_radius < 0:
        raise CorridorError("diameter must be > 0 and bend_radius >= 0")
    ds, de = _unit([float(v) for v in start_dir]), _unit([float(v) for v in end_dir])
    if _norm(ds) == 0 or _norm(de) == 0:
        raise CorridorError("start_dir / end_dir must be non-zero")
    start = [float(v) for v in start]
    end = [float(v) for v in end]
    if _dist(start, end) < 1e-6:
        raise CorridorError("start and end must be distinct")
    lead = bend_radius if lead is None else lead
    pl = _Planner(grid, diameter, bend_radius, clearance, coarse_cells)
    stats = {"dims": list(pl.nreal), "voxels": pl.nreal[0] * pl.nreal[1] * pl.nreal[2],
             "occupied_count": pl.occupied_count, "resolution_mm": pl.res * 1000.0,
             "inflation_mm": round(pl.R * 1000.0, 3)}
    result = {"candidates": [], "warnings": warnings, "rejected": rejected, "stats": stats}
    Su, Gu = pl.to_u(start), pl.to_u(end)
    for name, u in (("start", Su), ("end", Gu)):
        if not pl.in_region(u):
            warnings.append(f"{name} point is outside the grid region: enlarge the region")
    if warnings:
        return result
    pl.inflate()
    lead_v = max(lead / pl.res, 0.0)
    cap_v = lead_v + 3.0 * pl.rb + 4.0 * pl.rv + 4.0
    ws = pl.walk_ray(Su, ds, lead_v, cap_v, pl.free)
    if ws is None:
        warnings.append(f"start blocked: no free voxel along start_dir within "
                        f"{cap_v * pl.res * 1000:.0f} mm of the start (inflated by "
                        f"{pl.R * 1000:.1f} mm): ignore the start connector, or check start_dir")
        return result
    free_from_s, s_lead = ws
    Ls = _add(Su, _mul(ds, s_lead))
    if not pl.in_region(Ls):
        warnings.append("start lead point leaves the region: enlarge the region")
        return result
    if s_lead > lead_v + 1.01:
        warnings.append(f"start lead extended to {s_lead * pl.res * 1000:.1f} mm (blocked before)")
    pl.flood(pl.vidx(Ls))
    we = pl.walk_ray(Gu, _mul(de, -1.0), lead_v, cap_v, pl.reach)
    if we is None:
        wf = pl.walk_ray(Gu, _mul(de, -1.0), lead_v, cap_v, pl.free)
        if wf is None:
            warnings.append(f"end blocked: no free voxel against end_dir within "
                            f"{cap_v * pl.res * 1000:.0f} mm of the end: ignore the end "
                            f"connector, or check end_dir (the direction of ARRIVAL)")
        else:
            warnings.append("end not reachable from the start: the free space around the end is "
                            "enclosed (inside a closed solid) or every passage is narrower than "
                            "the bundle + clearance in this region")
        stats["elapsed_s"] = round(time.perf_counter() - t_all, 3)
        stats.update(pl.timing)
        return result
    free_from_e, e_lead = we
    Lg = _sub(Gu, _mul(de, e_lead))
    if e_lead > lead_v + 1.01:
        warnings.append(f"end lead extended to {e_lead * pl.res * 1000:.1f} mm (blocked before)")
    pl.zones = [(Su, max(free_from_s, 0.0) ** 2), (Gu, max(free_from_e, 0.0) ** 2)]
    path = _Path(None, ds, de, max(free_from_s + 0.5, 0.5), max(free_from_e + 0.5, 0.5))

    # Clearance offsets (voxel centres around a sample, nearest first).
    kc = int(math.ceil(pl.rv)) + 8
    offsets = sorted((math.sqrt(dx * dx + dy * dy + dz * dz), dx, dy, dz)
                     for dz in range(-kc, kc + 1) for dy in range(-kc, kc + 1)
                     for dx in range(-kc, kc + 1) if dx * dx + dy * dy + dz * dz <= kc * kc)

    t_search = time.perf_counter()
    f = pl.f0
    level = None
    expansions = 0
    found = []
    attempts = _ATTEMPTS[:max(3, 2 * max_candidates)]
    ai = 0
    while ai < len(attempts) and len(found) < max_candidates:
        if time.perf_counter() > deadline:
            warnings.append("time budget exhausted")
            break
        if level is None or level["f"] != f:
            G, dims, to_cell, centre, c_inside = pl.coarse(f, [Ls, Lg])
            cell_v = float(f)
            D = max(1, min(12, int(round(max(pl.R / pl.res, 0.3 * pl.rb) / cell_v))))
            level = {"f": f, "G": G, "dims": dims, "to_cell": to_cell, "centre": centre,
                     "cc": pl.clearance_cost(G, dims, D, c_inside), "D": D,
                     "pen": bytearray(len(G)), "cell": cell_v}
        att = attempts[ai]
        lv = level
        sc, gc = lv["to_cell"](Ls), lv["to_cell"](Lg)
        ctab = [att["clear"] * min(v, lv["D"]) / lv["D"] for v in range(256)]
        cells, exp, why = astar(lv["G"], lv["dims"], sc, gc, cost=lv["cc"], cost_tab=ctab,
                                pen=lv["pen"], pen_w=att["div"],
                                turn_cost=att["turn"] * max(pl.rb, 1.0) / lv["cell"],
                                start_move=nearest_move(ds), heur_w=1.15, deadline=deadline)
        expansions += exp
        if cells is None:
            if ai == 0 and f > 1 and "time" not in why:
                f = max(1, f // 2 if f > 3 else f - 1)
                warnings.append(f"coarse search ({lv['f']} voxel cells) failed: {why}; retrying "
                                f"with {f} voxel cells")
                continue
            rejected.append({"attempt": ai, "reason": f"search: {why}"})
            ai += 1
            if ai == 1:
                break
            continue
        W = [Ls] + [lv["centre"](c) for c in cells[1:-1]] + [Lg]
        pulled, why = pl.string_pull(W, f)
        if pulled is None:
            rejected.append({"attempt": ai, "reason": why})
            ai += 1
            continue
        P = [Su] + pulled + [Gu]
        P, why = _Repair(pl, path, min_corner_deg).run(P)
        if P is None:
            rejected.append({"attempt": ai, "reason": why})
            _mark(lv, pl, W, max(1, int(round(0.5 * pl.rb / lv["cell"]))))
            ai += 1
            continue
        # Straight-through leads (deflection < 0.5 deg) are not corners.
        P = _drop_straight(P)
        cand = _evaluate(pl, P, offsets, kc, bend_radius)
        if not cand["ok"]:
            rejected.append({"attempt": ai, "reason": cand["reason"]})
        elif any(_same(cand, c, 2.0 * lv["cell"]) for c in found):
            rejected.append({"attempt": ai, "reason": "duplicate of an earlier candidate"})
        else:
            cand["attempt"] = ai
            found.append(cand)
        _mark(lv, pl, cand["samples_u"], max(1, int(round(0.5 * pl.rb / lv["cell"]))))
        ai += 1

    stats["coarse_voxels_per_cell"] = level["f"] if level else None
    stats["expansions"] = expansions
    stats["search_s"] = round(time.perf_counter() - t_search, 3)
    stats.update(pl.timing)
    stats["elapsed_s"] = round(time.perf_counter() - t_all, 3)
    found.sort(key=lambda c: c["length_mm"] + c["corners"] * bend_radius * 1000.0)
    for c in found:
        c.pop("ok", None)
        c.pop("samples_u", None)
        c.pop("_resampled", None)
    result["candidates"] = found[:max_candidates]
    if not found and not any("blocked" in w or "reachable" in w for w in warnings):
        warnings.append("no feasible candidate: see rejected (a larger region, a smaller bend "
                        "radius or ignoring a component may open a corridor)")
    return result


def _drop_straight(P):
    q = list(P)
    k = 1
    while k < len(q) - 1:
        th = corner_tangents(q[k - 1:k + 2], 0.0)[0][0]
        if th < math.radians(0.5):
            q.pop(k)
        else:
            k += 1
    return q


def _mark(lv, pl, pts, rr):
    GX, GY, GZ = lv["dims"]
    pen = lv["pen"]
    to_cell = lv["to_cell"]
    seen = set()
    for u in pts:
        c = to_cell(u)
        if c in seen:
            continue
        seen.add(c)
        x0, y0, z0 = c % GX, (c // GX) % GY, c // (GX * GY)
        for z in range(max(1, z0 - rr), min(GZ - 1, z0 + rr + 1)):
            for y in range(max(1, y0 - rr), min(GY - 1, y0 + rr + 1)):
                b = GX * (y + GY * z)
                for x in range(max(1, x0 - rr), min(GX - 1, x0 + rr + 1)):
                    pen[b + x] = 1


def _same(a, b, tol_v) -> bool:
    ra, rb = a["_resampled"], b["_resampled"]
    return max(_dist(p, q) for p, q in zip(ra, rb)) < tol_v


def _evaluate(pl: _Planner, P, offsets, kc, bend_radius) -> dict:
    samples, length = filleted_samples(P, pl.rb, STEP)
    body, leads = [], []
    for u, part in samples:
        if pl.zones and pl._in_zone(u[0], u[1], u[2]):
            continue
        if not pl.point_free(u):
            return {"ok": False, "reason": f"verification: filleted curve leaves the free space "
                                           f"near {pl._mm(u)} mm", "samples_u": [s for s, _ in samples]}
        (body if part == "body" else leads).append(u)
    chk = fillet_check(P, pl.rb)
    res_mm = pl.res * 1000.0
    rad_mm = pl.radius * 1000.0
    out = {"ok": True,
           "points": [[round(v, 6) for v in pl.to_w(u)] for u in P],
           "bend_radii": [bend_radius] * (len(P) - 2),
           "length_mm": round(length * res_mm, 1),
           "corners": len(P) - 2,
           "deflections_deg": [round(c["deflection_deg"], 1) for c in chk["corners"]],
           "min_straight_mm": round(min(leg["slack"] for leg in chk["legs"]) * res_mm, 1),
           "samples_u": [s for s, _ in samples],
           "_resampled": _resample(P)}
    cap = float(kc)
    clear_info = {}
    for key, pts in (("min_clearance_mm", body), ("lead_clearance_mm", leads)):
        if not pts:
            continue
        best, where = _clearance(pl, pts, offsets, cap)
        val = round((best - 0.5) * res_mm - rad_mm, 1)
        out[key] = val
        clear_info[key] = (best, where)
        if best >= cap:
            out[key + "_at_least"] = True
        elif key == "min_clearance_mm" and where is not None:
            out["min_clearance_at"] = [round(v, 4) for v in pl.to_w(where)]
    if "min_clearance_mm" not in out and "lead_clearance_mm" in clear_info:
        best, where = clear_info["lead_clearance_mm"]
        out["min_clearance_mm"] = out["lead_clearance_mm"]
        if best >= cap:
            out["min_clearance_mm_at_least"] = True
        elif where is not None:
            out["min_clearance_at"] = [round(v, 4) for v in pl.to_w(where)]
    return out
