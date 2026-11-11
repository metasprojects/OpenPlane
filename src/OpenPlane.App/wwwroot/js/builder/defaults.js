// New parts, placed sensibly for the plane being built.
import { buildWing } from './geometry.js';
import { eyeOf } from './parts.js';

export const newId = kind => `${kind}-${Math.random().toString(36).slice(2, 8)}`;

/** Rough layout of the current plane from the last analysis: nose, tail, centre of gravity, length. */
export function layout(analysis) {
  const st = analysis?.stats;
  const min = st?.bounds_min, max = st?.bounds_max;
  const has = st && min && max && (max[2] - min[2]) > 0.5;
  const nose = has ? max[2] : 4, tail = has ? min[2] : -4;
  const cg = analysis?.flight?.center_of_mass || [0, 0, (nose + tail) / 2];
  const length = nose - tail;
  return { nose, tail, cgZ: cg[2], cgY: cg[1], length, bottom: has ? min[1] : -0.6, top: has ? max[1] : 0.6 };
}

export function fuselage(analysis) {
  const L = layout(analysis);
  const empty = !analysis?.stats || (analysis.stats.length_m || 0) < 0.5;
  return {
    id: newId('fus'), name: 'Fuselage', kind: 'loft', position: [0, 0, empty ? 0 : L.cgZ], rotation_deg: [0, 0, 0], smooth: 0, mirror: false, sides: 24, scale: 1, mesh_index: 0, source_frontal_area: 0, skin_thickness: 0,
    rings: [
      { z: 4.2, y: 0, width: 0, height: 0, roundness: 1 },
      { z: 3.2, y: 0, width: 0.8, height: 0.8, roundness: 1 },
      { z: 1.2, y: 0.05, width: 1.25, height: 1.35, roundness: 0.8 },
      { z: -2.0, y: 0.05, width: 1.1, height: 1.1, roundness: 0.8 },
      { z: -4.2, y: 0.2, width: 0.35, height: 0.5, roundness: 1 },
    ],
  };
}

export function wing(analysis) {
  const L = layout(analysis);
  return {
    id: newId('wing'), name: 'Wing', kind: 'wing', mirror: true, root: [0.5, 0, round(L.cgZ + 1.2)], span: 4, root_chord: 2.2, tip_chord: 1.1,
    sweep_deg: 10, dihedral_deg: 3, incidence_deg: 1, twist_deg: 0, vertical: false, thickness_ratio: 0.12,
    surfaces: [{ type: 'flap', from_pct: 5, to_pct: 55, chord_pct: 25 }, { type: 'aileron', from_pct: 58, to_pct: 95, chord_pct: 25 }],
    panels: [], offset: [0, 0, 0], scale: 1,
  };
}

export function tailplane(analysis) {
  const L = layout(analysis);
  return {
    id: newId('wing'), name: 'Tailplane', kind: 'wing', mirror: true, root: [0.15, round(L.cgY + 0.2), round(L.tail + 1.6)], span: 1.8, root_chord: 1.3, tip_chord: 0.7,
    sweep_deg: 15, dihedral_deg: 0, incidence_deg: 0, twist_deg: 0, vertical: false, thickness_ratio: 0.1,
    surfaces: [{ type: 'elevator', from_pct: 0, to_pct: 100, chord_pct: 35 }], panels: [], offset: [0, 0, 0], scale: 1,
  };
}

export function fin(analysis) {
  const L = layout(analysis);
  return {
    id: newId('wing'), name: 'Fin', kind: 'wing', mirror: false, root: [0, round(Math.max(0.3, L.top - 0.3)), round(L.tail + 1.8)], span: 1.7, root_chord: 1.6, tip_chord: 0.7,
    sweep_deg: 35, dihedral_deg: 0, incidence_deg: 0, twist_deg: 0, vertical: true, thickness_ratio: 0.1,
    surfaces: [{ type: 'rudder', from_pct: 0, to_pct: 100, chord_pct: 35 }], panels: [], offset: [0, 0, 0], scale: 1,
  };
}

export function jet(analysis) {
  const L = layout(analysis);
  return {
    id: newId('eng'), name: 'Jet engine', type: 'jet', source_type: null, title: null, position: [0, 0, round(L.tail + 1.2)], mirror: false,
    variant: 'turbojet', sizing: 'thrust', turbine_temp_k: 1400, afterburner: false, thrust_kn: 45, afterburner_kn: 0, diameter_m: 0.9, power_kw: 600, prop_diameter_m: 2.4, estimated: false,
  };
}

export function prop(analysis) {
  const L = layout(analysis);
  return {
    id: newId('eng'), name: 'Propeller engine', type: 'prop', source_type: null, title: null, position: [0, 0, round(L.nose + 0.15)], mirror: false,
    variant: 'piston', sizing: 'thrust', turbine_temp_k: 1400, afterburner: false, thrust_kn: 0, afterburner_kn: 0, diameter_m: 0.9, power_kw: 650, prop_diameter_m: 2.4, estimated: false,
  };
}

export function gear(analysis) {
  const L = layout(analysis);
  return {
    id: newId('gear'), name: 'Main gear', kind: 'main', position: [1.0, round(L.bottom + 0.1), round(L.cgZ - 0.6)], mirror: true,
    strut_length: 1.0, wheel_radius: 0.3, travel: 0.2, steerable: false, brakes: true, retractable: true,
  };
}

export function hardpoint(analysis) {
  const L = layout(analysis);
  return {
    id: newId('hp'), name: 'Hardpoint', position: [2.0, round(L.bottom + 0.2), round(L.cgZ)], rotation: [0, 0, 0, 1], mirror: true,
    allows: ['missile-ir', 'missile-radar', 'missile-ground', 'bomb', 'rockets'], internal: false, original_payload: 'empty',
  };
}

export function gun(analysis) {
  const L = layout(analysis);
  return {
    id: newId('gun'), name: 'Cannon', position: [0.45, round(L.cgY + 0.2), round(L.nose - Math.max(1.5, L.length * 0.25))], direction: [0, 0, 1], mirror: false,
    caliber_mm: 20, rounds_per_minute: 6000, muzzle_velocity: 1050, barrels: 1,
  };
}

export function cockpit(analysis) {
  const L = layout(analysis);
  return { position: [0, round(Math.max(L.cgY + 0.5, L.top - 0.35)), round(L.nose - Math.max(1.2, L.length * 0.22))] };
}

const round = v => Math.round(v * 100) / 100;
const round3 = v => Math.round(v * 1000) / 1000;

// ---------------------------------------------------------------- instruments and lights

/** Faces the pilot: turns the face's +y to -z (back along the plane). */
export const FACE_PILOT = [-0.7071, 0, 0, 0.7071];

export const INSTRUMENT_KINDS = {
  gauge: { label: 'Gauge', name: 'Airspeed gauge', size: 0.08, reads: 'airspeed' },
  attitude: { label: 'Artificial horizon', name: 'Artificial horizon', size: 0.09 },
  compass: { label: 'Compass', name: 'Compass', size: 0.07, reads: 'heading' },
  screen: { label: 'Screen', name: 'Screen', size: 0.14 },
  hud: { label: 'Head-up display', name: 'Head-up display', size: 0.14 },
  mirror: { label: 'Mirror', name: 'Mirror', size: 0.16 },
  lever: { label: 'Lever', name: 'Lever', size: 0.12 },
};

/**
 * A new instrument in front of the pilot: dials 0.55 m ahead of and 0.15 m below the eye, facing the pilot (each new dial
 * a little to the side of the last), the head-up display in the line of sight, a mirror above, a lever at the left hand.
 */
export function instrument(analysis, spec, kind = 'gauge') {
  const eye = eyeOf(spec?.cockpit || cockpit(analysis));
  const d = INSTRUMENT_KINDS[kind] || INSTRUMENT_KINDS.gauge;
  let at = { hud: [0, -0.04, 0.5], mirror: [0, 0.2, 0.42], lever: [-0.28, -0.42, 0.15] }[kind];
  if (!at) {
    const n = (spec?.instruments || []).filter(x => !['hud', 'mirror', 'lever'].includes(x.kind)).length;
    at = [[0, 1, -1, 2, -2][n % 5] * 0.1, -0.15 - (Math.floor(n / 5) % 3) * 0.1, 0.55];
  }
  return {
    id: newId('inst'), name: d.name, kind, reads: d.reads || null, position: eye.map((v, i) => round3(v + at[i])),
    rotation: kind === 'lever' ? [0, 0, 0, 1] : [...FACE_PILOT], size: d.size, min: 0, max: 1, min_deg: 30, max_deg: 330, mirror: false,
  };
}

export const LIGHT_KINDS = {
  nav: { label: 'Navigation lights', name: 'Navigation light' },
  strobe: { label: 'Strobe', name: 'Strobe' },
  beacon: { label: 'Beacon', name: 'Beacon' },
  landing: { label: 'Landing light', name: 'Landing light' },
  strip: { label: 'Light strip', name: 'Light strip' },
};
export const LIGHT_COLORS = { green: [0.1, 1, 0.25, 1], red: [1, 0.1, 0.08, 1], white: [1, 1, 1, 1] };

/** The right wingtip of the biggest wing: its leading and trailing corner, or null. */
function rightTip(spec) {
  let best = null;
  for (const w of spec?.wings || []) {
    if (w.vertical) continue;
    for (const p of buildWing(w))
      for (const [a, b] of [[p.tip_leading, p.tip_trailing], [p.root_leading, p.root_trailing]])
        if (!best || a[0] > best.a[0]) best = { a, b };
  }
  return best && best.a[0] > 0.3 ? best : null;
}

/** A new light: navigation lights and strobes on the right wingtip (mirrored), a beacon on top, a landing light under the nose. */
export function light(analysis, spec, kind = 'nav') {
  const L = layout(analysis);
  const half = (analysis?.stats?.span_m > 1 ? analysis.stats.span_m : 8) / 2;
  const tip = rightTip(spec) || { a: [half, L.cgY, L.cgZ + 0.5], b: [half, L.cgY, L.cgZ - 0.5] };
  const onTip = t => [tip.a[0] + 0.03, tip.a[1] + (tip.b[1] - tip.a[1]) * t, tip.a[2] + (tip.b[2] - tip.a[2]) * t].map(round3);
  const base = { id: newId('light'), name: (LIGHT_KINDS[kind] || LIGHT_KINDS.nav).name, kind, rotation: [0, 0, 0, 1], size: 0.1, mirror: false };
  switch (kind) {
    case 'strobe': return { ...base, position: onTip(0.85), color: [...LIGHT_COLORS.white], mirror: true };
    case 'beacon': return { ...base, position: [0, round3(L.top + 0.02), round(L.cgZ)], color: [1, 0.15, 0.08, 1], size: 0.14 };
    case 'landing': return { ...base, position: [0, round3(L.bottom + 0.15), round(L.nose - Math.max(1, L.length * 0.15))], color: [1, 0.96, 0.85, 1], size: 0.16 };
    case 'strip': return { ...base, position: [0.45, round(L.cgY), round(L.cgZ - 1)], rotation: [0, 0.7071, 0, 0.7071], color: [0.75, 0.95, 1, 1], size: 0.6, mirror: true };
    default: return { ...base, kind: 'nav', position: onTip(0.25), color: [...LIGHT_COLORS.green], mirror: true };
  }
}

/** A reference picture for a view, sized to the plane (or 10 m). */
export function reference(analysis, image, view, aspect) {
  const st = analysis?.stats;
  const along = st?.length_m > 1 ? st.length_m : 10, across = st?.span_m > 1 ? st.span_m : 10;
  const width = view === 'side' ? along * 1.1 : view === 'front' ? across * 1.1 : Math.max(along, across) * 1.1;
  const L = layout(analysis);
  const mid = (L.nose + L.tail) / 2;
  return {
    id: newId('ref'), name: (view === 'side' ? 'Side' : view === 'front' ? 'Front' : 'Top') + ' reference', image, view,
    center: view === 'side' ? [round(mid), 0] : view === 'front' ? [0, 0] : [0, round(mid)], width_m: round(width), rotation_deg: 0, opacity: 0.5, visible: true, locked: false,
  };
}
