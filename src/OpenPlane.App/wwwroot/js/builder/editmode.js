// Edit mode: the editable elements of a part (fuselage rings and their vertices, a shape's vertices/edges/faces, a
// wing's corners and control-surface strips), how to draw them, and how moved vertices are written back.
// All element positions are in plane space; write-back converts to each part's own space.
import {
  vec, clampSides, orderedRings, ringOffsets, isPointRing, fuselageTransform, shapeTransform, shapeFaces, planform,
  panelsPivot, mirrorIndex, hasPoints,
} from './geometry.js';
import { getItem, parseKey, fitWing } from './parts.js';

const R5 = v => Math.round(v * 1e5) / 1e5;
const r5 = a => a.map(R5);

/** Which element modes a part offers in Edit mode, or null when it has no Edit mode. */
export function editModes(spec, key) {
  const { kind } = parseKey(key);
  const it = getItem(spec, key);
  if (!it) return null;
  if (kind === 'fuselage') return it.kind === 'mesh' ? null : ['vert', 'ring'];
  if (kind === 'shape') return ['vert', 'edge', 'face'];
  if (kind === 'wing') return ['vert', 'face'];
  return null;
}

export const MODE_LABEL = { vert: 'Vertex', edge: 'Edge', face: 'Face', ring: 'Ring' };
const PLURAL = { vert: 'vertices', edge: 'edges', face: 'faces', ring: 'rings' };
/** '1 vertex', '3 vertices', '2 rings'... */
export const countOf = (mode, n) => `${n} ${n === 1 ? MODE_LABEL[mode].toLowerCase() : PLURAL[mode]}`;

// ---------------------------------------------------------------- elements

/**
 * The elements of a part in Edit mode:
 * { kind, verts: Map id -> { p, ... }, edges: [{ id, a, b }], faces: [{ id, verts, c }], rings: [{ id, verts, c, pts }] }
 */
export function buildElements(spec, key, ctx) {
  const { kind } = parseKey(key);
  const it = getItem(spec, key);
  if (!it) return null;
  if (kind === 'fuselage') return fuselageElements(it);
  if (kind === 'shape') return shapeElements(it);
  if (kind === 'wing') return wingElements(it);
  return null;
}

function fuselageElements(f) {
  const T = fuselageTransform(f);
  const n = clampSides(f.sides);
  const verts = new Map(), edges = [], rings = [];
  const order = orderedRings(f).map(r => f.rings.indexOf(r));
  f.rings.forEach((r, i) => {
    const c = [0, +r.y || 0, +r.z || 0];
    if (isPointRing(r)) {
      const id = `${i}:0`;
      verts.set(id, { p: T.toPlane(c), ring: i, k: 0, point: true });
      rings.push({ id: i, verts: [id], c: T.toPlane(c), pts: [T.toPlane(c)], point: true });
      return;
    }
    const offs = ringOffsets(r, n);
    const ids = [];
    offs.forEach((o, k) => { const id = `${i}:${k}`; ids.push(id); verts.set(id, { p: T.toPlane(vec.add(c, o)), ring: i, k }); });
    for (let k = 0; k < n; k++) edges.push({ id: `${ids[k]}|${ids[(k + 1) % n]}`, a: ids[k], b: ids[(k + 1) % n] });
    rings.push({ id: i, verts: ids, c: T.toPlane(c), pts: ids.map(id => verts.get(id).p) });
  });
  // Lines along the body between neighbouring rings.
  for (let j = 0; j + 1 < order.length; j++) {
    const A = rings.find(r => r.id === order[j]), B = rings.find(r => r.id === order[j + 1]);
    if (!A || !B) continue;
    if (A.point && B.point) continue;
    for (let k = 0; k < n; k++) {
      const a = A.point ? A.verts[0] : A.verts[k], b = B.point ? B.verts[0] : B.verts[k];
      edges.push({ id: `${a}|${b}`, a, b, along: true });
    }
  }
  return { kind: 'fuselage', verts, edges, faces: [], rings, order, n };
}

// A shape's topology (valid faces with their index, and its edges) only changes when its face list does.
const topoCache = new WeakMap();
function shapeTopology(s) {
  const key = s.faces || [];
  const n = (s.vertices || []).length;
  const hit = topoCache.get(key);
  if (hit && hit.n === n) return hit;
  const ok = new Set(shapeFaces(s));
  const faces = [];
  key.forEach((f, i) => { if (ok.has(f)) faces.push({ id: i, verts: f }); });
  const seen = new Set(), edges = [];
  for (const f of faces)
    for (let k = 0; k < f.verts.length; k++) {
      const a = f.verts[k], b = f.verts[(k + 1) % f.verts.length];
      const id = a < b ? `${a}|${b}` : `${b}|${a}`;
      if (seen.has(id)) continue;
      seen.add(id);
      edges.push({ id, a: Math.min(a, b), b: Math.max(a, b) });
    }
  const topo = { n, faces, edges };
  topoCache.set(key, topo);
  return topo;
}

function shapeElements(s) {
  const T = shapeTransform(s);
  const verts = new Map();
  (s.vertices || []).forEach((v, i) => verts.set(i, { p: T.toPlane([+v[0], +v[1], +v[2]]), local: v }));
  const topo = shapeTopology(s);
  const faces = topo.faces.map(f => {
    let x = 0, y = 0, z = 0;
    for (const v of f.verts) { const p = verts.get(v).p; x += p[0]; y += p[1]; z += p[2]; }
    const k = 1 / f.verts.length;
    return { id: f.id, verts: f.verts, c: [x * k, y * k, z * k] };
  });
  return { kind: 'shape', verts, edges: topo.edges, faces, rings: [] };
}

/** Plane-space corners of a fixed panel wing after its move/scale (the right-hand side). */
function bakedPanels(w) {
  const pv = panelsPivot(w.panels || []);
  const sc = +w.scale > 0 ? +w.scale : 1, off = w.offset || [0, 0, 0];
  const T = p => vec.add(vec.add(pv, vec.mul(vec.sub(p, pv), sc)), off);
  return (w.panels || []).map(p => ({
    ...p, root_leading: T(p.root_leading), root_trailing: T(p.root_trailing), tip_leading: T(p.tip_leading), tip_trailing: T(p.tip_trailing),
    surfaces: (p.surfaces || []).map(s => ({ ...s, root_leading: T(s.root_leading), root_trailing: T(s.root_trailing), tip_leading: T(s.tip_leading), tip_trailing: T(s.tip_trailing) })),
  }));
}

const CORNERS = ['root_leading', 'root_trailing', 'tip_trailing', 'tip_leading'];

function wingElements(w) {
  const verts = new Map(), edges = [], faces = [];
  if (w.kind === 'panels') {
    // Corners of every panel and surface, welded by position: dragging one moves every corner that shares it.
    const panels = bakedPanels(w);
    const clusters = [];
    const find = p => clusters.find(c => vec.len(vec.sub(c.p, p)) < 2e-3);
    const addCorner = (p, ref) => { let c = find(p); if (!c) { c = { id: `c${clusters.length}`, p, refs: [] }; clusters.push(c); } c.refs.push(ref); return c.id; };
    panels.forEach((pn, i) => {
      const ids = CORNERS.map(cn => addCorner(pn[cn], { panel: i, surface: -1, corner: cn }));
      for (let k = 0; k < 4; k++) edges.push({ id: `p${i}e${k}`, a: ids[k], b: ids[(k + 1) % 4] });
      (pn.surfaces || []).forEach((s, j) => {
        const sid = CORNERS.map(cn => addCorner(s[cn], { panel: i, surface: j, corner: cn }));
        for (let k = 0; k < 4; k++) edges.push({ id: `p${i}s${j}e${k}`, a: sid[k], b: sid[(k + 1) % 4] });
        if (s.role !== 'fixed') faces.push({ id: `p${i}s${j}`, verts: sid, c: vec.mul(CORNERS.map(cn => s[cn]).reduce(vec.add, [0, 0, 0]), 0.25), role: s.role, panel: i, surface: j });
      });
    });
    for (const c of clusters) verts.set(c.id, { p: c.p, refs: c.refs });
    return { kind: 'wing', panels: true, verts, edges, faces, rings: [] };
  }
  const pf = planform(w);
  const names = { rl: 'Root, leading edge', rt: 'Root, trailing edge', tt: 'Tip, trailing edge', tl: 'Tip, leading edge' };
  for (const k of ['rl', 'rt', 'tt', 'tl']) verts.set(k, { p: pf[k], label: names[k] });
  const ring = ['rl', 'rt', 'tt', 'tl'];
  for (let k = 0; k < 4; k++) edges.push({ id: `e${k}`, a: ring[k], b: ring[(k + 1) % 4] });
  // Control-surface strips (their own quads, from the wing's numbers).
  const P = (s, f) => vec.lerp(vec.lerp(pf.rl, pf.tl, s), vec.lerp(pf.rt, pf.tt, s), f);
  (w.surfaces || []).forEach((s, i) => {
    if (!s || s.type === 'none') return;
    const a = Math.min(+s.from_pct, +s.to_pct) / 100, b = Math.max(+s.from_pct, +s.to_pct) / 100, c = Math.max(0.05, Math.min(0.6, +s.chord_pct / 100));
    const lead = s.type === 'slat' || s.edge === 'leading';
    const q = lead ? [P(a, 0), P(a, c), P(b, c), P(b, 0)] : [P(a, 1 - c), P(a, 1), P(b, 1), P(b, 1 - c)];
    const ids = q.map((p, k) => { const id = `s${i}v${k}`; verts.set(id, { p, strip: i, hide: true }); return id; });
    for (let k = 0; k < 4; k++) edges.push({ id: `s${i}e${k}`, a: ids[k], b: ids[(k + 1) % 4], strip: i });
    faces.push({ id: `s${i}`, verts: ids, c: vec.mul(q.reduce(vec.add, [0, 0, 0]), 0.25), surface: i, role: s.type });
  });
  return { kind: 'wing', panels: false, verts, edges, faces, rings: [], planform: pf };
}

// ---------------------------------------------------------------- selection helpers

/** Vertex ids covered by a selection of elements in a mode. */
export function selectedVerts(el, mode, sel) {
  const out = new Set();
  if (mode === 'vert') for (const id of sel) { if (el.verts.has(id) && el.verts.get(id).strip == null) out.add(id); }
  else if (mode === 'edge') for (const e of el.edges) { if (sel.has(e.id)) { out.add(e.a); out.add(e.b); } }
  else if (mode === 'face') for (const f of el.faces) { if (sel.has(f.id)) f.verts.forEach(v => out.add(v)); }
  else if (mode === 'ring') for (const r of el.rings) { if (sel.has(r.id)) r.verts.forEach(v => out.add(v)); }
  return out;
}

/** Ids of every element in a mode. */
export function allIds(el, mode) {
  if (mode === 'vert') return [...el.verts.entries()].filter(([, v]) => v.strip == null).map(([id]) => id);
  if (mode === 'edge') return el.edges.filter(e => !e.along || el.kind !== 'fuselage').map(e => e.id);
  if (mode === 'face') return el.faces.map(f => f.id);
  if (mode === 'ring') return el.rings.map(r => r.id);
  return [];
}

/** Where an element sits (for box select and picking). */
export function elementPoint(el, mode, id) {
  if (mode === 'vert') return el.verts.get(id)?.p;
  if (mode === 'edge') { const e = el.edges.find(x => x.id === id); return e && vec.lerp(el.verts.get(e.a).p, el.verts.get(e.b).p, 0.5); }
  if (mode === 'face') return el.faces.find(f => f.id === id)?.c;
  if (mode === 'ring') return el.rings.find(r => r.id === id)?.c;
  return null;
}

/** Converts a selection when the element mode changes (Blender keeps what it can). */
export function convertSelection(el, from, to, sel) {
  const verts = selectedVerts(el, from, sel);
  const out = new Set();
  if (to === 'vert') verts.forEach(v => out.add(v));
  else if (to === 'edge') el.edges.forEach(e => { if (verts.has(e.a) && verts.has(e.b)) out.add(e.id); });
  else if (to === 'face') el.faces.forEach(f => { if (f.verts.every(v => verts.has(v))) out.add(f.id); });
  else if (to === 'ring') el.rings.forEach(r => { if (r.verts.every(v => verts.has(v))) out.add(r.id); });
  return out;
}

/** The mirror partner of a vertex (x reflected in the part's own space), or null. */
export function mirrorPartner(el, item, id) {
  if (el.kind === 'fuselage') {
    const v = el.verts.get(id);
    if (!v || v.point) return null;
    const p = `${v.ring}:${mirrorIndex(v.k, el.n)}`;
    return p === id ? null : p;
  }
  if (el.kind === 'shape') {
    el.mirrorMap ||= buildMirrorMap(item);
    const p = el.mirrorMap.get(id);
    return p === undefined || p === id ? null : p;
  }
  return null;
}

function buildMirrorMap(s) {
  const map = new Map();
  const key = v => `${Math.round(v[0] * 1e4)},${Math.round(v[1] * 1e4)},${Math.round(v[2] * 1e4)}`;
  const idx = new Map();
  (s.vertices || []).forEach((v, i) => idx.set(key(v), i));
  (s.vertices || []).forEach((v, i) => { const j = idx.get(key([-v[0], v[1], v[2]])); if (j !== undefined) map.set(i, j); });
  return map;
}

// ---------------------------------------------------------------- drawing

/** The overlay for the viewport (see Viewport.setEditOverlay). */
export function overlayFor(el, mode, sel, activeId) {
  const vsel = selectedVerts(el, mode, sel);
  if (el.kind === 'fuselage') {
    return {
      verts: mode === 'vert' ? [...el.verts.entries()].map(([id, v]) => ({ p: v.p, sel: vsel.has(id), act: id === activeId })) : el.rings.map(r => ({ p: r.c, sel: sel.has(r.id), act: r.id === activeId })),
      edges: el.edges.filter(e => e.along).map(e => ({ a: el.verts.get(e.a).p, b: el.verts.get(e.b).p, sel: vsel.has(e.a) && vsel.has(e.b) && mode === 'vert' })),
      lines: el.rings.filter(r => !r.point).map(r => ({ pts: r.pts, closed: true, sel: mode === 'ring' ? sel.has(r.id) : r.verts.every(v => vsel.has(v)) })),
    };
  }
  if (el.kind === 'shape') {
    return {
      verts: mode === 'face' ? [] : [...el.verts.entries()].map(([id, v]) => ({ p: v.p, sel: vsel.has(id), act: id === activeId })),
      edges: el.edges.map(e => ({ a: el.verts.get(e.a).p, b: el.verts.get(e.b).p, sel: mode === 'edge' ? sel.has(e.id) : vsel.has(e.a) && vsel.has(e.b) })),
      faces: mode === 'face' ? el.faces.map(f => ({ c: f.c, sel: sel.has(f.id) })) : [],
    };
  }
  // wing
  return {
    verts: [...el.verts.entries()].filter(([, v]) => v.strip == null).map(([id, v]) => ({ p: v.p, sel: vsel.has(id), act: id === activeId })),
    edges: el.edges.map(e => ({ a: el.verts.get(e.a).p, b: el.verts.get(e.b).p, sel: (mode === 'face' && e.strip !== undefined && sel.has(`s${e.strip}`)) || (vsel.has(e.a) && vsel.has(e.b) && el.verts.get(e.a).strip == null) })),
    faces: el.faces.map(f => ({ c: f.c, sel: mode === 'face' && sel.has(f.id) })),
    onTop: true, // the corners sit inside the drawn wing's thickness
  };
}

// ---------------------------------------------------------------- writing moved vertices back

/**
 * Writes new plane-space positions (Map id -> p) into the part in `spec`, starting from its snapshot `orig` and the
 * snapshot's elements `el`. Ring-mode moves keep rings parametric when they only move or scale.
 */
export function writeVerts(spec, key, orig, el, moved, opts = {}) {
  const it = getItem(spec, key);
  if (!it) return;
  if (el.kind === 'shape') {
    const T = shapeTransform(orig);
    it.vertices = (orig.vertices || []).map((v, i) => (moved.has(i) ? r5(T.fromPlane(moved.get(i))) : v));
    return;
  }
  if (el.kind === 'wing') {
    if (el.panels) {
      const panels = bakedPanels(orig);
      for (const [id, p] of moved) for (const ref of el.verts.get(id).refs) {
        const target = ref.surface < 0 ? panels[ref.panel] : panels[ref.panel].surfaces[ref.surface];
        target[ref.corner] = r5(p);
      }
      it.panels = panels; it.offset = [0, 0, 0]; it.scale = 1;
      return;
    }
    const get = k => moved.get(k) || el.verts.get(k).p;
    fitWing(it, get('rl'), get('rt'), get('tl'), get('tt'));
    return;
  }
  // Fuselage rings.
  const T = fuselageTransform(orig);
  const n = clampSides(orig.sides);
  const byRing = new Map();
  for (const [id, p] of moved) {
    const v = el.verts.get(id);
    if (!v) continue;
    if (!byRing.has(v.ring)) byRing.set(v.ring, new Map());
    byRing.get(v.ring).set(v.k, T.fromPlane(p));
  }
  it.rings = orig.rings.map((r, i) => {
    const m = byRing.get(i);
    if (!m) return r;
    const c = [0, +r.y || 0, +r.z || 0];
    if (isPointRing(r)) {
      const p = m.get(0);
      return { ...r, y: R5(p[1]), z: R5(p[2]) };
    }
    const ringOp = opts.ringOps?.get(i);
    if (ringOp && !hasPoints(r) && (ringOp.type === 'translate' || ringOp.type === 'scale')) {
      // Whole ring moved or scaled: stay a parametric ring.
      const nc = T.fromPlane(ringOp.center);
      const out = { ...r, y: R5(nc[1]), z: R5(nc[2]) };
      if (ringOp.type === 'scale') { out.width = R5(Math.abs(r.width * ringOp.s[0])); out.height = R5(Math.abs(r.height * ringOp.s[1])); }
      return out;
    }
    const offs = ringOffsets(r, n);
    let center = c;
    if (ringOp) center = T.fromPlane(ringOp.center);
    else if (m.size === n) {
      // Every point moved (proportional editing): keep the ring's station at the middle of its points.
      const all = [...m.values()];
      center = [0, all.reduce((t, p) => t + p[1], 0) / n, all.reduce((t, p) => t + p[2], 0) / n];
    }
    const pts = offs.map((o, k) => (m.has(k) ? vec.sub(m.get(k), center) : vec.sub(vec.add(c, o), center)));
    return { ...r, y: R5(center[1]), z: R5(center[2]), points: pts.map(r5) };
  });
}

// ---------------------------------------------------------------- fuselage ring tools

/** Inserts a copy of ring i next to it (towards the nearer end) and returns the new ring's index. */
export function extrudeRing(f, i) {
  const order = orderedRings(f).map(r => f.rings.indexOf(r));
  const at = order.indexOf(i);
  const copy = JSON.parse(JSON.stringify(f.rings[i]));
  f.rings.push(copy);
  // Towards the nose for the nose-most ring, otherwise towards the tail (the new ring is pulled there by the move).
  return { index: f.rings.length - 1, towardsNose: at === 0 && order.length > 1 };
}

/** A ring between ordered rings j and j+1 at fraction t (0 = the nose-side ring). */
export function ringBetween(f, j, t) {
  const order = orderedRings(f);
  const a = order[j], b = order[j + 1];
  if (!a || !b) return null;
  const n = clampSides(f.sides);
  const L = (x, y) => x + (y - x) * t;
  const ring = { z: R5(L(+a.z, +b.z)), y: R5(L(+a.y || 0, +b.y || 0)), width: R5(L(+a.width, +b.width)), height: R5(L(+a.height, +b.height)), roundness: R5(L(a.roundness ?? 1, b.roundness ?? 1)) };
  if (hasPoints(a) || hasPoints(b)) {
    const oa = ringOffsets(a, n), ob = ringOffsets(b, n);
    ring.points = oa.map((p, k) => r5(vec.lerp(p, ob[k], t)));
  }
  return ring;
}

/** Cross-section presets: the offsets (or parametric numbers) for a ring of a given width and height. */
export const PRESETS = [
  { id: 'circle', label: 'Circle' },
  { id: 'oval', label: 'Oval' },
  { id: 'square', label: 'Rounded square' },
  { id: 'teardrop', label: 'Teardrop' },
  { id: 'flat', label: 'Flat bottom' },
];

export function applyPreset(ring, preset, sides) {
  const n = clampSides(sides);
  const off = ringOffsets(ring, n);
  const xs = off.map(p => p[0]), ys = off.map(p => p[1]);
  const w = Math.max(0.01, Math.max(...xs) - Math.min(...xs)), h = Math.max(0.01, Math.max(...ys) - Math.min(...ys));
  const cy = (Math.max(...ys) + Math.min(...ys)) / 2;
  const base = { ...ring, y: R5((+ring.y || 0) + cy), points: null };
  delete base.points;
  if (preset === 'circle') { const d = (w + h) / 2; return { ...base, width: R5(d), height: R5(d), roundness: 1 }; }
  if (preset === 'oval') return { ...base, width: R5(w), height: R5(h), roundness: 1 };
  if (preset === 'square') return { ...base, width: R5(w), height: R5(h), roundness: 0.2 };
  const a = w / 2, b = h / 2, pts = [];
  for (let k = 0; k < n; k++) {
    const t = 2 * Math.PI * k / n, s = Math.sin(t), c = Math.cos(t);
    if (preset === 'teardrop') {
      // Round below, drawn to a soft point on top (a spine or canopy fairing).
      const pinch = c > 0 ? 1 - 0.8 * Math.pow(c, 1.5) : 1;
      pts.push([R5(a * s * pinch), R5(c > 0 ? b * c * 1.12 - b * 0.12 * (1 - c) : b * c), 0]);
    } else {
      // Flat bottom: round above, a flat floor below with small rounded corners.
      const y = c >= 0 ? b * c : -b * Math.min(1, 0.92 * Math.pow(Math.abs(c), 0.15));
      const x = c >= 0 ? a * s : a * Math.sign(s) * Math.min(1, Math.pow(Math.abs(s), 0.55));
      pts.push([R5(x), R5(y), 0]);
    }
  }
  return { ...base, width: R5(w), height: R5(h), roundness: 1, points: pts };
}

// ---------------------------------------------------------------- shape edge rings (loop cut preview)

/** The edges crossed by a loop cut started on edge a–b: walks across quads both ways. Returns [[a, b], ...]. */
export function edgeRing(s, a, b) {
  const faces = shapeFaces(s);
  const key = (x, y) => (x < y ? `${x}|${y}` : `${y}|${x}`);
  const byEdge = new Map();
  faces.forEach((f, fi) => f.forEach((v, k) => {
    const e = key(v, f[(k + 1) % f.length]);
    if (!byEdge.has(e)) byEdge.set(e, []);
    byEdge.get(e).push(fi);
  }));
  const out = [[a, b]];
  const seen = new Set([key(a, b)]);
  const walk = (x, y, fromFace) => {
    let cx = x, cy = y, prev = fromFace;
    for (let guard = 0; guard < 10000; guard++) {
      const fs = (byEdge.get(key(cx, cy)) || []).filter(fi => fi !== prev);
      if (!fs.length) return;
      const fi = fs[0], f = faces[fi];
      if (f.length !== 4) return;
      const k = f.findIndex((v, i) => (v === cx && f[(i + 1) % 4] === cy) || (v === cy && f[(i + 1) % 4] === cx));
      if (k < 0) return;
      const ox = f[(k + 2) % 4], oy = f[(k + 3) % 4];
      const e = key(ox, oy);
      if (seen.has(e)) return;
      seen.add(e);
      out.push([ox, oy]);
      cx = ox; cy = oy; prev = fi;
    }
  };
  const start = byEdge.get(key(a, b)) || [];
  walk(a, b, start[1] ?? -1);
  if (start.length > 1) walk(a, b, start[0]);
  return out;
}
