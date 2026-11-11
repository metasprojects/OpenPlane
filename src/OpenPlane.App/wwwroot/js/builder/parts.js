// What each kind of part is, where its origin sits, and how it responds to Move / Rotate / Scale in object mode.
// Every transform is applied from a snapshot of the part taken when the transform started, so moving the mouse never
// accumulates rounding, and Esc restores the snapshot exactly. Plane space throughout: x right, y up, z forward.
import {
  vec, planform, panelsPivot, meshCenter, fuselageTransform, orderedRings, eulerToQuat, quatToEuler, quatMul, quatRotate,
} from './geometry.js';

export const LISTS = {
  fuselage: 'fuselages', shape: 'shapes', wing: 'wings', engine: 'engines', gear: 'gear', hardpoint: 'hardpoints', gun: 'guns',
  instrument: 'instruments', light: 'lights', reference: 'references',
};
export const KIND_LABEL = {
  plane: 'Plane', fuselage: 'Fuselage', shape: 'Shape', wing: 'Wing', engine: 'Engine', gear: 'Landing gear', hardpoint: 'Hardpoint',
  gun: 'Gun', cockpit: 'Cockpit', instrument: 'Instrument', light: 'Light', reference: 'Reference image',
};
export const KIND_ICON = {
  fuselage: 'body', shape: 'cube', wing: 'wing', engine: 'engine', gear: 'gear', hardpoint: 'weapon', gun: 'weapon', cockpit: 'cockpit',
  instrument: 'prop', light: 'magic', reference: 'image', plane: 'plane',
};

export const keyOf = (kind, id) => `${kind}:${id}`;
export function parseKey(key) { const i = key.indexOf(':'); return { kind: key.slice(0, i), id: key.slice(i + 1) }; }
const ID_PREFIX = { fuselage: 'fus', engine: 'eng', hardpoint: 'hp', instrument: 'inst' };
export const newId = kind => `${ID_PREFIX[kind] || kind}-${Math.random().toString(36).slice(2, 8)}`;
/** A new id for a palette material (parts refer to materials by id). */
export const newMaterialId = () => `mat-${Math.random().toString(36).slice(2, 8)}`;

/** The pilot's eye (plane space): the cockpit's own eye, or 0.7 m above the seat. */
export function eyeOf(cockpit) {
  if (!cockpit) return null;
  if (Array.isArray(cockpit.eye) && cockpit.eye.length >= 3) return cockpit.eye.slice(0, 3).map(Number);
  const p = (cockpit.position || [0, 0, 0]).map(Number);
  return [p[0], p[1] + 0.7, p[2]];
}

// ---------------------------------------------------------------- the paint palette (spec.materials)
// Parts point at palette entries by id (material_id); pieces kept from a converted plane point at them by position in the
// list. These follow OpenPlane.Core's DesignMaterials, so the view shows the colours the game uses.

/** The default canopy glass (as MaterialDef.DefaultGlass). */
export const DEFAULT_GLASS = { id: 'glass', name: 'Glass', kind: 'glass', color: [0.55, 0.65, 0.72, 0.25], metallic: 0.2, smoothness: 0.95 };
export const paletteOf = spec => (Array.isArray(spec?.materials) ? spec.materials : []);
/** The palette entry with this id, or null (the plane's paint). */
export const materialById = (spec, id) => (id ? paletteOf(spec).find(m => m && m.id === id) || null : null);
/** The palette entry at this position, or null (the plane's paint). */
export function materialAt(spec, i) {
  const p = paletteOf(spec);
  return Number.isInteger(i) && i >= 0 && i < p.length && p[i] ? p[i] : null;
}
/** The palette's first glass, or the default glass (what older "glass" parts use). */
export const glassOf = spec => paletteOf(spec).find(m => m && m.kind === 'glass') || DEFAULT_GLASS;
/** The plane's base paint as a material. */
export function paintOf(spec) {
  const p = spec?.paint || {};
  return { id: null, name: 'Plane paint', kind: 'paint', color: p.base_color || [0.6, 0.62, 0.64, 1], metallic: p.metallic ?? 0, smoothness: p.smoothness ?? 0.3 };
}
/** The palette entry a shape is drawn with: its material, else glass for an older glass shape, else the paint. */
export const shapeMaterial = (spec, s) => materialById(spec, s.material_id) || (s.material === 'glass' ? glassOf(spec) : paintOf(spec));

const R4 = v => Math.round(v * 1e4) / 1e4;
const R3 = v => Math.round(v * 1e3) / 1e3;
const r4 = a => a.map(R4);
const deepCopy = o => JSON.parse(JSON.stringify(o));

/** The part a key names (the cockpit is { id: 'cockpit', position }), or null. */
export function getItem(spec, key) {
  if (!key) return null;
  const { kind, id } = parseKey(key);
  if (kind === 'cockpit') return spec.cockpit || null;
  return (spec[LISTS[kind]] || []).find(x => x.id === id) || null;
}

/** Every part's key, in outliner order. */
export function allKeys(spec) {
  const out = [];
  for (const kind of ['fuselage', 'shape', 'wing', 'engine', 'gear', 'hardpoint', 'gun'])
    for (const it of spec[LISTS[kind]] || []) out.push(keyOf(kind, it.id));
  if (spec.cockpit) out.push('cockpit:cockpit');
  for (const kind of ['instrument', 'light'])
    for (const it of spec[LISTS[kind]] || []) out.push(keyOf(kind, it.id));
  for (const r of spec.references || []) out.push(keyOf('reference', r.id));
  return out;
}

export function itemName(spec, key) {
  const { kind } = parseKey(key);
  if (kind === 'cockpit') return 'Cockpit';
  return getItem(spec, key)?.name || KIND_LABEL[kind];
}

// ---------------------------------------------------------------- origins

/** Where a reference image's centre sits in plane space (its depth axis at 0). */
export function referencePoint(r) {
  const c = r.center || [0, 0];
  if (r.view === 'side') return [0, c[1], c[0]];
  if (r.view === 'front') return [c[0], c[1], 0];
  return [c[0], 0, c[1]];
}
const REF_NORMAL = { top: [0, 1, 0], side: [1, 0, 0], front: [0, 0, 1] };

/** A part's origin (plane space): what Move drags and what Rotate/Scale turn about when it is alone. */
export function originOf(spec, key, sourceMeshes) {
  const it = getItem(spec, key);
  if (!it) return null;
  const { kind } = parseKey(key);
  switch (kind) {
    case 'fuselage': {
      if (it.kind === 'mesh') {
        const c = meshCenter(sourceMeshes[it.mesh_index]?.vertices || []);
        return vec.add(c, it.position || [0, 0, 0]);
      }
      const rings = orderedRings(it);
      const mid = rings.length ? (rings[0].z + rings[rings.length - 1].z) / 2 : 0;
      return fuselageTransform(it).toPlane([0, 0, mid]);
    }
    case 'shape': return (it.position || [0, 0, 0]).map(Number);
    case 'wing': return it.kind === 'panels' ? vec.add(panelsPivot(it.panels || []), it.offset || [0, 0, 0]) : (it.root || [0, 0, 0]).map(Number);
    case 'reference': return referencePoint(it);
    default: return (it.position || [0, 0, 0]).map(Number);
  }
}

/** The median of the origins of several parts. */
export function medianOf(spec, keys, sourceMeshes) {
  const pts = keys.map(k => originOf(spec, k, sourceMeshes)).filter(Boolean);
  if (!pts.length) return null;
  return vec.mul(pts.reduce(vec.add, [0, 0, 0]), 1 / pts.length);
}

// ---------------------------------------------------------------- transforms

/** Applies op to a point. op: { type: 'translate', d } | { type: 'rotate', q, pivot } | { type: 'scale', s, pivot }. */
export function xfPoint(p, op) {
  if (op.type === 'translate') return vec.add(p, op.d);
  if (op.type === 'rotate') return vec.add(op.pivot, quatRotate(op.q, vec.sub(p, op.pivot)));
  const d = vec.sub(p, op.pivot);
  return vec.add(op.pivot, [d[0] * op.s[0], d[1] * op.s[1], d[2] * op.s[2]]);
}

/** A direction under op (rotation only; scale and translation leave directions). */
const xfDir = (d, op) => (op.type === 'rotate' ? quatRotate(op.q, d) : d);

/** The single "how much bigger" factor of a scale: the component furthest from 1 (parts that only scale evenly). */
export const evenFactor = s => s.reduce((best, v) => (Math.abs(Math.abs(v) - 1) > Math.abs(Math.abs(best) - 1) ? v : best), 1);
const isUniform = s => Math.abs(s[0] - s[1]) < 1e-9 && Math.abs(s[1] - s[2]) < 1e-9;

/**
 * Applies a transform to one part (in `spec`) starting from its snapshot `orig`. Returns a note when the part could
 * only follow part of the transform (shown once in the status bar).
 */
export function applyToPart(spec, key, orig, op, ctx) {
  const { kind } = parseKey(key);
  const it = getItem(spec, key);
  if (!it || !orig) return null;
  const P = op.pivot;
  switch (kind) {
    case 'engine': case 'gear': case 'hardpoint': case 'gun': case 'cockpit': case 'instrument': case 'light': {
      it.position = r4(xfPoint(orig.position || [0, 0, 0], op));
      // The pilot's eye goes along with the seat.
      if (kind === 'cockpit' && Array.isArray(orig.eye)) it.eye = r4(vec.add(orig.eye, vec.sub(it.position, orig.position || [0, 0, 0])));
      if (kind === 'gun' && orig.direction) it.direction = r4(vec.norm(xfDir(orig.direction, op)));
      const turns = kind === 'hardpoint' || kind === 'instrument' || kind === 'light';
      if (turns && op.type === 'rotate') it.rotation = quatMul(op.q, orig.rotation || [0, 0, 0, 1]).map(v => Math.round(v * 1e6) / 1e6);
      if ((kind === 'instrument' || kind === 'light') && op.type === 'scale') it.size = R4(Math.max(0.005, (+orig.size || 0.08) * Math.abs(evenFactor(op.s))));
      if (op.type === 'scale' && isUniform(op.s)) {
        const f = Math.abs(op.s[0]);
        if (kind === 'gear') { it.strut_length = R3(orig.strut_length * f); it.wheel_radius = R3(orig.wheel_radius * f); }
        if (kind === 'engine') { it.diameter_m = R3(orig.diameter_m * f); it.prop_diameter_m = R3(orig.prop_diameter_m * f); }
      }
      if (kind === 'engine' || kind === 'gear' || kind === 'cockpit') if (op.type === 'rotate' && !ctx?.multi) return 'Engines, wheels and the cockpit only move; turning them has no effect in the game.';
      return null;
    }
    case 'shape': {
      if (op.type === 'translate') it.position = r4(vec.add(orig.position || [0, 0, 0], op.d));
      else if (op.type === 'rotate') {
        it.position = r4(xfPoint(orig.position || [0, 0, 0], op));
        it.rotation_deg = quatToEuler(quatMul(op.q, eulerToQuat(orig.rotation_deg || [0, 0, 0]))).map(R3);
      } else {
        it.position = r4(xfPoint(orig.position || [0, 0, 0], op));
        const sc = orig.scale || [1, 1, 1];
        it.scale = [sc[0] * op.s[0], sc[1] * op.s[1], sc[2] * op.s[2]].map(R4);
      }
      return null;
    }
    case 'fuselage': {
      if (orig.kind === 'mesh') {
        const c = meshCenter(ctx.sourceMeshes[orig.mesh_index]?.vertices || []);
        const A = vec.add(c, orig.position || [0, 0, 0]);
        if (op.type === 'translate') it.position = r4(vec.add(orig.position || [0, 0, 0], op.d));
        else if (op.type === 'rotate') {
          it.position = r4(vec.sub(xfPoint(A, op), c));
          it.rotation_deg = quatToEuler(quatMul(op.q, eulerToQuat(orig.rotation_deg || [0, 0, 0]))).map(R3);
        } else {
          const f = Math.abs(evenFactor(op.s));
          const A2 = vec.add(P, vec.mul(vec.sub(A, P), f));
          it.position = r4(vec.sub(A2, c));
          it.scale = R4((+orig.scale || 1) * f);
          if (!isUniform(op.s)) return 'Fixed bodies from a converted plane scale evenly. Use "Make editable" to stretch them.';
        }
        return null;
      }
      // A loft: move its position; turn it as a whole; scale its rings in its own axes.
      if (op.type === 'translate') it.position = r4(vec.add(orig.position || [0, 0, 0], op.d));
      else if (op.type === 'rotate') {
        it.position = r4(xfPoint(orig.position || [0, 0, 0], op));
        it.rotation_deg = quatToEuler(quatMul(op.q, eulerToQuat(orig.rotation_deg || [0, 0, 0]))).map(R3);
      } else {
        const s = op.s;
        it.position = r4(xfPoint(orig.position || [0, 0, 0], op));
        it.rings = orig.rings.map(r => {
          const n = { ...r, z: R4(r.z * s[2]), y: R4((r.y || 0) * s[1]), width: R4(Math.abs(r.width * s[0])), height: R4(Math.abs(r.height * s[1])) };
          if (Array.isArray(r.points)) n.points = r.points.map(p => [R4(p[0] * s[0]), R4(p[1] * s[1]), R4((p[2] || 0) * s[2])]);
          return n;
        });
      }
      return null;
    }
    case 'wing': {
      if (orig.kind === 'panels') {
        // Bake the panel move/scale into the corners, then transform the corners themselves.
        const pv = panelsPivot(orig.panels || []);
        const sc = +orig.scale > 0 ? +orig.scale : 1, off = orig.offset || [0, 0, 0];
        const base = p => vec.add(vec.add(pv, vec.mul(vec.sub(p, pv), sc)), off);
        if (op.type === 'translate') { it.offset = r4(vec.add(off, op.d)); return null; }
        const T = p => r4(xfPoint(base(p), op));
        const up = u => {
          if (op.type === 'rotate') return r4(vec.norm(quatRotate(op.q, u)));
          return r4(vec.norm([u[0] / (op.s[0] || 1e-9), u[1] / (op.s[1] || 1e-9), u[2] / (op.s[2] || 1e-9)]));
        };
        const corners = o => ({ root_leading: T(o.root_leading), root_trailing: T(o.root_trailing), tip_leading: T(o.tip_leading), tip_trailing: T(o.tip_trailing) });
        it.panels = orig.panels.map(p => ({ ...p, ...corners(p), up: up(p.up || [0, 1, 0]), surfaces: (p.surfaces || []).map(s => ({ ...s, ...corners(s) })) }));
        it.offset = [0, 0, 0]; it.scale = 1;
        return null;
      }
      if (op.type === 'translate') { it.root = r4(vec.add(orig.root || [0, 0, 0], op.d)); return null; }
      const pf = planform(orig);
      fitWing(it, xfPoint(pf.rl, op), xfPoint(pf.rt, op), xfPoint(pf.tl, op), xfPoint(pf.tt, op));
      return null;
    }
    case 'reference': {
      const c = orig.center || [0, 0];
      if (op.type === 'translate') {
        const d = op.d;
        it.center = r4(orig.view === 'side' ? [c[0] + d[2], c[1] + d[1]] : orig.view === 'front' ? [c[0] + d[0], c[1] + d[1]] : [c[0] + d[0], c[1] + d[2]]);
      } else if (op.type === 'rotate') {
        const n = REF_NORMAL[orig.view] || REF_NORMAL.top;
        const w = [op.q[0], -op.q[1], -op.q[2], op.q[3]];                   // to world (x mirrored)
        const nw = [-n[0], n[1], n[2]];
        const ang = 2 * Math.atan2(w[0] * nw[0] + w[1] * nw[1] + w[2] * nw[2], w[3]) * 180 / Math.PI;
        it.rotation_deg = R3((+orig.rotation_deg || 0) + ang);
      } else {
        const f = Math.abs(evenFactor(op.s));
        it.width_m = R4(Math.max(0.05, (+orig.width_m || 10) * f));
        const p = xfPoint(referencePoint(orig), { ...op, s: [f, f, f] });
        it.center = r4(orig.view === 'side' ? [p[2], p[1]] : orig.view === 'front' ? [p[0], p[1]] : [p[0], p[2]]);
      }
      return null;
    }
  }
  return null;
}

/**
 * Fits a parametric wing's numbers (root, span, chords, sweep, dihedral, incidence, twist) to four corners, the
 * inverse of geometry.planform. Used when a wing is turned or scaled, and when its corners are dragged in Edit mode.
 */
export function fitWing(w, rl, rt, tl, tt) {
  const DEG = 180 / Math.PI;
  const d = vec.sub(tl, rl);
  const span = Math.max(0.05, Math.hypot(d[0], d[1]));
  const theta = Math.atan2(d[1], d[0]);
  const u0 = [-Math.sin(theta), Math.cos(theta), 0];
  const v = vec.sub(rt, rl), vt = vec.sub(tt, tl);
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const inc = Math.atan2(-dot(v, u0), -v[2]);
  const incT = Math.atan2(-dot(vt, u0), -vt[2]);
  let twist = (incT - inc) * DEG;
  while (twist > 180) twist -= 360;
  while (twist < -180) twist += 360;
  w.root = r4(rl);
  w.span = R3(span);
  w.root_chord = R3(Math.max(0.05, vec.len(v)));
  w.tip_chord = R3(Math.max(0.02, vec.len(vt)));
  w.dihedral_deg = R3(w.vertical ? 90 - theta * DEG : theta * DEG);
  w.sweep_deg = R3(Math.max(-80, Math.min(80, Math.atan2(-d[2], span) * DEG)));
  w.incidence_deg = R3(inc * DEG);
  w.twist_deg = R3(Math.max(-30, Math.min(30, twist)));
}

// ---------------------------------------------------------------- copies

/** A copy of a part with a fresh id (the caller adds it and moves it). */
export function duplicatePart(spec, key) {
  const { kind } = parseKey(key);
  if (kind === 'cockpit') return null;
  const it = getItem(spec, key);
  if (!it) return null;
  const copy = deepCopy(it);
  copy.id = newId(kind);
  copy.name = `${it.name || KIND_LABEL[kind]} copy`;
  return { kind, item: copy, key: keyOf(kind, copy.id) };
}

export function addPart(spec, kind, item) {
  if (kind === 'cockpit') { spec.cockpit = item; return 'cockpit:cockpit'; }
  (spec[LISTS[kind]] ||= []).push(item);
  return keyOf(kind, item.id);
}

export function removePart(spec, key) {
  const { kind, id } = parseKey(key);
  if (kind === 'cockpit') { spec.cockpit = null; return; }
  spec[LISTS[kind]] = (spec[LISTS[kind]] || []).filter(x => x.id !== id);
  if (spec.editor) {
    spec.editor.hidden = (spec.editor.hidden || []).filter(k => k !== key);
    spec.editor.locked = (spec.editor.locked || []).filter(k => k !== key);
  }
}
