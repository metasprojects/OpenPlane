// Live-preview geometry. This mirrors OpenPlane.Core.Builder (DesignGenerator, FuselageLoft, ShapeMesh) exactly, so what
// the builder draws is what gets saved; the saved file is always made by the C# code (tests compare the two).
// Unity axes: x right, y up, z forward. Pure functions: no three.js, so node can run it too.

export const CENTRE_TOL = 0.01;
// Fixed shapes kept from other planes (meshes, exact panels) get a twin unless exactly centred.
export const FIXED_TOL = 1e-4;
export const POINT_SIZE = 1e-3;
const DEG = Math.PI / 180;

const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const lerp = (a, b, t) => add(a, mul(sub(b, a), t));
const len = a => Math.hypot(a[0], a[1], a[2]);
const norm = a => { const l = len(a); return l > 1e-12 ? mul(a, 1 / l) : [0, 0, 0]; };
const dist = (a, b) => len(sub(a, b));
export const mirrorPoint = p => [-p[0], p[1], p[2]];
const clamp = (v, lo, hi) => (Number.isNaN(v) ? lo : Math.max(lo, Math.min(hi, v)));
export const vec = { add, sub, mul, lerp, len, norm };

// ---------------------------------------------------------------- rotations (Unity's quaternion maths)

/** Rotation of `deg` degrees about a unit axis, as Core's Quat.AngleAxis: [x, y, z, w]. */
export function angleAxis(deg, axis) {
  const n = norm(axis), h = deg * Math.PI / 360, s = Math.sin(h);
  return [n[0] * s, n[1] * s, n[2] * s, Math.cos(h)];
}

/** Hamilton product a * b (apply b, then a). */
export function quatMul(a, b) {
  return [
    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
  ];
}

/** q * v, as Core's Quat operator. */
export function quatRotate(q, v) {
  const tx = 2 * (q[1] * v[2] - q[2] * v[1]);
  const ty = 2 * (q[2] * v[0] - q[0] * v[2]);
  const tz = 2 * (q[0] * v[1] - q[1] * v[0]);
  return [
    v[0] + q[3] * tx + (q[1] * tz - q[2] * ty),
    v[1] + q[3] * ty + (q[2] * tx - q[0] * tz),
    v[2] + q[3] * tz + (q[0] * ty - q[1] * tx),
  ];
}

export const quatConj = q => [-q[0], -q[1], -q[2], q[3]];

export const isTurned = deg => !!deg && deg.length >= 3 && (+deg[0] !== 0 || +deg[1] !== 0 || +deg[2] !== 0);

/** Unity's Euler rule (degrees [x, y, z]): about z first, then x, then y. q = qy * qx * qz. */
export function eulerToQuat(deg) {
  if (!isTurned(deg)) return [0, 0, 0, 1];
  const qx = angleAxis(+deg[0] || 0, [1, 0, 0]), qy = angleAxis(+deg[1] || 0, [0, 1, 0]), qz = angleAxis(+deg[2] || 0, [0, 0, 1]);
  return quatMul(quatMul(qy, qx), qz);
}

/** The Euler degrees [x, y, z] (Unity order) of a rotation; x in [-90, 90]. */
export function quatToEuler(q) {
  const [x, y, z, w] = q;
  const m11 = 1 - 2 * (y * y + z * z), m13 = 2 * (x * z + w * y);
  const m21 = 2 * (x * y + w * z), m22 = 1 - 2 * (x * x + z * z), m23 = 2 * (y * z - w * x);
  const m31 = 2 * (x * z - w * y), m33 = 1 - 2 * (x * x + y * y);
  const ex = Math.asin(-clamp(m23, -1, 1));
  let ey, ez;
  if (Math.abs(m23) < 0.9999999) { ey = Math.atan2(m13, m33); ez = Math.atan2(m21, m22); }
  else { ey = Math.atan2(-m31, m11); ez = 0; }
  const d = v => { const r = v / DEG; return Math.abs(r) < 1e-9 ? 0 : r; };
  return [d(ex), d(ey), d(ez)];
}

/** Column-major 4x4 (like THREE.Matrix4.elements) of translate(t) * rotate(q) * scale(s). */
function trs(t, q, s) {
  const [x, y, z, w] = q;
  const r = [
    1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y),
    2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
    2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y),
  ];
  return [r[0] * s[0], r[1] * s[0], r[2] * s[0], 0, r[3] * s[1], r[4] * s[1], r[5] * s[1], 0, r[6] * s[2], r[7] * s[2], r[8] * s[2], 0, t[0], t[1], t[2], 1];
}

// ---------------------------------------------------------------- wings

/** Leading/trailing edges and lift direction of a wing item (as DesignGenerator.Planform). */
export function planform(w) {
  const span = Math.max(0.05, +w.span || 0), rc = Math.max(0.05, +w.root_chord || 0), tc = Math.max(0.02, +w.tip_chord || 0);
  const theta = (w.vertical ? 90 - (+w.dihedral_deg || 0) : (+w.dihedral_deg || 0)) * DEG;
  const spanDir = [Math.cos(theta), Math.sin(theta), 0];
  const u0 = [-Math.sin(theta), Math.cos(theta), 0];
  const c0 = [0, 0, -1];
  const inc = (+w.incidence_deg || 0) * DEG;
  const chordDir = sub(mul(c0, Math.cos(inc)), mul(u0, Math.sin(inc)));
  const up = norm(add(mul(u0, Math.cos(inc)), mul(c0, Math.sin(inc))));
  const rl = (w.root || [0, 0, 0]).slice(0, 3).map(Number);
  const tl = add(add(rl, mul(spanDir, span)), [0, 0, -span * Math.tan(clamp(+w.sweep_deg || 0, -80, 80) * DEG)]);
  let tipChordDir = chordDir;
  const twist = +w.twist_deg || 0;
  if (twist !== 0) {
    const it = ((+w.incidence_deg || 0) + clamp(twist, -30, 30)) * DEG;
    tipChordDir = sub(mul(c0, Math.cos(it)), mul(u0, Math.sin(it)));
  }
  return { rl, rt: add(rl, mul(chordDir, rc)), tl, tt: add(tl, mul(tipChordDir, tc)), up };
}

/** Roll and yaw reverse on the other side of the plane; pitch, flaps and brakes don't. */
export const mirrorsGain = axis => axis === 'roll' || axis === 'yaw';

/** Role, travel and inputs of a surface type (as DesignGenerator.ApplyRole). */
export function applyRole(s, type, midX, up) {
  const rollSign = midX >= 0 ? -1 : 1;
  const yawSign = up[0] > 1e-6 ? -1 : 1;
  s.inputs = [];
  switch (type) {
    case 'aileron': s.role = 'aileron'; s.min_deg = -20; s.max_deg = 20; s.inputs.push({ axis: 'roll', gain: 20 * rollSign }); break;
    case 'elevator': s.role = 'elevator'; s.min_deg = -20; s.max_deg = 20; s.inputs.push({ axis: 'pitch', gain: 20 }); break;
    case 'rudder': s.role = 'rudder'; s.min_deg = -20; s.max_deg = 20; s.inputs.push({ axis: 'yaw', gain: 20 * yawSign }); break;
    case 'flap': s.role = 'flap'; s.min_deg = 0; s.max_deg = 30; s.inputs.push({ axis: 'flaps', gain: 30 }); break;
    case 'elevon': s.role = 'elevon'; s.min_deg = -25; s.max_deg = 25; s.inputs.push({ axis: 'pitch', gain: 20 }, { axis: 'roll', gain: 20 * rollSign }); break;
    case 'flaperon': s.role = 'flaperon'; s.min_deg = -20; s.max_deg = 30; s.inputs.push({ axis: 'roll', gain: 20 * rollSign }, { axis: 'flaps', gain: 30 }); break;
    case 'ruddervator': s.role = 'ruddervator'; s.min_deg = -20; s.max_deg = 20; s.inputs.push({ axis: 'pitch', gain: 20 }, { axis: 'yaw', gain: 20 * yawSign }); break;
    case 'airbrake': s.role = 'airbrake'; s.min_deg = 0; s.max_deg = 60; s.inputs.push({ axis: 'brake', gain: 60 }); break;
    case 'slat': s.role = 'slat'; s.min_deg = 0; s.max_deg = 25; s.inputs.push({ axis: 'flaps', gain: 25 }); break;
    default: s.role = 'fixed'; s.min_deg = 0; s.max_deg = 0;
  }
  s.servo_deg_per_sec = type === 'flap' || type === 'slat' ? 20 : type === 'airbrake' ? 45 : 90;
}

/** applyRole, then the surface spec's travel, servo and input overrides (gains given for the +x side). */
function applySurface(d, spec, midX, up) {
  applyRole(d, spec.type, midX, up);
  // Its palette material (null: the wing's), for the preview's colours.
  if (spec.material_id) d.material_id = spec.material_id;
  if (spec.min_deg != null) d.min_deg = +spec.min_deg;
  if (spec.max_deg != null) d.max_deg = +spec.max_deg;
  if (spec.servo_deg_per_sec != null) d.servo_deg_per_sec = Math.max(1, +spec.servo_deg_per_sec);
  if (Array.isArray(spec.inputs))
    d.inputs = spec.inputs.filter(i => i && i.axis && String(i.axis).trim()).map(i => ({ axis: i.axis, gain: midX < 0 && mirrorsGain(i.axis) ? -(+i.gain || 0) : (+i.gain || 0) }));
}

/** Slats always sit on the leading edge; other surfaces where their edge says. */
export const isLeadingSurface = s => s.type === 'slat' || s.edge === 'leading';

function strips(w, list, notes) {
  let segs = [];
  for (const s of list) {
    const a = clamp(Math.min(+s.from_pct, +s.to_pct), 0, 100) / 100, b = clamp(Math.max(+s.from_pct, +s.to_pct), 0, 100) / 100;
    const c = clamp(+s.chord_pct, 5, 60) / 100;
    if (b - a < 0.005) continue;
    segs.push({ spec: s, s0: a, s1: b, c });
  }
  segs = segs.map((s, i) => ({ ...s, i })).sort((x, y) => x.s0 - y.s0 || x.i - y.i);
  // Same rule as the server (DesignGenerator.Strips): trim against the furthest end reached so far, not just the previous
  // strip, so the view never shows a surface the saved plane leaves out.
  let furthest = 0;
  for (let i = 1; i < segs.length; i++) {
    const end = segs[furthest].s1;
    if (segs[i].s0 < end - 1e-9) {
      const inside = segs[i].s1 <= end + 1e-9;
      notes.push({ level: 'note', item_id: w.id, message: inside
        ? `Two control surfaces overlap on "${w.name}"; the ${segs[i].spec.type} lies inside the ${segs[furthest].spec.type}, so it was left out.`
        : `Two control surfaces overlap on "${w.name}"; the ${segs[i].spec.type} was shortened to start where the ${segs[furthest].spec.type} ends.` });
      segs[i] = { ...segs[i], s0: end, s1: Math.max(end, segs[i].s1) };
    }
    if (segs[i].s1 > segs[furthest].s1) furthest = i;
  }
  return segs.filter(s => s.s1 - s.s0 >= 0.005);
}

function parametricPanel(w, notes) {
  const { rl, rt, tl, tt, up } = planform(w);
  if ((+w.span || 0) < 0.05 || (+w.root_chord || 0) < 0.05)
    notes.push({ level: 'warning', item_id: w.id, message: `"${w.name}" is very small; give it a span and chord of at least a few centimetres.` });
  const P = (s, f) => lerp(lerp(rl, tl, s), lerp(rt, tt, s), f);

  const used = (w.surfaces || []).filter(s => s && s.type !== 'none');
  const segs = strips(w, used.filter(s => !isLeadingSurface(s)), notes);
  let lead = strips(w, used.filter(isLeadingSurface), notes);
  const cmax = segs.length ? Math.max(...segs.map(s => s.c)) : 0;
  let lmax = lead.length ? Math.max(...lead.map(s => s.c)) : 0;
  if (lmax + cmax > 0.9) {
    const f = (0.9 - cmax) / lmax;
    lead = lead.map(s => ({ ...s, c: s.c * f }));
    lmax = 0.9 - cmax;
    notes.push({ level: 'note', item_id: w.id, message: `The leading-edge strips on "${w.name}" were made narrower so the surfaces leave a tenth of the chord fixed.` });
  }
  const fp = 1 - cmax;
  const panel = {
    name: w.name, root_leading: lmax > 0 ? P(0, lmax) : rl, root_trailing: P(0, fp), tip_leading: lmax > 0 ? P(1, lmax) : tl, tip_trailing: P(1, fp), up,
    tip_twist_deg: +w.twist_deg || 0, thickness_ratio: clamp(+w.thickness_ratio || 0.12, 0.02, 0.3), surfaces: [],
  };
  const midX = (rl[0] + tl[0]) / 2;
  const strip = (s0, s1, f0, f1, edge = 'trailing') => ({
    role: 'fixed', edge, root_leading: P(s0, f0), root_trailing: P(s0, f1), tip_leading: P(s1, f0), tip_trailing: P(s1, f1), inputs: [], min_deg: 0, max_deg: 0,
  });
  let covered = 0;
  for (const s of segs) {
    if (cmax > 0 && s.s0 > covered + 1e-6) panel.surfaces.push(strip(covered, s.s0, fp, 1));
    if (s.c < cmax - 1e-9) panel.surfaces.push(strip(s.s0, s.s1, fp, 1 - s.c));
    const surf = strip(s.s0, s.s1, 1 - s.c, 1);
    applySurface(surf, s.spec, midX, up);
    panel.surfaces.push(surf);
    covered = s.s1;
  }
  if (cmax > 0 && covered < 1 - 1e-6) panel.surfaces.push(strip(covered, 1, fp, 1));
  covered = 0;
  for (const s of lead) {
    if (s.s0 > covered + 1e-6) panel.surfaces.push(strip(covered, s.s0, 0, lmax, 'leading'));
    if (s.c < lmax - 1e-9) panel.surfaces.push(strip(s.s0, s.s1, s.c, lmax, 'leading'));
    const surf = strip(s.s0, s.s1, 0, s.c, 'leading');
    applySurface(surf, s.spec, midX, up);
    panel.surfaces.push(surf);
    covered = s.s1;
  }
  if (lmax > 0 && covered < 1 - 1e-6) panel.surfaces.push(strip(covered, 1, 0, lmax, 'leading'));
  // An all-moving panel (as DesignGenerator.ParametricPanel): the whole panel is the control.
  if (w.moving && w.moving.type && w.moving.type !== 'none') {
    const whole = {};
    applySurface(whole, w.moving, midX, up);
    panel.inputs = whole.inputs; panel.min_deg = whole.min_deg; panel.max_deg = whole.max_deg; panel.servo_deg_per_sec = whole.servo_deg_per_sec;
    if (!panel.inputs.length) notes.push({ level: 'note', item_id: w.id, message: `"${w.name}" is set to move as a whole but has no inputs, so it stays still.` });
  }
  return panel;
}

const corners = p => [p.root_leading, p.root_trailing, p.tip_leading, p.tip_trailing];

/** Centre used for scaling fixed panels (all panel corners, as the C# code). */
export function panelsPivot(panels) {
  const pts = panels.flatMap(corners);
  if (!pts.length) return [0, 0, 0];
  return mul(pts.reduce(add, [0, 0, 0]), 1 / pts.length);
}

function transformPanels(w) {
  const pivot = panelsPivot(w.panels || []);
  const scale = +w.scale > 0 ? +w.scale : 1;
  const off = w.offset || [0, 0, 0];
  const T = p => add(add(pivot, mul(sub(p, pivot), scale)), off);
  return (w.panels || []).map(p => ({
    ...p,
    root_leading: T(p.root_leading), root_trailing: T(p.root_trailing), tip_leading: T(p.tip_leading), tip_trailing: T(p.tip_trailing),
    surfaces: (p.surfaces || []).map(s => ({ ...s, root_leading: T(s.root_leading), root_trailing: T(s.root_trailing), tip_leading: T(s.tip_leading), tip_trailing: T(s.tip_trailing) })),
  }));
}

export function mirrorPanel(p, name) {
  return {
    ...p, name,
    root_leading: mirrorPoint(p.root_leading), root_trailing: mirrorPoint(p.root_trailing),
    tip_leading: mirrorPoint(p.tip_leading), tip_trailing: mirrorPoint(p.tip_trailing), up: mirrorPoint(p.up),
    // An all-moving panel's roll and yaw reverse on the other side, as its strips' do.
    inputs: (p.inputs || []).map(i => ({ ...i, gain: mirrorsGain(i.axis) ? -i.gain : i.gain })),
    surfaces: (p.surfaces || []).map(s => ({
      ...s,
      root_leading: mirrorPoint(s.root_leading), root_trailing: mirrorPoint(s.root_trailing),
      tip_leading: mirrorPoint(s.tip_leading), tip_trailing: mirrorPoint(s.tip_trailing),
      inputs: (s.inputs || []).map(i => ({ ...i, gain: mirrorsGain(i.axis) ? -i.gain : i.gain })),
    })),
  };
}

/** Panels made by one wing item: right side first, then the mirrored twin (as DesignGenerator.BuildWing). */
export function buildWing(w, notes = []) {
  const right = w.kind === 'panels' ? transformPanels(w) : [parametricPanel(w, notes)];
  const tol = w.kind === 'panels' ? FIXED_TOL : CENTRE_TOL;
  const twin = w.mirror && right.flatMap(corners).some(p => Math.abs(p[0]) > tol);
  if (twin && w.kind !== 'panels') right.forEach(p => { p.name = `${w.name} (right)`; });
  const result = [...right];
  if (twin) for (const p of right) result.push(mirrorPanel(p, w.kind !== 'panels' ? `${w.name} (left)` : `${p.name} (left)`));
  return result;
}

/** Area of one quad (half the cross product of its diagonals). */
export function quadArea(rl, rt, tt, tl) {
  const d1 = sub(tt, rl), d2 = sub(tl, rt);
  const c = [d1[1] * d2[2] - d1[2] * d2[1], d1[2] * d2[0] - d1[0] * d2[2], d1[0] * d2[1] - d1[1] * d2[0]];
  return len(c) / 2;
}

// ---------------------------------------------------------------- fuselage loft

export function exponent(roundness) { const r = clamp(+roundness, 0, 1); return 2 / (0.2 + 0.8 * r); }
export function clampSides(n) { n = Math.max(8, Math.min(64, Math.round(+n || 24))); return n % 2 === 0 ? n : n + 1; }

/** Rings nose first (highest z first), keeping list order for equal stations. */
export function orderedRings(f) {
  return (f.rings || []).map((r, i) => ({ r, i })).sort((a, b) => (b.r.z - a.r.z) || (a.i - b.i)).map(x => x.r);
}

const signedPow = (v, p) => (Math.abs(v) < 1e-12 ? 0 : Math.sign(v) * Math.pow(Math.abs(v), p));

const usablePoints = ring => (Array.isArray(ring?.points) ? ring.points.filter(p => Array.isArray(p) && p.length >= 2) : []);
/** True when a ring has a custom cross-section (three or more points). */
export const hasPoints = ring => usablePoints(ring).length >= 3;

/** The mirror partner of point k of n around a ring (x reflected). */
export const mirrorIndex = (k, n) => (n - k) % n;

/** Points spaced evenly by length along a closed polygon, starting at its first point (as FuselageLoft.Resample). */
export function resampleRing(loop, n) {
  const m = loop.length;
  const result = [];
  if (!m) return Array.from({ length: n }, () => [0, 0, 0]);
  const cum = [0];
  for (let i = 0; i < m; i++) cum.push(cum[i] + dist(loop[i], loop[(i + 1) % m]));
  const total = cum[m];
  if (total < 1e-12) return Array.from({ length: n }, () => [...loop[0]]);
  let seg = 0;
  for (let k = 0; k < n; k++) {
    const d = total * k / n;
    while (seg < m - 1 && cum[seg + 1] < d) seg++;
    const l = cum[seg + 1] - cum[seg];
    const t = l > 1e-12 ? (d - cum[seg]) / l : 0;
    result.push(lerp(loop[seg], loop[(seg + 1) % m], t));
  }
  return result;
}

/** n offsets of a ring's points from its centre (0, y, z): top first, then towards +x (as FuselageLoft.Offsets). */
export function ringOffsets(ring, sides) {
  const n = clampSides(sides);
  const pts = usablePoints(ring);
  if (pts.length >= 3) {
    const src = pts.map(p => [+p[0], +p[1], p.length > 2 ? +p[2] : 0]);
    return src.length === n ? src : resampleRing(src, n);
  }
  const e = 2 / exponent(ring.roundness);
  const a = Math.max(0, +ring.width) / 2, b = Math.max(0, +ring.height) / 2;
  const out = [];
  for (let k = 0; k < n; k++) {
    const t = 2 * Math.PI * k / n;
    out.push([a * signedPow(Math.sin(t), e), b * signedPow(Math.cos(t), e), 0]);
  }
  return out;
}

/** A ring's points in aircraft space with the body's position (no turn), as FuselageLoft.RingPoints. */
export function ringPoints(ring, pos, sides) {
  const cx = pos[0], cy = pos[1] + (+ring.y || 0), cz = pos[2] + (+ring.z || 0);
  return ringOffsets(ring, sides).map(o => [cx + o[0], cy + o[1], cz + o[2]]);
}

/** A ring's points in the fuselage's own space (before its turn and position). */
export const ringLocalPoints = (ring, sides) => ringPoints(ring, [0, 0, 0], sides);

/** True for a ring that is a single tip point. */
export function isPointRing(r) {
  const pts = usablePoints(r);
  if (pts.length >= 3) return pts.every(p => Math.hypot(+p[0], +p[1], p.length > 2 ? +p[2] : 0) < POINT_SIZE);
  return (+r.width) < POINT_SIZE && (+r.height) < POINT_SIZE;
}

function catmullRom(p0, p1, p2, p3, t) {
  const t2 = t * t, t3 = t2 * t;
  const o = [];
  for (let k = 0; k < 3; k++)
    o.push(0.5 * ((2 * p1[k]) + (p2[k] - p0[k]) * t + (2 * p0[k] - 5 * p1[k] + 4 * p2[k] - p3[k]) * t2 + (3 * p1[k] - p0[k] - 3 * p2[k] + p3[k]) * t3));
  return o;
}

function smoothRings(rings, level) {
  const steps = 1 << level, m = rings.length, out = [];
  for (let i = 0; i < m; i++) {
    out.push(rings[i]);
    if (i === m - 1) break;
    const a = rings[i > 0 ? i - 1 : i].pts, b = rings[i].pts, c = rings[i + 1].pts, d = rings[i + 2 < m ? i + 2 : i + 1].pts;
    for (let j = 1; j < steps; j++) {
      const t = j / steps;
      const pts = b.map((_, k) => catmullRom(a[k], b[k], c[k], d[k], t));
      const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
      out.push({ pts, point: Math.max(...xs) - Math.min(...xs) < POINT_SIZE && Math.max(...ys) - Math.min(...ys) < POINT_SIZE });
    }
  }
  return out;
}

/** A loft's rings in aircraft space, nose first: { pts, point } (a tip has one point), as FuselageLoft.PlaneRings. */
export function planeRings(f) {
  const rings = orderedRings(f);
  const n = clampSides(f.sides);
  const pos = (f.position || [0, 0, 0]).map(Number);
  const smooth = Math.max(0, Math.min(3, Math.round(+f.smooth || 0)));
  const turned = isTurned(f.rotation_deg);
  if (!smooth && !turned)
    return rings.map(r => (isPointRing(r) ? { pts: [[pos[0], pos[1] + (+r.y || 0), pos[2] + (+r.z || 0)]], point: true } : { pts: ringPoints(r, pos, n), point: false }));
  let local = rings.map(r => {
    const c = [0, +r.y || 0, +r.z || 0];
    return isPointRing(r) ? { pts: Array.from({ length: n }, () => [...c]), point: true } : { pts: ringLocalPoints(r, n), point: false };
  });
  if (smooth > 0 && local.length >= 2) local = smoothRings(local, smooth);
  const q = eulerToQuat(f.rotation_deg);
  const T = v => (turned ? add(pos, quatRotate(q, v)) : add(v, pos));
  return local.map(({ pts, point }) => {
    if (!point) return { pts: pts.map(T), point: false };
    const c = mul(pts.reduce(add, [0, 0, 0]), 1 / pts.length);
    return { pts: [T(c)], point: true };
  });
}

/** The loft mesh (vertices and triangles in aircraft space), identical to FuselageLoft.Build. */
export function loft(f) {
  if ((f.rings || []).length < 2) return { positions: new Float32Array(0), indices: new Uint32Array(0) };
  const rings = planeRings(f);
  const n = clampSides(f.sides);
  const verts = [], tris = [];
  const start = [], point = [];
  rings.forEach((r, i) => { start[i] = verts.length; point[i] = r.point; verts.push(...r.pts); });
  for (let i = 0; i + 1 < rings.length; i++) {
    const a0 = start[i], b0 = start[i + 1];
    if (point[i] && point[i + 1]) continue;
    for (let k = 0; k < n; k++) {
      const k1 = (k + 1) % n;
      if (point[i]) tris.push(a0, b0 + k1, b0 + k);
      else if (point[i + 1]) tris.push(a0 + k, a0 + k1, b0);
      else tris.push(a0 + k, a0 + k1, b0 + k1, a0 + k, b0 + k1, b0 + k);
    }
  }
  const cap = (ring, front) => {
    const c = mul(ring.reduce(add, [0, 0, 0]), 1 / ring.length);
    const ci = verts.length;
    verts.push(c);
    const s = verts.length;
    verts.push(...ring);
    for (let k = 0; k < ring.length; k++) {
      const k1 = (k + 1) % ring.length;
      if (front) tris.push(ci, s + k1, s + k); else tris.push(s + k, s + k1, ci);
    }
  };
  if (!point[0]) cap(rings[0].pts, true);
  const last = rings.length - 1;
  if (!point[last]) cap(rings[last].pts, false);
  const positions = new Float32Array(verts.length * 3);
  verts.forEach((v, i) => { positions[i * 3] = v[0]; positions[i * 3 + 1] = v[1]; positions[i * 3 + 2] = v[2]; });
  return { positions, indices: Uint32Array.from(tris) };
}

/** Bounds centre of a stored mesh (vertices: flat x,y,z array). */
export function meshCenter(vertices) {
  let min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i + 2 < vertices.length; i += 3)
    for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], vertices[i + k]); max[k] = Math.max(max[k], vertices[i + k]); }
  if (!isFinite(min[0])) return [0, 0, 0];
  return [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2];
}

/**
 * Where a fuselage's own space lands in the plane. Loft: turned about its position. Fixed mesh (pass its source mesh,
 * { vertices: flat array }): scaled and turned about the source's bounds centre c, then moved: p' = c + R(s(p - c)) + position.
 * Returns { toPlane(p), fromPlane(p), matrix (column-major 4x4), quat }.
 */
export function fuselageTransform(f, sourceMesh = null) {
  const q = eulerToQuat(f.rotation_deg);
  const pos = (f.position || [0, 0, 0]).map(Number);
  if (f.kind === 'mesh') {
    const c = meshCenter(sourceMesh?.vertices || []);
    const s = +f.scale > 0 ? +f.scale : 1;
    const t = sub(add(c, pos), quatRotate(q, mul(c, s)));
    return {
      quat: q,
      toPlane: p => add(add(c, quatRotate(q, mul(sub(p, c), s))), pos),
      fromPlane: p => add(c, mul(quatRotate(quatConj(q), sub(sub(p, pos), c)), 1 / s)),
      matrix: trs(t, q, [s, s, s]),
    };
  }
  return {
    quat: q,
    toPlane: p => add(pos, quatRotate(q, p)),
    fromPlane: p => quatRotate(quatConj(q), sub(p, pos)),
    matrix: trs(pos, q, [1, 1, 1]),
  };
}

/** Whether a point part makes a twin on the other side. */
export const hasTwin = (pos, mirror) => !!mirror && Math.abs(pos?.[0] || 0) > CENTRE_TOL;

// ---------------------------------------------------------------- engines

/** Static thrust of a propeller (as DesignGenerator.PropThrustN), in newtons. */
export function propThrustN(powerKw, diameterM) {
  const p = Math.max(0, +powerKw || 0) * 1000;
  if (!(diameterM > 0)) return p / 40;
  return Math.pow(0.75 * p, 2 / 3) * Math.pow(2 * 1.225 * Math.PI * diameterM * diameterM / 4, 1 / 3);
}

/** Dry static thrust (N) of a jet from its diameter and turbine temperature (as DesignGenerator.JetThrustN). */
export function jetThrustN(diameterM, turbineTempK) {
  const r = Math.max(0, +diameterM || 0) / 2;
  return 0.1697 * Math.PI * r * r * Math.pow(Math.max(0, (+turbineTempK || 0) - 300), 1.766);
}

/** True when an engine drives a propeller (its variant wins over its type). */
export const isPropEngine = e => (e.variant ? e.variant === 'piston' || e.variant === 'turboprop' || e.variant === 'electric' : e.type === 'prop');

/** The engine's thrust in newtons as the saved plane will have it: { dry, afterburner }. */
export function engineThrust(e) {
  if (isPropEngine(e)) return { dry: Math.round(propThrustN(e.power_kw, e.prop_diameter_m)), afterburner: 0 };
  if (e.sizing === 'designed') {
    const dry = jetThrustN(e.diameter_m, e.turbine_temp_k ?? 1400);
    return { dry: Math.round(dry), afterburner: e.afterburner ? Math.round(dry * 1.55) : 0 };
  }
  const dry = Math.max(0, +e.thrust_kn || 0) * 1000;
  return { dry, afterburner: (+e.afterburner_kn || 0) > (+e.thrust_kn || 0) ? +e.afterburner_kn * 1000 : 0 };
}

/** True when a stored mesh is its own mirror image across x = 0 (as FuselageLoft.IsSelfMirrored). */
export function isSelfMirrored(vertices) {
  if (vertices.length < 3) return true;
  const g = 500, cells = new Set();
  const k = (x, y, z) => `${x},${y},${z}`;
  for (let i = 0; i + 2 < vertices.length; i += 3) cells.add(k(Math.round(vertices[i] * g), Math.round(vertices[i + 1] * g), Math.round(vertices[i + 2] * g)));
  for (let i = 0; i + 2 < vertices.length; i += 3) {
    const x = Math.round(-vertices[i] * g), y = Math.round(vertices[i + 1] * g), z = Math.round(vertices[i + 2] * g);
    if (!cells.has(k(x, y, z)) && !cells.has(k(x - 1, y, z)) && !cells.has(k(x + 1, y, z))) return false;
  }
  return true;
}

// ---------------------------------------------------------------- shape parts

/**
 * One Catmull-Clark step (as ShapeMesh.CatmullClark). vertices: [[x,y,z]...], faces: [[i,j,k,...]...].
 * Output corners: moved originals (same indices), then one per edge in first-met order, then one per face. Output faces:
 * per face and corner k, [corner k, edge (k,k+1), face, edge (k-1,k)]. Edges without exactly two faces are borders.
 */
export function catmullClark(vertices, faces) {
  const nv = vertices.length, nf = faces.length;
  const key = (a, b) => (a < b ? a * nv + b : b * nv + a);
  const facePts = faces.map(f => {
    let c = [0, 0, 0];
    for (const i of f) c = add(c, vertices[i]);
    return mul(c, 1 / f.length);
  });
  const edgeIndex = new Map();
  const edgeA = [], edgeB = [], edgeFaceCount = [], edgeFaceSum = [];
  faces.forEach((c, f) => {
    for (let k = 0; k < c.length; k++) {
      const a = c[k], b = c[(k + 1) % c.length], kk = key(a, b);
      let e = edgeIndex.get(kk);
      if (e === undefined) { e = edgeA.length; edgeIndex.set(kk, e); edgeA.push(a); edgeB.push(b); edgeFaceCount.push(0); edgeFaceSum.push([0, 0, 0]); }
      edgeFaceCount[e]++; edgeFaceSum[e] = add(edgeFaceSum[e], facePts[f]);
    }
  });
  const ne = edgeA.length;
  const edgePts = edgeA.map((a, e) => (edgeFaceCount[e] === 2
    ? mul(add(add(vertices[a], vertices[edgeB[e]]), edgeFaceSum[e]), 1 / 4)
    : mul(add(vertices[a], vertices[edgeB[e]]), 1 / 2)));
  const faceSum = Array.from({ length: nv }, () => [0, 0, 0]), faceCount = new Array(nv).fill(0);
  faces.forEach((c, f) => { for (const i of c) { faceSum[i] = add(faceSum[i], facePts[f]); faceCount[i]++; } });
  const midSum = Array.from({ length: nv }, () => [0, 0, 0]), edgeCount = new Array(nv).fill(0), border = new Array(nv).fill(null);
  for (let e = 0; e < ne; e++) {
    const a = edgeA[e], b = edgeB[e];
    const mid = mul(add(vertices[a], vertices[b]), 1 / 2);
    midSum[a] = add(midSum[a], mid); midSum[b] = add(midSum[b], mid); edgeCount[a]++; edgeCount[b]++;
    if (edgeFaceCount[e] !== 2) { (border[a] ||= []).push(b); (border[b] ||= []).push(a); }
  }
  const out = [];
  for (let i = 0; i < nv; i++) {
    const p = vertices[i];
    if (faceCount[i] === 0) out.push([...p]);
    else if (!border[i]) {
      const n = edgeCount[i];
      const q = mul(faceSum[i], 1 / faceCount[i]), r = mul(midSum[i], 1 / edgeCount[i]);
      out.push(mul(add(add(q, mul(r, 2)), mul(p, n - 3)), 1 / n));
    } else if (border[i].length === 2) out.push(mul(add(add(vertices[border[i][0]], mul(p, 6)), vertices[border[i][1]]), 1 / 8));
    else out.push([...p]);
  }
  out.push(...edgePts, ...facePts);
  const outF = [];
  faces.forEach((c, f) => {
    const m = c.length;
    for (let k = 0; k < m; k++) {
      const next = edgeIndex.get(key(c[k], c[(k + 1) % m])), prev = edgeIndex.get(key(c[(k + m - 1) % m], c[k]));
      outF.push([c[k], nv + next, nv + ne + f, nv + prev]);
    }
  });
  return { vertices: out, faces: outF };
}

/** Fan triangles from each face's first corner (flat index list), as ShapeMesh.Triangulate. */
export function triangulate(faces) {
  const tris = [];
  for (const f of faces) for (let k = 1; k + 1 < f.length; k++) tris.push(f[0], f[k], f[k + 1]);
  return tris;
}

/** A shape's faces with broken ones (fewer than three corners, missing corners) left out. */
export function shapeFaces(s) {
  const n = (s.vertices || []).length;
  return (s.faces || []).filter(f => Array.isArray(f) && f.length >= 3 && f.every(i => Number.isInteger(i) && i >= 0 && i < n));
}

/**
 * A shape part ready to draw, in its OWN space (place it with shapeTransform): smoothed `subdivision` times, in triangles.
 * { positions: Float32Array, indices: Uint32Array, materials: Uint8Array (1 = glass, per triangle) }.
 */
export function shapeMesh(s) {
  let v = (s.vertices || []).map(p => [+p[0], +p[1], +p[2]]), f = shapeFaces(s);
  const levels = Math.max(0, Math.min(3, Math.round(+s.subdivision || 0)));
  for (let i = 0; i < levels && f.length; i++) ({ vertices: v, faces: f } = catmullClark(v, f));
  const tris = triangulate(f);
  const positions = new Float32Array(v.length * 3);
  v.forEach((p, i) => { positions[i * 3] = p[0]; positions[i * 3 + 1] = p[1]; positions[i * 3 + 2] = p[2]; });
  return { positions, indices: Uint32Array.from(tris), materials: new Uint8Array(tris.length / 3).fill(s.material === 'glass' ? 1 : 0) };
}

/** Where a shape's own space lands in the plane: p' = position + R(scale * p). { toPlane, fromPlane, matrix, quat }. */
export function shapeTransform(s) {
  const q = eulerToQuat(s.rotation_deg);
  const pos = (s.position || [0, 0, 0]).map(Number);
  const sc = (s.scale && s.scale.length >= 3 ? s.scale : [1, 1, 1]).map(Number);
  const safe = sc.map(v => (Math.abs(v) > 1e-9 ? v : 1e-9));
  return {
    quat: q,
    toPlane: p => add(pos, quatRotate(q, [p[0] * sc[0], p[1] * sc[1], p[2] * sc[2]])),
    fromPlane: p => { const l = quatRotate(quatConj(q), sub(p, pos)); return [l[0] / safe[0], l[1] / safe[1], l[2] / safe[2]]; },
    matrix: trs(pos, q, sc),
  };
}
