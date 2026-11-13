// Properties: the settings of whatever is selected (or of the plane), plus Edit-mode tools for the part being edited.
// Number boxes scrub like Blender's: drag sideways on one to change it, click to type.
import { html, esc, fmt, fmtKg, donorLabel, DONORS } from '../ui.js';
import { icon } from '../icons.js';
import { buildWing, quadArea, propThrustN, hasTwin, engineThrust, shapeFaces, triangulate, eulerToQuat, quatToEuler } from './geometry.js';
import { KIND_LABEL, parseKey, getItem, paletteOf, newMaterialId, eyeOf } from './parts.js';
import { PRESETS, MODE_LABEL, countOf } from './editmode.js';
import { INSTRUMENT_KINDS, LIGHT_KINDS, LIGHT_COLORS } from './defaults.js';

const WEAPONS = [
  ['missile-ir', 'IR (heat-seeking) missiles'], ['missile-radar', 'Radar missiles'], ['missile-ground', 'Ground-attack missiles'],
  ['bomb', 'Bombs'], ['rockets', 'Rockets'], ['tank', 'Drop tanks'], ['gun-pod', 'Gun pods'],
];
const SURFACE_TYPES = [
  ['aileron', 'Aileron (roll)'], ['elevator', 'Elevator (pitch)'], ['rudder', 'Rudder (yaw)'], ['flap', 'Flap'],
  ['elevon', 'Elevon (pitch + roll)'], ['flaperon', 'Flaperon (roll + flaps)'], ['ruddervator', 'Ruddervator (pitch + yaw)'],
  ['airbrake', 'Airbrake'], ['slat', 'Slat (leading edge)'], ['none', 'None'],
];
const AXES = [['pitch', 'Pitch'], ['roll', 'Roll'], ['yaw', 'Yaw'], ['flaps', 'Flaps'], ['brake', 'Brake'], ['throttle', 'Throttle'], ['gear', 'Gear']];
const ROLE_DEFAULTS = {
  aileron: { min: -20, max: 20, servo: 90, inputs: [['roll', -20]] }, elevator: { min: -20, max: 20, servo: 90, inputs: [['pitch', 20]] },
  rudder: { min: -20, max: 20, servo: 90, inputs: [['yaw', 20]] }, flap: { min: 0, max: 30, servo: 20, inputs: [['flaps', 30]] },
  elevon: { min: -25, max: 25, servo: 90, inputs: [['pitch', 20], ['roll', -20]] }, flaperon: { min: -20, max: 30, servo: 90, inputs: [['roll', -20], ['flaps', 30]] },
  ruddervator: { min: -20, max: 20, servo: 90, inputs: [['pitch', 20], ['yaw', 20]] }, airbrake: { min: 0, max: 60, servo: 45, inputs: [['brake', 60]] },
  slat: { min: 0, max: 25, servo: 20, inputs: [['flaps', 25]] },
};
const NOT_IN_GAME = '<span class="tag-soon" title="Saved with the plane, but the OpenPlane mod does not use it in the game yet.">not in game yet</span>';
const MATERIAL_KINDS = [['paint', 'Paint (solid)'], ['glass', 'Glass (see-through)'], ['light', 'Light colour (glows)']];
const KIND_WORD = { paint: 'paint', glass: 'glass', light: 'light' };
const READINGS = [
  ['airspeed', 'Airspeed (km/h)'], ['altitude', 'Altitude (m)'], ['radar-altitude', 'Height above the ground (m)'], ['vertical-speed', 'Climb rate (m/s)'],
  ['rpm', 'Engine speed (%)'], ['throttle', 'Throttle (0 to 1)'], ['fuel', 'Fuel left (0 to 1)'], ['g', 'G-force (g)'], ['aoa', 'Angle of attack (°)'],
  ['heading', 'Heading (°)'], ['clock', 'Clock (hours)'],
];
// What a screen shows and what moves a lever in the game (the instrument's "reads").
const SCREEN_PAGES = [['', 'Nothing (a plain green display)'], ['flight', 'Flight: attitude, speed, altitude, heading, g'], ['engine', 'Engine: engine speed, throttle and fuel bars'], ['nav', 'Navigation: heading, height above the ground, climb rate']];
const SCREEN_HELP = {
  flight: 'A small primary flight display: an attitude ball with airspeed (knots) on the left, altitude (feet) on the right, heading on top, g and angle of attack below.',
  engine: 'Three bars with numbers: engine speed, throttle and fuel left.',
  nav: 'A heading needle with the heading, the height above the ground (feet) and the climb rate (feet a minute).',
};
const LEVER_MOTIONS = [['', 'Nothing (it stays still)'], ['throttle', 'The throttle (forward for more power)'], ['gear', 'The landing gear (down with the gear)'], ['stick', 'The stick (pitch and roll)']];
const INSTRUMENT_HELP = {
  attitude: 'Shows the horizon: blue sky over brown ground, tilting and rolling with the plane.',
  compass: 'Its needle shows which way the plane is heading.',
  screen: 'A dark green display.',
  hud: 'A see-through green glass in the pilot\'s line of sight.',
  mirror: 'A silver mirror.',
  lever: 'A stick with a knob. It sticks out along its facing direction (straight up when Facing is 0, 0, 0).',
};
const LIGHT_HELP = {
  nav: 'A steady lamp: green on the right wingtip, red on the left, white at the tail.',
  strobe: 'Flashes quickly, usually white on the wingtips or the tail.',
  beacon: 'Blinks slowly, usually red, on top of or under the body.',
  landing: 'A bright lamp, usually under the nose or on the gear.',
  strip: 'A thin glowing bar (a formation light). Press R to turn it.',
};

let uid = 0;
const fid = () => `f${++uid}`;
// The palette row open for editing (by its place in the list), kept while the panel redraws.
let openMaterial = -1;

const clamp01 = v => Math.max(0, Math.min(1, Number.isFinite(+v) ? +v : 0));
const toHex = c => '#' + (c || [0, 0, 0]).slice(0, 3).map(v => Math.round(clamp01(v) * 255).toString(16).padStart(2, '0')).join('');
const fromHex = hex => [1, 3, 5].map(i => Math.round(parseInt(hex.slice(i, i + 2), 16) / 255 * 1000) / 1000);

// ---------------------------------------------------------------- field helpers

/** A number box. `set(value, final)` runs while typing or dragging (final=false) and when committed (final=true). */
function num(label, value, set, { unit = '', step = 0.1, min = -1e6, max = 1e6, digits = 2, hint = '', title = '' } = {}) {
  const id = fid();
  const el = html(`<div class="numfield">
      <label class="label" for="${id}" title="${esc(title || label)}">${esc(label)}</label>
      <div class="wrap"><input type="number" id="${id}" step="${step}" min="${min}" max="${max}" value="${esc(roundTo(value, digits))}" title="${esc(title || 'Drag sideways to change, click to type')}">${unit ? `<span class="unit">${esc(unit)}</span>` : ''}</div>
      ${hint ? `<span class="hint faint small">${hint}</span>` : ''}
    </div>`);
  const input = el.querySelector('input');
  const clamp = v => Math.min(max, Math.max(min, v));
  const read = () => { const v = parseFloat(input.value); return Number.isFinite(v) ? clamp(v) : null; };
  input.addEventListener('input', () => { const v = read(); if (v !== null) set(v, false); });
  input.addEventListener('change', () => {
    const v = read();
    if (v === null) { input.value = roundTo(value, digits); return; }
    if (String(v) !== input.value) input.value = roundTo(v, digits);
    set(v, true);
  });
  // Scrub: drag sideways on a box that isn't being typed in.
  input.addEventListener('pointerdown', e => {
    if (document.activeElement === input || e.button !== 0) return;
    e.preventDefault();
    const x0 = e.clientX, v0 = read() ?? 0;
    let dragged = false;
    input.setPointerCapture(e.pointerId);
    const mv = ev => {
      const dx = ev.clientX - x0;
      if (!dragged && Math.abs(dx) < 3) return;
      dragged = true;
      const k = ev.shiftKey ? 0.1 : ev.ctrlKey ? 10 : 1;
      const v = clamp(Math.round((v0 + Math.round(dx / 4) * step * k) / step) * step);
      input.value = roundTo(v, digits);
      set(v, false);
    };
    const up = ev => {
      input.removeEventListener('pointermove', mv); input.removeEventListener('pointerup', up);
      input.releasePointerCapture?.(ev.pointerId);
      if (dragged) set(read() ?? v0, true);
      else { input.focus(); input.select(); }
    };
    input.addEventListener('pointermove', mv); input.addEventListener('pointerup', up);
  });
  return el;
}

function roundTo(v, d) { const n = Number(v); return Number.isFinite(n) ? String(Math.round(n * 10 ** d) / 10 ** d) : ''; }

function text(label, value, set, { placeholder = '', multiline = false } = {}) {
  const id = fid();
  const el = html(`<label class="field" for="${id}"><span class="label">${esc(label)}</span>
      ${multiline ? `<textarea id="${id}" placeholder="${esc(placeholder)}">${esc(value || '')}</textarea>` : `<input type="text" id="${id}" value="${esc(value || '')}" placeholder="${esc(placeholder)}">`}</label>`);
  const input = el.querySelector('input, textarea');
  input.addEventListener('input', () => set(input.value, false));
  input.addEventListener('change', () => set(input.value, true));
  return el;
}

function check(label, value, set, hint = '') {
  const el = html(`<label class="check"><input type="checkbox" ${value ? 'checked' : ''}><span>${esc(label)}${hint ? ` <span class="faint small">${hint}</span>` : ''}</span></label>`);
  el.querySelector('input').addEventListener('change', e => set(e.target.checked, true));
  return el;
}

function select(label, value, options, set, extra = '') {
  const id = fid();
  const el = html(`<label class="field" for="${id}"><span class="label">${esc(label)} ${extra}</span><select id="${id}">
      ${options.map(([v, t]) => `<option value="${esc(v)}" ${String(v) === String(value) ? 'selected' : ''}>${esc(t)}</option>`).join('')}</select></label>`);
  el.querySelector('select').addEventListener('change', e => set(e.target.value, true));
  return el;
}

/** Three number boxes (plane axes: x right, y up, z forward). */
function vec3(label, arr, set, { unit = 'm', step = 0.05, digits = 3, names = ['X (right)', 'Y (up)', 'Z (fwd)'], hint = '' } = {}) {
  const v = [...(arr || [0, 0, 0])];
  const el = html(`<div class="insp-sub" role="group" aria-label="${esc(label)}"><span class="label-caps">${esc(label)}</span><div class="row3"></div>${hint ? `<span class="faint small">${hint}</span>` : ''}</div>`);
  const row = el.querySelector('.row3');
  names.forEach((name, i) => row.append(num(name, v[i], (val, final) => { v[i] = val; set([...v], final); }, { unit, step, digits })));
  return el;
}

function group(title, ...children) {
  const el = html(`<div class="insp-group">${title ? `<span class="label-caps">${title}</span>` : ''}</div>`);
  children.filter(Boolean).forEach(c => el.append(c));
  return el;
}

function row2(a, b) { const el = html(`<div class="row2"></div>`); el.append(a, ...(b ? [b] : [])); return el; }

/** A colour picker; `set([r, g, b], final)` while picking and when the picker closes. */
function colorField(label, color, set) {
  const id = fid();
  const el = html(`<label class="field" for="${id}"><span class="label">${esc(label)}</span><input type="color" id="${id}" value="${toHex(color)}"></label>`);
  const ci = el.querySelector('input');
  ci.addEventListener('input', () => set(fromHex(ci.value), false));
  ci.addEventListener('change', () => set(fromHex(ci.value), true));
  return el;
}

/** A 0 to 1 slider shown in percent; `set(value, final)`. */
function slider(label, value, set, title = '') {
  const id = fid();
  const pct = Math.round(clamp01(value) * 100);
  const el = html(`<label class="slider-field" for="${id}" title="${esc(title || label)}"><span>${esc(label)}</span><span class="mono">${pct}%</span><input type="range" id="${id}" min="0" max="100" value="${pct}"></label>`);
  const r = el.querySelector('input');
  r.addEventListener('input', () => { el.querySelector('.mono').textContent = `${r.value}%`; set(r.value / 100, false); });
  r.addEventListener('change', () => set(r.value / 100, true));
  return el;
}

function btnRow(...buttons) { const el = html('<div class="btn-row"></div>'); el.append(...buttons.filter(Boolean)); return el; }
function note(textHtml, kind = 'note') { return html(`<div class="notice ${kind}">${icon(kind === 'warn' ? 'warn' : 'info')}<div>${textHtml}</div></div>`); }
function button(label, ic, run, { primary = false, keys = '', title = '' } = {}) {
  const b = html(`<button type="button" class="btn ${primary ? 'primary' : ''}" title="${esc(title || label)}${keys ? ` (${esc(keys)})` : ''}">${ic ? icon(ic, 'sm') : ''}<span>${esc(label)}</span>${keys ? `<kbd>${esc(keys)}</kbd>` : ''}</button>`);
  b.onclick = run;
  return b;
}

// ---------------------------------------------------------------- inspector

/**
 * ctx: { spec, key ('plane' or a part key), count (selected parts), analysis, sourceMeshes, report,
 *        edit: { key, mode, sel: Set, el } | null, change(fn, final, opts), select(key), run(commandId), actions: {...} }
 */
export function renderInspector(host, ctx) {
  host.innerHTML = '';
  if (ctx.count > 1) return multiFields(host, ctx);
  const key = ctx.key || 'plane';
  const plane = key === 'plane';
  const { kind } = plane ? { kind: 'plane' } : parseKey(key);
  const item = plane ? null : getItem(ctx.spec, key);
  if (!plane && !item) { ctx.select('plane'); return; }
  const title = plane ? (ctx.spec.name || 'Plane') : kind === 'cockpit' ? 'Cockpit' : (item.name || KIND_LABEL[kind]);
  const head = html(`<div class="insp-head"><h2 title="${esc(title)}">${esc(title)}</h2></div>`);
  if (!plane) {
    if (kind !== 'cockpit') head.append(button('', 'copy', () => ctx.run('duplicate'), { title: 'Duplicate', keys: 'Shift+D' }));
    const del = button('', 'trash', () => ctx.run('delete'), { title: 'Delete', keys: 'X' });
    del.classList.add('danger');
    head.append(del);
    head.querySelectorAll('.btn').forEach(b => { b.className = 'icon-btn'; b.querySelector('kbd')?.remove(); b.querySelector('span:not(.i)')?.remove(); });
  }
  host.append(head);
  host.append(html(`<span class="insp-kind">${plane ? 'Plane settings' : esc(KIND_LABEL[kind])}${item?.mirror && (kind === 'wing' || kind === 'fuselage' || kind === 'shape' || hasTwin(item.position, item.mirror)) ? ' · mirrored (×2)' : ''}</span>`));

  const set = (fn, final, opts) => ctx.change(spec => { const it = plane ? spec : getItem(spec, key); if (it) fn(it, spec); }, final, opts);
  const nameField = () => text('Name', item.name, (v, f) => set(it => { it.name = v; }, f, { outline: true, inspector: false }));

  if (ctx.edit && ctx.edit.key === key) editTools(host, ctx, item, set);

  switch (kind) {
    case 'plane': planeFields(host, ctx, set); break;
    case 'fuselage': item.kind === 'mesh' ? meshFields(host, ctx, item, set, nameField) : loftFields(host, ctx, item, set, nameField); break;
    case 'shape': shapeFields(host, ctx, item, set, nameField); break;
    case 'wing': item.kind === 'panels' ? panelFields(host, ctx, item, set, nameField) : wingFields(host, ctx, item, set, nameField); break;
    case 'engine': engineFields(host, ctx, item, set, nameField); break;
    case 'gear': gearFields(host, ctx, item, set, nameField); break;
    case 'hardpoint': hardpointFields(host, ctx, item, set, nameField); break;
    case 'gun': gunFields(host, ctx, item, set, nameField); break;
    case 'reference': referenceFields(host, ctx, item, set, nameField); break;
    case 'instrument': instrumentFields(host, ctx, item, set, nameField); break;
    case 'light': lightFields(host, ctx, item, set, nameField); break;
    case 'cockpit': cockpitFields(host, ctx, item, set); break;
  }
}

function multiFields(host, ctx) {
  host.append(html(`<div class="insp-head"><h2>${ctx.count} parts selected</h2></div>`));
  host.append(html(`<span class="insp-kind">The last one you clicked is the active part (brighter in the view).</span>`));
  const g = group('Together');
  const grid = html('<div class="ops-grid"></div>');
  grid.append(button('Move', 'move', () => ctx.run('grab'), { keys: 'G' }), button('Rotate', 'rotate', () => ctx.run('rotate'), { keys: 'R' }),
    button('Scale', 'scale', () => ctx.run('scale'), { keys: 'S' }), button('Duplicate', 'copy', () => ctx.run('duplicate'), { keys: 'Shift+D' }),
    button('Hide', 'eyeOff', () => ctx.run('hide'), { keys: 'H' }), button('Frame', 'frame', () => ctx.run('frameSel'), { keys: 'F' }),
    button('Delete', 'trash', () => ctx.run('delete'), { keys: 'X' }));
  g.append(grid);
  host.append(g);
  host.append(note('Moving, rotating and scaling several parts turns and scales them around their middle (the median of their origins), like Blender.'));
}

function mirrorField(item, set, what = 'part') {
  return check('Mirror on the other side', item.mirror, v => set(it => { it.mirror = v; }, true, { outline: true }), `(a matching left ${what})`);
}

// ---------------------------------------------------------------- edit-mode tools

function editTools(host, ctx, item, set) {
  const e = ctx.edit;
  const { kind } = parseKey(e.key);
  const count = e.sel.size;
  const g = group(`Edit mode · ${esc(MODE_LABEL[e.mode])} select`);
  g.append(html(`<span class="faint small">${count ? `${countOf(e.mode, count)} selected.` : 'Nothing selected yet: click in the view, or drag a box.'} Tab goes back to Object mode.</span>`));
  const grid = html('<div class="ops-grid"></div>');
  const op = (label, ic, id, keys, title) => { const b = button(label, ic, () => ctx.run(id), { keys, title }); grid.append(b); return b; };
  op('Move', 'move', 'grab', 'G'); op('Rotate', 'rotate', 'rotate', 'R'); op('Scale', 'scale', 'scale', 'S');
  if (kind === 'fuselage') {
    op('Extrude ring', 'extrude', 'extrude', 'E', 'Copy the selected end ring and pull it out');
    op('Loop cut', 'loopcut', 'loopcut', 'Ctrl+R', 'Add a ring between two rings');
    op('Delete ring', 'trash', 'delete', 'X');
  } else if (kind === 'shape') {
    op('Extrude', 'extrude', 'extrude', 'E', 'Pull the selected faces out');
    op('Inset', 'inset', 'inset', 'I', 'A smaller face inside each selected face');
    op('Loop cut', 'loopcut', 'loopcut', 'Ctrl+R', 'Cut a loop of edges across the quads');
    op('Merge', 'merge', 'merge', 'M');
    op('Delete', 'trash', 'delete', 'X');
    op('Subdivide', 'smooth', 'subdivideApply', '', 'Smooth the shape once and keep the new vertices');
    op('Outside out', 'refresh', 'recalc', 'Shift+N', 'Make every face point outwards');
    op('Flip faces', 'mirror', 'flip', '', 'Turn the selected faces inside out');
  } else if (kind === 'wing') {
    op('Sweep', 'wing', 'wingSweep', '', 'Drag to change the sweep');
    op('Dihedral', 'wing', 'wingDihedral', '', 'Drag to change the dihedral');
    op('Twist', 'rotate', 'wingTwist', '', 'Drag to twist the tip');
  }
  g.append(grid);
  if (kind === 'fuselage' && (e.mode === 'ring' || count)) {
    const pr = html(`<div class="insp-sub"><span class="label-caps">Cross-section of the selected rings</span><div class="preset-row"></div></div>`);
    PRESETS.forEach(p => pr.querySelector('.preset-row').append(button(p.label, '', () => ctx.actions.preset(p.id))));
    g.append(pr);
  }
  if (kind === 'wing' && e.mode === 'face' && count === 1) {
    const sid = [...e.sel][0];
    g.append(html(`<span class="faint small">Strip selected: its settings are highlighted below. G slides it along the span, S changes its chord.</span>`));
    ctx.focusSurface = String(sid).replace(/^s/, '');
  }
  if (ctx.edit.proportional) g.append(html(`<span class="faint small">Proportional editing is on (O): nearby vertices follow. Scroll while moving to change the circle.</span>`));
  host.append(g);
}

// ---------------------------------------------------------------- the plane

function planeFields(host, ctx, set) {
  const s = ctx.spec;
  const a = ctx.analysis;
  host.append(group('', text('Name', s.name, (v, f) => set(p => { p.name = v; }, f, { outline: true, inspector: false, title: true })),
    text('Built by', s.author, (v, f) => set(p => { p.author = v; if (p.origin && p.origin.source !== 'flyout') p.origin.builder = v || null; }, f, { inspector: false }), { placeholder: 'Your name' }),
    text('Description', s.description, (v, f) => set(p => { p.description = v || null; }, f, { inspector: false }), { multiline: true, placeholder: 'Optional: shown in the game\'s encyclopedia' })));

  if (s.origin?.source === 'flyout') {
    const box = note(`Converted from the Flyout craft <strong>${esc(s.origin.craft_name || '')}</strong>${s.origin.builder ? `, built by <strong>${esc(s.origin.builder)}</strong>` : ''}. The builder's credit stays with the plane.
        ${ctx.report?.length ? '<div style="margin-top:6px"><button type="button" class="btn small">What changed in conversion</button></div>' : ''}`);
    box.querySelector('button')?.addEventListener('click', () => ctx.actions.showReport());
    host.append(box);
  }

  const autoName = a?.donor ? donorLabel(a.donor) : 'picked from the engines and size';
  host.append(group('Built on', select('Stock plane it is built on', s.donor || 'auto',
    [['auto', `Automatic: ${autoName}`], ...Object.keys(DONORS).map(k => [k, donorLabel(k)])], v => set(p => { p.donor = v; }, true)),
  html(`<span class="faint small">Your plane reuses this stock plane's systems, landing gear and weapon mounts (and its cockpit view, if you choose), and appears in hangars that offer it.</span>`)));

  host.append(paintSection(ctx, set));

  const est = a?.flight;
  host.append(group('Weight',
    row2(num('Empty weight', s.mass?.empty_kg || 0, (v, f) => set(p => { p.mass = { ...(p.mass || {}), empty_kg: v }; }, f, { inspector: false }), { unit: 'kg', step: 50, min: 0, digits: 0 }),
      num('Fuel', s.mass?.fuel_kg || 0, (v, f) => set(p => { p.mass = { ...(p.mass || {}), fuel_kg: v }; }, f, { inspector: false }), { unit: 'kg', step: 50, min: 0, digits: 0 })),
    html(`<span class="faint small">0 means "work it out from the shape"${est ? ` (now ${fmtKg(est.empty_mass_kg)} empty, ${fmtKg(est.fuel_mass_kg)} fuel)` : ''}.</span>`)));

  host.append(group('Fly-by-wire',
    row2(num('G limit', s.handling?.g_limit || 0, (v, f) => set(p => { p.handling = { ...(p.handling || {}), g_limit: v }; }, f, { inspector: false }), { unit: 'g', step: 0.5, min: 0, max: 20, digits: 1 }),
      num('Angle-of-attack limit', s.handling?.aoa_limit_deg || 0, (v, f) => set(p => { p.handling = { ...(p.handling || {}), aoa_limit_deg: v }; }, f, { inspector: false }), { unit: '°', step: 1, min: 0, max: 90, digits: 0 })),
    html(`<span class="faint small">0 uses the stock plane's limits. The game's fly-by-wire keeps to both: the G limit (4 to 12 g) and the angle-of-attack limit (10 to 60°).</span>`)));

  const sensors = s.sensors || {};
  const sensorBox = group(`Sensors and defences ${NOT_IN_GAME}`);
  [['radar', 'Radar'], ['irst', 'Infrared search and track'], ['targeting_pod', 'Targeting pod'], ['radar_warning', 'Radar warning receiver'], ['countermeasures', 'Flares and chaff'], ['jammer', 'Jammer']]
    .forEach(([k, label]) => sensorBox.append(check(label, sensors[k], v => set(p => { p.sensors = { ...(p.sensors || {}), [k]: v }; }, true))));
  sensorBox.append(html(`<span class="faint small">In the game your plane has the stock plane's sensors for now.</span>`));
  host.append(sensorBox);

  const resize = html(`<div class="insp-group"><span class="label-caps">Resize the whole plane</span>
      <div class="row2" style="align-items:end"><div class="numfield"><label class="label" for="rs">Scale by</label><div class="wrap"><input type="number" id="rs" value="1.1" step="0.05" min="0.2" max="5"><span class="unit">×</span></div></div>
      <button type="button" class="btn">Resize</button></div>
      <span class="faint small">Scales every part and position. Engine power stays the same.</span></div>`);
  resize.querySelector('button').addEventListener('click', () => {
    const f = parseFloat(resize.querySelector('input').value);
    if (Number.isFinite(f) && f > 0.1 && f < 10 && f !== 1) ctx.actions.resize(f);
  });
  host.append(resize);
}

// ---------------------------------------------------------------- paint and materials

const materialLabel = (m, i) => `${m?.name || `Colour ${i + 1}`} (${KIND_WORD[m?.kind] || 'paint'})`;

/** A colour chip for a palette entry (glass over a checker, light colours glowing). */
function swatch(m) {
  const c = m?.color || [0.6, 0.62, 0.64, 1];
  const a = m?.kind === 'glass' ? Math.max(0.2, Math.min(1, c[3] ?? 0.25)) : 1;
  return `<span class="swatch ${m?.kind === 'glass' ? 'see' : ''} ${m?.kind === 'light' ? 'glow' : ''}" style="--sw:rgba(${c.slice(0, 3).map(v => Math.round(clamp01(v) * 255)).join(',')},${a})" aria-hidden="true"></span>`;
}

/** The select value for a part's material id: the palette position, or '' (none). */
const materialValue = (spec, id) => { const i = id ? paletteOf(spec).findIndex(m => m && m.id === id) : -1; return i >= 0 ? String(i) : ''; };

/** The id for a chosen select value (a palette position), giving the entry an id when it has none; null for anything else. */
function pickMaterial(spec, v) {
  const m = /^\d+$/.test(String(v)) ? paletteOf(spec)[+v] : null;
  if (!m) return null;
  m.id ||= newMaterialId();
  return m.id;
}

/** A "Material" select: `none` first, then `extra` options, then the palette. `apply(value)` runs on a choice. */
function materialField(ctx, label, value, apply, { none = 'Plane paint', extra = [], hint = '', quiet = false } = {}) {
  const pal = paletteOf(ctx.spec);
  const el = select(label, value, [['', none], ...extra, ...pal.map((m, i) => [String(i), materialLabel(m, i)])], apply);
  if (!quiet && (hint || !pal.length)) {
    el.append(html(`<span class="faint small">${hint || 'Make more colours, glass and light colours in the plane\'s settings: click empty space in the view.'}</span>`));
  }
  return el;
}

/** How the palette entry at position i is used: parts pointing at it, and (converted pieces) the furthest position they use. */
function materialUse(spec, sourceMeshes, i) {
  const id = paletteOf(spec)[i]?.id;
  let parts = 0, lastKept = -1;
  const byId = o => { if (id && o?.material_id === id) parts++; };
  (spec.fuselages || []).forEach(byId);
  (spec.shapes || []).forEach(byId);
  for (const w of spec.wings || []) {
    byId(w);
    (w.surfaces || []).forEach(byId);
    for (const p of w.panels || []) { if (p.material === i) parts++; for (const s of p.surfaces || []) if (s.material === i) parts++; }
  }
  // Bodies kept from a converted plane point at the palette by position, in their saved mesh.
  for (const f of spec.fuselages || []) {
    const t = f.kind === 'mesh' ? sourceMeshes?.[f.mesh_index]?.materials : null;
    if (!Array.isArray(t)) continue;
    if (t.includes(i)) parts++;
    for (const v of t) lastKept = Math.max(lastKept, v);
  }
  return { parts, lastKept };
}

/** Removes palette entry i: parts using it fall back to the plane paint (a surface to its wing's); later positions move up. */
function removeMaterial(spec, i) {
  const pal = spec.materials || [];
  const id = pal[i]?.id;
  pal.splice(i, 1);
  const byId = o => { if (o && id && o.material_id === id) o.material_id = null; };
  const shift = v => (v === i ? null : v > i ? v - 1 : v);
  (spec.fuselages || []).forEach(byId);
  (spec.shapes || []).forEach(byId);
  for (const w of spec.wings || []) {
    byId(w);
    (w.surfaces || []).forEach(byId);
    for (const p of w.panels || []) {
      if (Number.isInteger(p.material)) p.material = shift(p.material);
      for (const s of p.surfaces || []) if (Number.isInteger(s.material)) s.material = shift(s.material);
    }
  }
}

const NEW_MATERIAL = {
  paint: n => ({ name: `Paint ${n}`, color: [0.8, 0.8, 0.78, 1], metallic: 0, smoothness: 0.3 }),
  glass: n => ({ name: `Glass ${n}`, color: [0.55, 0.65, 0.72, 0.25], metallic: 0.2, smoothness: 0.95 }),
  light: n => ({ name: `Light colour ${n}`, color: [1, 0.85, 0.5, 1], metallic: 0, smoothness: 0.5 }),
};

/** One-click plane paints: the colours real air forces and trainers use (and bare metal). */
const PAINT_SCHEMES = [
  { name: 'Air-superiority grey', color: [0.56, 0.59, 0.62], metallic: 0.1, smoothness: 0.35 },
  { name: 'Navy blue-grey', color: [0.34, 0.40, 0.48], metallic: 0.1, smoothness: 0.35 },
  { name: 'Dark grey', color: [0.27, 0.29, 0.31], metallic: 0.1, smoothness: 0.3 },
  { name: 'Desert tan', color: [0.74, 0.64, 0.47], metallic: 0, smoothness: 0.25 },
  { name: 'Olive drab', color: [0.33, 0.35, 0.22], metallic: 0, smoothness: 0.2 },
  { name: 'Forest green', color: [0.20, 0.30, 0.20], metallic: 0, smoothness: 0.25 },
  { name: 'Arctic white', color: [0.90, 0.91, 0.92], metallic: 0.05, smoothness: 0.45 },
  { name: 'Bare metal', color: [0.74, 0.75, 0.77], metallic: 0.85, smoothness: 0.7 },
  { name: 'Gloss black', color: [0.07, 0.07, 0.08], metallic: 0.2, smoothness: 0.8 },
  { name: 'Trainer red', color: [0.76, 0.13, 0.11], metallic: 0.05, smoothness: 0.55 },
  { name: 'Trainer yellow', color: [0.93, 0.71, 0.12], metallic: 0.05, smoothness: 0.55 },
];

/** The plane's "Paint and materials": the plane paint, the stock-livery switch and the palette with its editor. */
function paintSection(ctx, set) {
  const s = ctx.spec;
  const paint = s.paint || {};
  const g = group('Paint and materials');
  const pset = (fn, f) => set(p => { p.paint = { ...(p.paint || {}) }; fn(p.paint); }, f, { inspector: false });
  const schemes = html(`<div class="field"><span class="label">Quick schemes</span><div class="schemes" role="group" aria-label="Quick paint schemes">${PAINT_SCHEMES.map((sc, i) =>
    `<button type="button" class="scheme" data-i="${i}" title="${esc(sc.name)}" aria-label="Paint it ${esc(sc.name)}" style="--c:rgb(${sc.color.map(v => Math.round(v * 255)).join(',')})${sc.metallic > 0.5 ? ';--sheen:1' : ''}"></button>`).join('')}</div></div>`);
  schemes.querySelectorAll('.scheme').forEach(b => b.onclick = () => {
    const sc = PAINT_SCHEMES[+b.dataset.i];
    set(p => { p.paint = { ...(p.paint || {}), base_color: [...sc.color, 1], metallic: sc.metallic, smoothness: sc.smoothness, stock_livery: false }; }, true);
  });
  g.append(schemes);
  g.append(row2(colorField('Plane paint', paint.base_color || [0.6, 0.62, 0.64, 1], (c, f) => pset(pp => { pp.base_color = [...c, (pp.base_color || [])[3] ?? 1]; }, f)),
    html(`<span class="faint small" style="align-self:end">For every part without a material of its own${(s.materials || []).some(m => (m?.kind || 'paint') === 'paint') ? ' (parts with their own colours below keep them)' : ''}.</span>`)));
  g.append(row2(slider('Metallic', paint.metallic ?? 0, (v, f) => pset(pp => { pp.metallic = v; }, f)),
    slider('Smoothness', paint.smoothness ?? 0.3, (v, f) => pset(pp => { pp.smoothness = v; }, f), 'Rough (0 %) to glossy (100 %)')));
  g.append(check('Use the stock livery in the game instead of these colours', paint.stock_livery, v => set(p => { p.paint = { ...(p.paint || {}), stock_livery: v }; }, true)));
  g.append(html(`<span class="faint small">${paint.stock_livery
    ? 'The game paints the plane with the stock plane\'s liveries (and Workshop liveries), mapped on from above. The builder still shows your colours.'
    : 'The game paints the plane in these colours, glass and lights included.'}</span>`));

  const list = html(`<div class="insp-sub"><span class="label-caps">Colours, glass and lights</span><div class="mat-list" role="list"></div>
      <div class="btn-row"></div></div>`);
  const host = list.querySelector('.mat-list');
  const draw = () => {
    host.innerHTML = '';
    const pal = paletteOf(ctx.spec);
    if (openMaterial >= pal.length) openMaterial = -1;
    if (!pal.length) host.append(html(`<span class="muted small">None yet: every part wears the plane paint. Add paint, glass or a light colour, then pick it in a part's settings.</span>`));
    pal.forEach((m, i) => {
      const open = openMaterial === i;
      const row = html(`<div class="mat-item" role="listitem"><button type="button" class="mat-row" aria-expanded="${open}" title="${open ? 'Close' : 'Edit this colour'}">${swatch(m)}<span class="nm">${esc(m?.name || `Colour ${i + 1}`)}</span><span class="tag">${esc(KIND_WORD[m?.kind] || 'paint')}</span></button></div>`);
      row.querySelector('.mat-row').onclick = () => { openMaterial = open ? -1 : i; draw(); host.querySelectorAll('.mat-row')[i]?.focus(); };
      if (open) row.append(materialEditor(ctx, m, i));
      host.append(row);
    });
  };
  draw();
  const add = kind => ctx.change(spec => {
    const pal = (spec.materials ||= []);
    const n = pal.filter(m => (m?.kind || 'paint') === kind).length + 1;
    pal.push({ id: newMaterialId(), kind, ...NEW_MATERIAL[kind](n) });
    openMaterial = pal.length - 1;
  }, true);
  list.querySelector('.btn-row').append(button('Add paint', 'plus', () => add('paint')), button('Add glass', 'plus', () => add('glass')), button('Add light colour', 'plus', () => add('light')));
  g.append(list);
  return g;
}

/** The editor under an open palette row: name, kind, colour (and opacity for glass), metallic, smoothness, delete. */
function materialEditor(ctx, m, i) {
  const at = (fn, final = true) => ctx.change(spec => { const x = paletteOf(spec)[i]; if (x) fn(x, spec); }, final);
  const el = html('<div class="mat-edit"></div>');
  const kind = m.kind || 'paint';
  const color = m.color || [0.6, 0.62, 0.64, 1];
  el.append(text('Name', m.name, (v, f) => at(x => { x.name = v; }, f)));
  el.append(select('Kind', kind, MATERIAL_KINDS, v => at(x => {
    const c = x.color || [0.6, 0.62, 0.64, 1];
    x.kind = v;
    // Glass starts mostly see-through; paint and lights are solid.
    x.color = [c[0], c[1], c[2], v === 'glass' ? ((c[3] ?? 1) > 0.9 ? 0.3 : c[3]) : 1];
  })));
  el.append(row2(colorField('Colour', color, (c, f) => at(x => { x.color = [...c, (x.color || [])[3] ?? 1]; }, f)),
    kind === 'glass' ? slider('Opacity', color[3] ?? 0.25, (v, f) => at(x => { x.color = [...(x.color || color).slice(0, 3), v]; }, f), 'How solid the glass is: 0 % is clear (the view shows at least 20 %)') : null));
  el.append(row2(slider('Metallic', m.metallic ?? 0, (v, f) => at(x => { x.metallic = v; }, f)),
    slider('Smoothness', m.smoothness ?? 0.3, (v, f) => at(x => { x.smoothness = v; }, f), 'Rough (0 %) to glossy (100 %)')));
  if (m.finish) el.append(html(`<span class="faint small">Surface in Flyout: ${esc(m.finish)}. OpenPlane draws it as this plain colour.</span>`));
  const use = materialUse(ctx.spec, ctx.sourceMeshes, i);
  const kept = i <= use.lastKept;
  const del = button('Delete', 'trash', () => { openMaterial = -1; at((x, spec) => removeMaterial(spec, i)); },
    { title: kept ? 'Pieces kept from the converted plane find their colours by their place in this list, so only colours after theirs can be deleted.' : 'Delete this colour (parts using it go back to the plane paint)' });
  del.classList.add('danger');
  if (kept) del.disabled = true;
  el.append(html(`<span class="faint small">${use.parts ? `Used by ${use.parts} part${use.parts === 1 ? '' : 's'}.` : 'Not used by any part yet.'}${kept ? ' Pieces kept from the converted plane use this list, so it stays; you can still change its colour.' : ''}</span>`), btnRow(del));
  return el;
}

// ---------------------------------------------------------------- bodies

function transformGroup(item, set, { rotation = true, scale = null, positionLabel = 'Position' } = {}) {
  const g = group('Transform', vec3(positionLabel, item.position, (v, f) => set(it => { it.position = v; }, f)));
  if (rotation) g.append(vec3('Rotation', item.rotation_deg || [0, 0, 0], (v, f) => set(it => { it.rotation_deg = v; }, f), { unit: '°', step: 1, digits: 2 }));
  if (scale === 'vec') g.append(vec3('Scale', item.scale || [1, 1, 1], (v, f) => set(it => { it.scale = v; }, f), { unit: '×', step: 0.05, digits: 3 }));
  if (scale === 'one') g.append(num('Scale', item.scale ?? 1, (v, f) => set(it => { it.scale = v; }, f), { unit: '×', step: 0.05, min: 0.05, max: 20, digits: 3 }));
  return g;
}

/** The interior checkbox of a body or shape (deleted when off, as the saved file leaves it out). */
function interiorField(item, set) {
  return check('Interior piece', item.interior, v => set(it => { if (v) it.interior = true; else delete it.interior; }, true, { outline: true }), '(seen from the cockpit, no drag)');
}

function loftFields(host, ctx, f, set, nameField) {
  host.append(group('', nameField(), mirrorField(f, set, 'body (for twin booms or pods)')));
  host.append(group('Look', materialField(ctx, 'Material', materialValue(ctx.spec, f.material_id), v => set((it, spec) => { it.material_id = pickMaterial(spec, v); }, true)),
    interiorField(f, set)));
  host.append(transformGroup(f, set));
  const sideOptions = [['12', 'Low (12 sides)'], ['16', 'Medium (16)'], ['24', 'Smooth (24)'], ['32', 'Very smooth (32)'], ['48', 'Ultra (48)']];
  const sides = String(f.sides || 24);
  if (!sideOptions.some(o => o[0] === sides)) sideOptions.push([sides, `${sides} sides`]);
  sideOptions.sort((a, b) => +a[0] - +b[0]);
  host.append(group('Surface',
    row2(select('Sides around', sides, sideOptions, v => ctx.actions.setSides(parseInt(v, 10))),
      select('Smooth along the body', String(f.smooth || 0), [['0', 'Off'], ['1', 'Smooth (×2 rings)'], ['2', 'Smoother (×4)'], ['3', 'Smoothest (×8)']], v => set(it => { it.smooth = parseInt(v, 10); }, true))),
    html(`<span class="faint small">Smoothing curves the body through its rings (in the game too). Press <kbd>Tab</kbd> to edit the rings and their vertices.</span>`)));

  const list = html(`<div class="insp-group"><span class="label-caps">Rings (nose to tail)</span><div class="ring-list"></div>
      <div class="btn-row"><button type="button" class="btn small" data-add="nose">${icon('plus', 'sm')}Ring at nose</button><button type="button" class="btn small" data-add="tail">${icon('plus', 'sm')}Ring at tail</button></div></div>`);
  const rl = list.querySelector('.ring-list');
  const order = (f.rings || []).map((r, i) => ({ r, i })).sort((a, b) => (b.r.z - a.r.z) || (a.i - b.i));
  const selRings = ctx.edit?.key && ctx.edit.mode === 'ring' ? ctx.edit.sel : new Set();
  order.forEach(({ r, i }, n) => {
    const custom = Array.isArray(r.points) && r.points.length >= 3;
    const row = html(`<div class="ring-row" aria-current="${selRings.has(i)}">
        <div class="ring-head"><button type="button" class="pick" title="Select this ring in Edit mode">Ring ${n + 1}${n === 0 ? ' · nose' : n === order.length - 1 ? ' · tail' : ''}${custom ? ' · custom shape' : ''}</button>
          <button type="button" class="icon-btn" data-a="ins" title="Insert a ring behind this one" aria-label="Insert a ring behind ring ${n + 1}">${icon('plus', 'sm')}</button>
          <button type="button" class="icon-btn danger" data-a="rm" title="Remove this ring" aria-label="Remove ring ${n + 1}" ${(f.rings || []).length <= 2 ? 'disabled' : ''}>${icon('trash', 'sm')}</button></div>
      </div>`);
    const setRing = k => (v, fin) => set(it => {
      const ring = it.rings[i];
      if (custom && (k === 'width' || k === 'height')) {
        // Scale the custom shape to the new size.
        const idx = k === 'width' ? 0 : 1, cur = Math.max(1e-6, ring[k]);
        ring.points = ring.points.map(p => { const q = [...p]; q[idx] = q[idx] * v / cur; return q; });
      }
      ring[k] = v;
    }, fin, { inspector: false });
    const a = html(`<div class="row2"></div>`);
    a.append(num('Station (z)', r.z, setRing('z'), { unit: 'm', step: 0.05 }), num('Up / down (y)', r.y || 0, setRing('y'), { unit: 'm', step: 0.02 }));
    const b = html(`<div class="row3"></div>`);
    b.append(num('Width', r.width, setRing('width'), { unit: 'm', step: 0.02, min: 0 }), num('Height', r.height, setRing('height'), { unit: 'm', step: 0.02, min: 0 }),
      custom ? html(`<div class="numfield"><span class="label">Shape</span><button type="button" class="btn small" data-a="reset" title="Back to a round section">Round</button></div>`) : num('Roundness', r.roundness ?? 1, setRing('roundness'), { step: 0.05, min: 0, max: 1 }));
    row.append(a, b);
    row.querySelector('.pick').onclick = () => ctx.actions.selectRing(i);
    row.querySelector('[data-a=reset]')?.addEventListener('click', () => set(it => { delete it.rings[i].points; it.rings[i].roundness = 1; }, true));
    row.querySelector('[data-a=ins]').onclick = () => ctx.actions.insertRingAfter(n);
    row.querySelector('[data-a=rm]').onclick = () => set(it => { it.rings.splice(i, 1); }, true);
    rl.append(row);
  });
  list.querySelectorAll('[data-add]').forEach(b => b.onclick = () => ctx.actions.addEndRing(b.dataset.add === 'nose'));
  host.append(list);
}

function meshFields(host, ctx, f, set, nameField) {
  host.append(group('', nameField(), mirrorField(f, set, 'body')));
  host.append(note('This body keeps its exact shape from the original plane. Move, rotate or scale it here, or make it editable to change its vertices like any shape.'));
  const mk = button('Make editable', 'cube', () => ctx.actions.makeEditable(), { primary: true, title: 'Turn this body into a shape you can edit vertex by vertex' });
  host.append(group('', mk, html(`<span class="faint small">Big pieces (thousands of triangles) are slower to edit.</span>`)));
  host.append(group('Look', materialField(ctx, 'Material', materialValue(ctx.spec, f.material_id), v => set((it, spec) => { it.material_id = pickMaterial(spec, v); }, true),
    { none: 'Kept from the converted plane', hint: 'A material repaints the painted parts of this body; its glass stays glass.' }), interiorField(f, set)));
  host.append(transformGroup(f, set, { scale: 'one', positionLabel: 'Move by' }));
}

function shapeFields(host, ctx, s, set, nameField) {
  host.append(group('', nameField(), mirrorField(s, set, 'shape')));
  host.append(transformGroup(s, set, { scale: 'vec' }));
  const faces = shapeFaces(s);
  const tris = triangulate(faces).length / 3;
  const level = Math.max(0, Math.min(3, +s.subdivision || 0));
  // Older shapes say "glass" without a palette entry: that's the palette's first glass (or the default glass).
  const value = materialValue(ctx.spec, s.material_id) || (s.material === 'glass' ? 'glass' : '');
  const pickShape = v => set((it, spec) => {
    it.material_id = pickMaterial(spec, v);
    const m = it.material_id ? paletteOf(spec).find(x => x && x.id === it.material_id) : null;
    it.material = v === 'glass' || m?.kind === 'glass' ? 'glass' : 'skin';
  }, true, { outline: true });
  host.append(group('Surface',
    materialField(ctx, 'Material', value, pickShape, { extra: [['glass', 'Glass (the standard canopy glass)']] }),
    interiorField(s, set),
    select('Subdivision', String(level), [['0', 'Off'], ['1', 'Level 1'], ['2', 'Level 2'], ['3', 'Level 3']], v => set(it => { it.subdivision = parseInt(v, 10); }, true)),
    html(`<dl class="kv"><dt>Vertices</dt><dd>${(s.vertices || []).length}</dd><dt>Faces</dt><dd>${faces.length}</dd><dt>Triangles in game</dt><dd>${(tris * Math.pow(4, level)).toLocaleString()}</dd></dl>`),
    html(`<span class="faint small">Subdivision smooths the shape in the game too. Shapes count as body (weight, drag and balance) unless they are interior pieces.</span>`)));
  host.append(group('', html(`<span class="faint small">Press <kbd>Tab</kbd> to edit vertices, edges and faces: extrude, inset, loop cut, merge.</span>`)));
}

// ---------------------------------------------------------------- wings

function wingStats(w) {
  const panels = buildWing(w);
  let area = 0, minX = Infinity, maxX = -Infinity;
  for (const p of panels) {
    area += quadArea(p.root_leading, p.root_trailing, p.tip_trailing, p.tip_leading);
    for (const s of p.surfaces || []) area += quadArea(s.root_leading, s.root_trailing, s.tip_trailing, s.tip_leading);
    for (const c of [p.root_leading, p.root_trailing, p.tip_leading, p.tip_trailing]) { minX = Math.min(minX, c[0]); maxX = Math.max(maxX, c[0]); }
  }
  return { area, width: maxX - minX, panels: panels.length };
}

function wingFields(host, ctx, w, set, nameField) {
  const st = wingStats(w);
  host.append(group('', nameField(), html(`<dl class="kv"><dt>Area</dt><dd>${fmt(st.area, 2)} m²${st.panels > 1 ? ' (both sides)' : ''}</dd><dt>${w.vertical ? 'Width' : 'Tip to tip'}</dt><dd>${fmt(st.width, 2)} m</dd></dl>`),
    check('Fin (stands upright)', w.vertical, v => set(it => {
      it.vertical = v;
      if (v && it.mirror && Math.abs(it.root[0]) < 0.01) it.mirror = false;
      it.surfaces = (it.surfaces || []).map(s => ({ ...s, type: v && (s.type === 'aileron' || s.type === 'elevator') ? 'rudder' : !v && s.type === 'rudder' ? 'elevator' : s.type }));
    }, true, { outline: true })), mirrorField(w, set, w.vertical ? 'fin' : 'wing'),
    materialField(ctx, 'Material', materialValue(ctx.spec, w.material_id), v => set((it, spec) => { it.material_id = pickMaterial(spec, v); }, true))));
  host.append(group('Placement', vec3('Root, leading edge', w.root, (v, f) => set(it => { it.root = v; }, f))));
  host.append(group('Shape',
    row2(num(w.vertical ? 'Height' : 'Span (one side)', w.span, (v, f) => set(it => { it.span = v; }, f), { unit: 'm', step: 0.05, min: 0.05 }),
      num('Sweep', w.sweep_deg, (v, f) => set(it => { it.sweep_deg = v; }, f), { unit: '°', step: 0.5, min: -60, max: 75, digits: 1 })),
    row2(num('Root chord', w.root_chord, (v, f) => set(it => { it.root_chord = v; }, f), { unit: 'm', step: 0.05, min: 0.05 }),
      num('Tip chord', w.tip_chord, (v, f) => set(it => { it.tip_chord = v; }, f), { unit: 'm', step: 0.05, min: 0.02 })),
    row2(num(w.vertical ? 'Cant (lean out)' : 'Dihedral', w.dihedral_deg, (v, f) => set(it => { it.dihedral_deg = v; }, f), { unit: '°', step: 0.5, min: -45, max: 60, digits: 1 }),
      num('Incidence', w.incidence_deg, (v, f) => set(it => { it.incidence_deg = v; }, f), { unit: '°', step: 0.25, min: -10, max: 10, digits: 2 })),
    row2(num('Tip twist', w.twist_deg || 0, (v, f) => set(it => { it.twist_deg = v; }, f), { unit: '°', step: 0.25, min: -30, max: 30, digits: 2, title: 'Washout: a negative twist turns the tip nose-down so it stalls last' }),
      num('Thickness', (w.thickness_ratio ?? 0.12) * 100, (v, f) => set(it => { it.thickness_ratio = v / 100; }, f), { unit: '%', step: 1, min: 2, max: 30, digits: 0 })),
    html(`<span class="faint small">Press <kbd>Tab</kbd> to drag the four corners in the view.</span>`)));
  host.append(movingGroup(w, set));
  host.append(surfaceList(ctx, w, set));
}

/** Panel types that can turn as a whole: an all-moving tail, an all-moving V-tail or a turning fin. */
const MOVING_TYPES = [
  ['', 'No: a fixed panel'], ['elevator', 'Stabilator (pitch)'], ['elevon', 'Taileron (pitch + roll)'],
  ['ruddervator', 'All-moving V-tail (pitch + yaw)'], ['rudder', 'All-moving fin (yaw)'], ['aileron', 'Roll only'],
];

function movingGroup(w, set) {
  const m = w.moving && w.moving.type && w.moving.type !== 'none' ? w.moving : null;
  const box = html(`<div class="insp-group"><span class="label-caps">Moves as a whole</span></div>`);
  box.append(select('The whole panel turns', m ? m.type : '', MOVING_TYPES, v => set(it => {
    if (!v) delete it.moving; else it.moving = { type: v };
  }, true)));
  if (m) {
    const at = (fn, fin = true) => set(it => fn(it.moving), fin, { inspector: fin });
    box.append(travelEditor(m, ROLE_DEFAULTS[m.type] || { min: -20, max: 20, servo: 90, inputs: [] }, at));
    box.append(html(`<span class="faint small">The whole panel is the control surface, like a stabilator or an all-moving V-tail. Hinged strips below still work too.</span>`));
  }
  return box;
}

/** Travel, speed and inputs of a control surface (a strip or a whole panel); "at" applies a change to it. */
function travelEditor(s, def, at) {
  const out = html(`<div></div>`);
  const travel = html(`<div class="row3"></div>`);
  travel.append(num('Min', s.min_deg ?? def.min, (v, f) => at(x => { x.min_deg = v; }, f), { unit: '°', step: 1, min: -90, max: 90, digits: 0 }),
    num('Max', s.max_deg ?? def.max, (v, f) => at(x => { x.max_deg = v; }, f), { unit: '°', step: 1, min: -90, max: 90, digits: 0 }),
    num('Speed', s.servo_deg_per_sec ?? def.servo, (v, f) => at(x => { x.servo_deg_per_sec = v; }, f), { unit: '°/s', step: 5, min: 1, max: 500, digits: 0 }));
  out.append(html(`<span class="label-caps" style="margin-top:2px">Deflection and inputs</span>`), travel);
  const inputs = Array.isArray(s.inputs) ? s.inputs : def.inputs.map(([axis, gain]) => ({ axis, gain }));
  const custom = Array.isArray(s.inputs) || s.min_deg != null || s.max_deg != null || s.servo_deg_per_sec != null;
  const box = html(`<div class="insp-sub"></div>`);
  inputs.forEach((inp, k) => {
    const r = html(`<div class="input-row"></div>`);
    r.append(select(k ? '' : 'Input', inp.axis, AXES, v => at(x => { x.inputs = [...inputs.map(q => ({ ...q }))]; x.inputs[k].axis = v; })),
      num(k ? '' : 'Gain', inp.gain, (v, f) => at(x => { x.inputs = [...inputs.map(q => ({ ...q }))]; x.inputs[k].gain = v; }, f), { unit: '°', step: 1, min: -90, max: 90, digits: 0 }),
      html(`<button type="button" class="icon-btn" title="Remove this input" aria-label="Remove this input">${icon('x', 'sm')}</button>`));
    r.lastChild.onclick = () => at(x => { x.inputs = inputs.filter((_, j) => j !== k).map(q => ({ ...q })); });
    box.append(r);
  });
  const brow = html(`<div class="btn-row"></div>`);
  brow.append(button('Add input', 'plus', () => at(x => { x.inputs = [...inputs.map(q => ({ ...q })), { axis: 'pitch', gain: 10 }]; })));
  if (custom) brow.append(button('Defaults', 'refresh', () => at(x => { delete x.inputs; delete x.min_deg; delete x.max_deg; delete x.servo_deg_per_sec; })));
  box.append(brow);
  out.append(box);
  return out;
}

function surfaceList(ctx, w, set) {
  const surfs = html(`<div class="insp-group"><span class="label-caps">Control surfaces</span><div class="surf-list"></div>
      <div class="btn-row"><button type="button" class="btn small" data-add>${icon('plus', 'sm')}Add control surface</button></div>
      <span class="faint small">Percent runs from the root (0) to the tip (100). Inputs are for the right-hand side; the left one mirrors roll and yaw.</span></div>`);
  const sl = surfs.querySelector('.surf-list');
  const focus = ctx.focusSurface != null ? +ctx.focusSurface : -1;
  (w.surfaces || []).forEach((s, i) => {
    const def = ROLE_DEFAULTS[s.type] || { min: 0, max: 0, servo: 90, inputs: [] };
    const row = html(`<div class="surf-row ${focus === i ? 'sel' : ''}"><div class="surf-head"><span class="grow">${esc((SURFACE_TYPES.find(t => t[0] === s.type) || [s.type, s.type])[1])}</span>
        <button type="button" class="icon-btn" data-a="pick" title="Select this strip in the view (Edit mode)" aria-label="Select this control surface">${icon('cursor', 'sm')}</button>
        <button type="button" class="icon-btn danger" aria-label="Remove this control surface" title="Remove" data-a="rm">${icon('trash', 'sm')}</button></div></div>`);
    const at = (fn, fin = true) => set(it => fn(it.surfaces[i]), fin, { inspector: fin });
    const typeSel = select('Type', s.type, SURFACE_TYPES, v => at(x => { x.type = v; if (v === 'slat') x.edge = 'leading'; delete x.inputs; delete x.min_deg; delete x.max_deg; delete x.servo_deg_per_sec; }));
    const edgeSel = select('Edge', s.type === 'slat' ? 'leading' : s.edge || 'trailing', [['trailing', 'Trailing edge'], ['leading', 'Leading edge']], v => at(x => { x.edge = v; }));
    if (s.type === 'slat') edgeSel.querySelector('select').disabled = true;
    row.append(row2(typeSel, edgeSel));
    row.append(materialField(ctx, 'Material', materialValue(ctx.spec, s.material_id), v => set((it, spec) => { it.surfaces[i].material_id = pickMaterial(spec, v); }, true), { none: 'Same as the wing', quiet: true }));
    const nums = html(`<div class="row3"></div>`);
    nums.append(num('From', s.from_pct, (v, f) => at(x => { x.from_pct = v; }, f), { unit: '%', step: 1, min: 0, max: 100, digits: 0 }),
      num('To', s.to_pct, (v, f) => at(x => { x.to_pct = v; }, f), { unit: '%', step: 1, min: 0, max: 100, digits: 0 }),
      num('Chord', s.chord_pct, (v, f) => at(x => { x.chord_pct = v; }, f), { unit: '%', step: 1, min: 5, max: 60, digits: 0 }));
    row.append(nums);
    if (s.type !== 'none') row.append(travelEditor(s, def, at));
    row.querySelector('[data-a=rm]').onclick = () => set(it => { it.surfaces.splice(i, 1); }, true);
    row.querySelector('[data-a=pick]').onclick = () => ctx.actions.selectSurface(i);
    sl.append(row);
  });
  if (!(w.surfaces || []).length) sl.append(html(w.moving && w.moving.type && w.moving.type !== 'none'
    ? `<span class="muted small">None: the whole panel is the control (Moves as a whole, above).</span>`
    : `<span class="muted small">None yet. Wings need ailerons (or elevons), tails need elevators, fins need rudders.</span>`));
  surfs.querySelector('[data-add]').onclick = () => set(it => {
    const used = (it.surfaces || []).filter(s => s.edge !== 'leading' && s.type !== 'slat').reduce((m, s) => Math.max(m, s.to_pct), 0);
    const type = it.vertical ? 'rudder' : /tail|stab/i.test(it.name) ? 'elevator' : (it.surfaces || []).some(s => s.type === 'aileron') ? 'flap' : 'aileron';
    const from = used >= 95 ? 0 : used;
    it.surfaces = [...(it.surfaces || []), { type, edge: 'trailing', from_pct: from, to_pct: Math.min(100, Math.max(from + 20, 95)), chord_pct: 25 }];
  }, true);
  return surfs;
}

function panelFields(host, ctx, w, set, nameField) {
  host.append(group('', nameField(), mirrorField(w, set, 'panel')));
  host.append(note(`This ${w.vertical || /fin/i.test(w.name) ? 'fin' : 'wing'} keeps its exact panels from the original plane, control surfaces included. Press <kbd>Tab</kbd> to drag its corners; rotate or scale it in Object mode.`));
  host.append(group('Look', materialField(ctx, 'Material (whole wing)', materialValue(ctx.spec, w.material_id), v => set((it, spec) => { it.material_id = pickMaterial(spec, v); }, true),
    { none: 'Kept from the converted plane', hint: 'A material paints every panel of this wing; control surfaces with a colour of their own keep it.' })));
  host.append(group('Transform', vec3('Move by', w.offset, (v, f) => set(it => { it.offset = v; }, f)),
    num('Scale', w.scale ?? 1, (v, f) => set(it => { it.scale = v; }, f), { unit: '×', step: 0.05, min: 0.1, max: 10 })));
  const st = wingStats(w);
  const roles = (w.panels || []).flatMap(p => (p.surfaces || []).filter(s => s.role !== 'fixed').map(s => `${s.role} (${(s.inputs || []).map(i => `${i.axis} ${i.gain > 0 ? '+' : ''}${fmt(i.gain, 0)}°`).join(', ') || 'no input'})`));
  host.append(group('Details', html(`<dl class="kv"><dt>Area</dt><dd>${fmt(st.area, 2)} m²</dd><dt>Panels</dt><dd>${st.panels}</dd></dl>`),
    html(`<div class="panel-list">${roles.length ? roles.map(r => `<span>• ${esc(r)}</span>`).join('') : '<span>No moving surfaces.</span>'}</div>`)));
}

// ---------------------------------------------------------------- engines, gear, weapons

function engineFields(host, ctx, e, set, nameField) {
  host.append(group('', nameField(), mirrorField(e, set, 'engine')));
  const variant = e.variant || (e.type === 'prop' ? 'piston' : 'turbojet');
  const prop = variant === 'piston' || variant === 'turboprop' || variant === 'electric';
  const kind = select('Kind', variant, [['turbojet', 'Turbojet'], ['turbofan', 'Turbofan'], ['ramjet', 'Ramjet (flies as a jet)'], ['piston', 'Piston + propeller'], ['turboprop', 'Turboprop + propeller'], ['electric', 'Electric motor + propeller']],
    v => set(it => { it.variant = v; it.type = v === 'piston' || v === 'turboprop' || v === 'electric' ? 'prop' : 'jet'; it.source_type = null; }, true, { outline: true }));
  const g = group('Engine', kind);
  if (prop) {
    g.append(row2(num(variant === 'electric' ? 'Motor power' : 'Engine power', e.power_kw, (v, f) => set(it => { it.power_kw = v; it.estimated = false; }, f), { unit: 'kW', step: 10, min: 1, digits: 0 }),
      num('Propeller diameter', e.prop_diameter_m, (v, f) => set(it => { it.prop_diameter_m = v; }, f), { unit: 'm', step: 0.05, min: 0.3, max: 8 })));
    g.append(html(`<dl class="kv"><dt>Static thrust</dt><dd>${fmt(propThrustN(e.power_kw, e.prop_diameter_m) / 1000, 1)} kN</dd></dl>`));
    g.append(html(`<span class="faint small">From power and propeller size (momentum theory, 75 % efficient). Gearbox, blade count and rotors are ${NOT_IN_GAME}.</span>`));
  } else {
    const sizing = e.sizing === 'designed' ? 'designed' : 'thrust';
    g.append(select('Thrust', sizing, [['thrust', 'I know the thrust'], ['designed', 'Work it out from size and turbine temperature']], v => set(it => { it.sizing = v; if (v === 'designed') { it.turbine_temp_k ||= 1400; it.afterburner = (+it.afterburner_kn || 0) > (+it.thrust_kn || 0); } }, true)));
    if (sizing === 'designed') {
      g.append(row2(num('Diameter', e.diameter_m, (v, f) => set(it => { it.diameter_m = v; }, f), { unit: 'm', step: 0.02, min: 0.2, max: 4 }),
        num('Turbine temperature', e.turbine_temp_k || 1400, (v, f) => set(it => { it.turbine_temp_k = v; it.estimated = false; }, f), { unit: 'K', step: 10, min: 600, max: 2200, digits: 0 })));
      g.append(check('Afterburner', e.afterburner, v => set(it => { it.afterburner = v; }, true), '(about +55 % thrust)'));
      const t = engineThrust(e);
      g.append(html(`<dl class="kv"><dt>Dry thrust</dt><dd>${fmt(t.dry / 1000, 1)} kN</dd>${t.afterburner ? `<dt>Afterburner</dt><dd>${fmt(t.afterburner / 1000, 1)} kN</dd>` : ''}</dl>`));
      g.append(html(`<span class="faint small">Fitted to real engines: a Jumo 004 (0.74 m, 1,050 K) gives 8.8 kN and an AL-31F (1.28 m, 1,665 K) 76 kN. Hotter and bigger means more thrust.</span>`));
    } else {
      g.append(row2(num('Thrust', e.thrust_kn, (v, f) => set(it => { it.thrust_kn = v; it.estimated = false; }, f), { unit: 'kN', step: 0.5, min: 0.5, digits: 1 }),
        num('With afterburner', e.afterburner_kn || 0, (v, f) => set(it => { it.afterburner_kn = v; it.estimated = false; }, f), { unit: 'kN', step: 0.5, min: 0, digits: 1, hint: '0 = none' })));
      g.append(num('Diameter', e.diameter_m, (v, f) => set(it => { it.diameter_m = v; }, f), { unit: 'm', step: 0.02, min: 0.2, max: 4 }));
    }
    if (variant === 'ramjet') g.append(note('A ramjet gives no thrust standing still. The game has no ramjet, so it flies as a jet with this thrust.'));
  }
  if (e.estimated) g.append(html(`<span class="faint small">Thrust was estimated from the Flyout engine. Change it if you know better.</span>`));
  host.append(g);
  const startedCentred = Math.abs((e.position || [0])[0]) < 0.01;
  host.append(group('Placement', vec3('Position', e.position, (v, f) => set(it => {
    if (f && startedCentred && Math.abs(v[0]) >= 0.01 && !it.mirror) it.mirror = true; // a centred engine moved aside gets its twin
    it.position = v;
  }, f, { outline: true }))));
}

function gearFields(host, ctx, g, set, nameField) {
  host.append(group('', nameField(), select('Kind', g.kind, [['nose', 'Nose wheel'], ['main', 'Main wheels'], ['tail', 'Tail wheel'], ['other', 'Other']], v => set(it => { it.kind = v; if (v === 'nose') it.steerable = true; }, true, { outline: true })),
    mirrorField(g, set, 'wheel')));
  host.append(group('Placement and size', vec3('Mount point (top of the strut)', g.position, (v, f) => set(it => { it.position = v; }, f)),
    row2(num('Strut length', g.strut_length, (v, f) => set(it => { it.strut_length = v; }, f), { unit: 'm', step: 0.02, min: 0.05, hint: NOT_IN_GAME }),
      num('Wheel radius', g.wheel_radius, (v, f) => set(it => { it.wheel_radius = v; }, f), { unit: 'm', step: 0.01, min: 0.05, hint: NOT_IN_GAME })),
    num('Suspension travel', g.travel ?? 0.2, (v, f) => set(it => { it.travel = v; }, f), { unit: 'm', step: 0.01, min: 0, hint: NOT_IN_GAME })));
  host.append(group('Behaviour', check('Steers', g.steerable, v => set(it => { it.steerable = v; }, true)), check('Brakes', g.brakes, v => set(it => { it.brakes = v; }, true)),
    check('Folds up in flight', g.retractable, v => set(it => { it.retractable = v; }, true), NOT_IN_GAME),
    html(`<span class="faint small">In the game the stock plane's legs move to these wheels: the nose leg across and along the plane (at least 1 m ahead of the balance point), the main legs across only, so the plane rests and rotates on takeoff like the stock plane. Each leg steers and brakes as set here (a plane with no steering or no brakes at all keeps the stock plane's). Leg length and retraction stay the stock ones.</span>`)));
  host.append(group('', button('Auto-place all gear', 'magic', () => ctx.actions.autoGear()),
    html(`<span class="faint small">A nose wheel and two main wheels where they balance the plane, with struts long enough to clear the ground.</span>`)));
}

function hardpointFields(host, ctx, h, set, nameField) {
  host.append(group('', nameField(), mirrorField(h, set, 'station'), check('Inside a weapons bay', h.internal, v => set(it => { it.internal = v; }, true), '(keeps the stock plane\'s internal bay open)')));
  host.append(group('Placement', vec3('Position', h.position, (v, f) => set(it => { it.position = v; }, f))));
  const box = group(`Weapons it can carry ${NOT_IN_GAME}`);
  const allowed = new Set(h.allows || []);
  WEAPONS.filter(([k]) => (k !== 'tank' && k !== 'gun-pod') || allowed.has(k)).forEach(([k, label]) =>
    box.append(check(label, allowed.has(k), v => set(it => { const s = new Set(it.allows || []); v ? s.add(k) : s.delete(k); it.allows = [...s]; }, true))));
  box.append(html(`<span class="faint small">In the game the stock plane's weapon stations move onto your hardpoints (left and right in pairs, inner ones first), with the stock weapon choices. Stations that find no hardpoint aren't offered, and stores too heavy for your plane's weight aren't either. You pick the weapons in the loadout screen.</span>`));
  host.append(box);
}

function gunFields(host, ctx, g, set, nameField) {
  host.append(group('', nameField(), mirrorField(g, set, 'gun')));
  const cal = num('Calibre', g.caliber_mm, (v, f) => set(it => { it.caliber_mm = v; }, f, { outline: f }), { unit: 'mm', step: 0.1, min: 5, max: 40, digits: 1, hint: NOT_IN_GAME });
  const listId = fid();
  cal.querySelector('input').setAttribute('list', listId);
  cal.append(html(`<datalist id="${listId}">${[7.62, 12.7, 20, 23, 25, 27, 30].map(c => `<option value="${c}">`).join('')}</datalist>`));
  host.append(group('Gun', row2(cal, num('Rate of fire', g.rounds_per_minute, (v, f) => set(it => { it.rounds_per_minute = v; }, f), { unit: 'rpm', step: 100, min: 100, digits: 0 })),
    html(`<span class="faint small">In the game the stock plane's gun fires from here at this rate of fire (within a quarter and four times the stock gun's); its calibre stays the stock gun's for now.</span>`)));
  host.append(group('Placement', vec3('Position', g.position, (v, f) => set(it => { it.position = v; }, f)),
    vec3('Points towards', g.direction || [0, 0, 1], (v, f) => set(it => { it.direction = v; }, f), { unit: '', step: 0.05 })));
}

// ---------------------------------------------------------------- cockpit, instruments and lights

/** Three angles (degrees) for a [x, y, z, w] rotation, Unity's order like the other rotations here. */
function facingField(item, set, hint = '') {
  const q = item.rotation && item.rotation.length >= 4 ? item.rotation.map(Number) : [0, 0, 0, 1];
  const l = Math.hypot(...q) || 1;
  // Normalised first, so a rounded [-0.7071, 0, 0, 0.7071] reads as -90 and not -89.6.
  const e = quatToEuler(q.map(v => v / l)).map(v => Math.round(v * 10) / 10);
  return vec3('Facing', e, (v, f) => set(it => { it.rotation = eulerToQuat(v).map(x => Math.round(x * 1e6) / 1e6); }, f),
    { unit: '°', step: 1, digits: 1, names: ['Tilt (x)', 'Turn (y)', 'Roll (z)'], hint });
}

function cockpitFields(host, ctx, c, set) {
  const eye = eyeOf(c);
  const R = v => Math.round(v * 1e4) / 1e4;
  host.append(group('Seat', vec3('Where the pilot sits', c.position, (v, f) => set(it => {
    // A set eye moves with the seat.
    const old = it.position || [0, 0, 0];
    if (Array.isArray(it.eye)) it.eye = it.eye.map((e, i) => R(e + v[i] - old[i]));
    it.position = v;
  }, f))));
  host.append(group('Pilot\'s eye',
    vec3('Eye', eye, (v, f) => set(it => { it.eye = v; }, f), { hint: Array.isArray(c.eye) ? '' : 'Now 0.7 m above the seat (the default); it follows the seat until you move it.' }),
    btnRow(button('Reset eye', 'refresh', () => set(it => { it.eye = null; }, true), { title: 'Put the eye back 0.7 m above the seat' }),
      button('Cockpit view', 'cockpit', () => ctx.run('viewCockpit'), { title: 'Look forward from the eye', keys: 'Numpad 0' })),
    check('Fly from this cockpit in the game', c.own_view !== false, v => set(it => { it.own_view = v; }, true), '(untick to keep the stock plane\'s cockpit view)')));
  host.append(html(`<p class="muted small">In the game the view sits at the eye and you see this plane's own interior, instruments and canopy. Tick <strong>Interior piece</strong> on seats, panels and sticks so they show from the cockpit without adding drag. Add instruments and lights with <kbd>Shift</kbd>+<kbd>A</kbd>; a glass shape makes a canopy.</p>`));
}

function instrumentFields(host, ctx, n, set, nameField) {
  const kind = INSTRUMENT_KINDS[n.kind] ? n.kind : 'gauge';
  const dial = kind === 'gauge' || kind === 'compass' || kind === 'attitude';
  host.append(group('', nameField(), select('Kind', kind, Object.entries(INSTRUMENT_KINDS).map(([k, d]) => [k, d.label]), v => set(it => {
    const old = INSTRUMENT_KINDS[it.kind] || INSTRUMENT_KINDS.gauge, next = INSTRUMENT_KINDS[v];
    // A name or size left as it came follows the new kind.
    if (it.name === old.name) it.name = next.name;
    if (Math.abs((+it.size || 0) - old.size) < 1e-9) it.size = next.size;
    it.kind = v;
    if (v === 'gauge' && !it.reads) it.reads = 'airspeed';
    if (v === 'compass') it.reads = 'heading';
  }, true, { outline: true })), mirrorField(n, set, 'instrument')));

  const g = group('What it shows');
  if (kind === 'gauge') {
    g.append(select('Shows', n.reads || 'airspeed', READINGS, v => set(it => { it.reads = v; }, true, { outline: true })));
    g.append(row2(num('Dial starts at', n.min ?? 0, (v, f) => set(it => { it.min = v; }, f), { step: 1, digits: 2 }),
      num('Dial ends at', n.max ?? 1, (v, f) => set(it => { it.max = v; }, f), { step: 1, digits: 2 })));
    g.append(row2(num('Needle at the start', n.min_deg ?? 30, (v, f) => set(it => { it.min_deg = v; }, f), { unit: '°', step: 5, min: -360, max: 720, digits: 0 }),
      num('Needle at the end', n.max_deg ?? 330, (v, f) => set(it => { it.max_deg = v; }, f), { unit: '°', step: 5, min: -360, max: 720, digits: 0 })));
    g.append(html(`<span class="faint small">Needle angles go clockwise from 12 o'clock. Leave the dial at 0 to 1 to use the usual range of what it shows (for airspeed, 0 to 1,000 km/h).</span>`));
  } else if (kind === 'screen') {
    g.append(select('Shows', n.reads || '', SCREEN_PAGES, v => set(it => { if (v) it.reads = v; else delete it.reads; }, true, { outline: true })));
    g.append(html(`<span class="faint small">${esc(SCREEN_HELP[n.reads || ''] || INSTRUMENT_HELP.screen)}</span>`));
  } else if (kind === 'lever') {
    g.append(select('Moves with', n.reads || '', LEVER_MOTIONS, v => set(it => { if (v) it.reads = v; else delete it.reads; }, true, { outline: true })));
    g.append(html(`<span class="faint small">${esc(INSTRUMENT_HELP.lever)}</span>`));
  } else g.append(html(`<span class="faint small">${esc(INSTRUMENT_HELP[kind] || '')}</span>`));
  g.append(html(`<span class="faint small">OpenPlane draws this instrument in the game and moves it as you fly.</span>`));
  host.append(g);

  const eye = eyeOf(ctx.spec.cockpit);
  const face = kind !== 'lever' ? button('Face the pilot', 'cockpit', () => set(it => {
    // Tilt, then turn (no roll), so the face's +y points at the eye; straight ahead of the eye this is -90, 0, 0.
    const p = (it.position || [0, 0, 0]).map(Number);
    const d = [eye[0] - p[0], eye[1] - p[1], eye[2] - p[2]];
    const L = Math.hypot(...d);
    if (L < 1e-6) return;
    const u = d.map(v => v / L);
    const DEG = 180 / Math.PI;
    const tilt = -Math.acos(Math.max(-1, Math.min(1, u[1]))) * DEG;
    const turn = Math.hypot(u[0], u[2]) > 1e-9 ? Math.atan2(-u[0], -u[2]) * DEG : 0;
    it.rotation = eulerToQuat([tilt, turn, 0]).map(x => Math.round(x * 1e6) / 1e6);
  }, true), { title: ctx.spec.cockpit ? 'Turn the face towards the pilot\'s eye' : 'Add a cockpit first' }) : null;
  if (face && !ctx.spec.cockpit) face.disabled = true;
  host.append(group('Placement', vec3('Position', n.position, (v, f) => set(it => { it.position = v; }, f)),
    facingField(n, set, kind === 'lever' ? '0, 0, 0 points straight up. R turns it in the view.' : '−90, 0, 0 faces back along the plane, at the pilot. R turns it in the view.'),
    num(dial ? 'Face size' : kind === 'lever' ? 'Length' : 'Width', n.size ?? 0.08, (v, f) => set(it => { it.size = v; }, f), { unit: 'm', step: 0.005, min: 0.01, max: 2, digits: 3, title: dial ? 'Diameter of the face' : '' }),
    btnRow(face, button('Cockpit view', 'cockpit', () => ctx.run('viewCockpit'), { title: 'Look at it from the pilot\'s eye', keys: 'Numpad 0' }))));
}

const LIGHT_OPTIONS = [['nav', 'Navigation light (steady)'], ['strobe', 'Strobe (quick flashes)'], ['beacon', 'Beacon (slow blink)'], ['landing', 'Landing light (bright)'], ['strip', 'Light strip']];

function lightFields(host, ctx, l, set, nameField) {
  const kind = LIGHT_KINDS[l.kind] ? l.kind : 'nav';
  host.append(group('', nameField(), select('Kind', kind, LIGHT_OPTIONS, v => set(it => {
    const old = LIGHT_KINDS[it.kind] || LIGHT_KINDS.nav;
    if (it.name === old.name) it.name = LIGHT_KINDS[v].name;
    // A strip is a bar a little over half a metre long; a lamp is about 10 cm across.
    if (v === 'strip' && (+it.size || 0) < 0.2) it.size = 0.6;
    if (v !== 'strip' && (+it.size || 0) > 0.4) it.size = 0.1;
    it.kind = v;
  }, true, { outline: true })),
  check('Mirror on the other side', l.mirror, v => set(it => { it.mirror = v; }, true, { outline: true }), kind === 'nav' ? '(a matching left light; green and red swap)' : '(a matching left light)')));
  const presets = html('<div class="preset-row" role="group" aria-label="Colour presets"></div>');
  [['Green', LIGHT_COLORS.green], ['Red', LIGHT_COLORS.red], ['White', LIGHT_COLORS.white]]
    .forEach(([t, c]) => presets.append(button(t, '', () => set(it => { it.color = [...c]; }, true), { title: `Make it ${t.toLowerCase()}` })));
  host.append(group('Light',
    row2(colorField('Colour', l.color || [1, 1, 1, 1], (c, f) => set(it => { it.color = [...c, (it.color || [])[3] ?? 1]; }, f, { inspector: false })),
      num(kind === 'strip' ? 'Length' : 'Lamp size', l.size ?? 0.1, (v, f) => set(it => { it.size = v; }, f), { unit: 'm', step: 0.01, min: 0.01, max: 5, digits: 3 })),
    presets, html(`<span class="faint small">${esc(LIGHT_HELP[kind])} It glows in the game.</span>`)));
  host.append(group('Placement', vec3('Position', l.position, (v, f) => set(it => { it.position = v; }, f)),
    kind === 'strip' || kind === 'landing' ? facingField(l, set, kind === 'strip' ? 'A strip runs along its own x; R turns it in the view.' : 'R turns it in the view.') : null));
}

// ---------------------------------------------------------------- reference images

function referenceFields(host, ctx, r, set, nameField) {
  host.append(group('', nameField(), html(`<img src="/api/references/${encodeURIComponent(r.image)}" alt="" style="width:100%;max-height:120px;object-fit:contain;background:var(--viewport-bg);border:1px solid var(--line);border-radius:3px">`)));
  host.append(group('Placement',
    select('Seen from', r.view || 'top', [['top', 'Top'], ['side', 'Side (from the right)'], ['front', 'Front']], v => set(it => { it.view = v; }, true, { outline: true })),
    row2(num(r.view === 'side' ? 'Along (z)' : 'Across (x)', (r.center || [0, 0])[0], (v, f) => set(it => { it.center = [v, (it.center || [0, 0])[1]]; }, f), { unit: 'm', step: 0.05 }),
      num(r.view === 'top' ? 'Along (z)' : 'Up (y)', (r.center || [0, 0])[1], (v, f) => set(it => { it.center = [(it.center || [0, 0])[0], v]; }, f), { unit: 'm', step: 0.05 })),
    row2(num('Width', r.width_m, (v, f) => set(it => { it.width_m = v; }, f), { unit: 'm', step: 0.1, min: 0.1, max: 500 }),
      num('Rotation', r.rotation_deg || 0, (v, f) => set(it => { it.rotation_deg = v; }, f), { unit: '°', step: 0.5, digits: 1 }))));
  const op = html(`<label class="slider-field"><span>Opacity</span><span class="mono">${Math.round((r.opacity ?? 0.5) * 100)}%</span><input type="range" min="5" max="100" value="${Math.round((r.opacity ?? 0.5) * 100)}"></label>`);
  const rng = op.querySelector('input');
  rng.addEventListener('input', () => { op.querySelector('.mono').textContent = `${rng.value}%`; set(it => { it.opacity = rng.value / 100; }, false, { inspector: false }); });
  rng.addEventListener('change', () => set(it => { it.opacity = rng.value / 100; }, true, { inspector: false }));
  host.append(group('Show', op, check('Visible', r.visible !== false, v => set(it => { it.visible = v; }, true, { outline: true })),
    check('Locked', r.locked, v => set(it => { it.locked = v; }, true, { outline: true }), '(can\'t be picked in the view)')));
  host.append(group('Scale from a known length', button('Set scale…', 'measure', () => ctx.actions.calibrate(), { primary: true }),
    html(`<span class="faint small">Click two points on the picture a known distance apart (say, the wingtips), then type that distance. The picture shows when you look straight from its side: Top, Side (Right) or Front.</span>`)));
  host.append(note('Reference pictures are saved with your plane, but left out when you share it unless you tick "Include reference images".'));
}
