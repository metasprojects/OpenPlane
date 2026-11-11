// Build: the 3D builder. Picks a template (or opens a saved plane / draft), then edits it Blender-style: Object and
// Edit modes, G/R/S with axis locks and typed numbers, multi and box select, hide/lock, numbered views, quad view,
// X-ray, snapping, X-mirror, proportional editing, a labelled tool shelf, a right-click menu and live flight readouts.
// Every action is a command with a label and a shortcut, so the shelf, header, menus, keys and cheat sheet agree.
import { api } from '../api.js';
import { html, esc, fmt, fmtKg, toast, showError, debounce, modal, confirmDialog, promptDialog, dialogOpen, timeAgo, donorLabel } from '../ui.js';
import { icon } from '../icons.js';
import { go, launchGame } from '../app.js';
import { Viewport } from '../builder/viewport.js';
import { renderInspector } from '../builder/inspector.js';
import { renderOutliner, startRename } from '../builder/outliner.js';
import { Modal } from '../builder/transform.js';
import { showMenu, closeMenu, menuOpen, cheatSheet, firstRunTips } from '../builder/menus.js';
import {
  LISTS, KIND_LABEL, keyOf, parseKey, getItem, allKeys, itemName, originOf, medianOf, applyToPart, duplicatePart, addPart, removePart, xfPoint, eyeOf,
} from '../builder/parts.js';
import {
  editModes, buildElements, selectedVerts, allIds, elementPoint, convertSelection, mirrorPartner, overlayFor, writeVerts,
  extrudeRing, ringBetween, applyPreset, edgeRing, MODE_LABEL, countOf,
} from '../builder/editmode.js';
import { vec, meshCenter, panelsPivot, orderedRings, clampSides, ringOffsets, resampleRing, fuselageTransform, shapeTransform, quadArea } from '../builder/geometry.js';
import * as D from '../builder/defaults.js';
import { reportGroups, stabilityPill } from './convert.js';
import { listDrafts, renderDrafts, newDraftId } from '../drafts.js';

export async function render(main, args) {
  const target = args[0];
  if (!target) return templatePicker(main);
  let doc;
  try {
    if (target === 'draft') {
      // #/build/draft/<draft id>; an old link without an id opens the newest draft.
      let draftId = args[1];
      if (!draftId) draftId = (await listDrafts())[0]?.id;
      const r = draftId ? await api.get(`/api/drafts/${encodeURIComponent(draftId)}`) : { draft: null };
      if (!r.draft) { go('#/build'); return {}; }
      doc = { draftId: r.draft.id, planeId: r.draft.plane_id, spec: r.draft.spec, meshSet: r.draft.mesh_set, sourceMeshes: r.source_meshes || [], report: [], fromDraft: true, savedJson: null };
      if (doc.planeId) {
        try { const p = await api.get(`/api/planes/${doc.planeId}`); doc.report = p.report; doc.savedJson = JSON.stringify(normalize(p.spec)); doc.fileModified = p.modified; doc.fileName = p.file_name; }
        catch { doc.planeId = null; }
      }
    } else {
      const p = await api.get(`/api/planes/${target}`);
      doc = { draftId: p.id, planeId: p.id, spec: p.spec, meshSet: p.mesh_set, sourceMeshes: p.source_meshes, report: p.report, note: p.note, savedJson: JSON.stringify(normalize(p.spec)), fileModified: p.modified, fileName: p.file_name };
      // This plane's own draft (each plane keeps its own, so opening another plane never loses it).
      const d = await api.get(`/api/drafts/${encodeURIComponent(p.id)}`).catch(() => null);
      if (d?.draft && JSON.stringify(normalize(d.draft.spec)) !== doc.savedJson) {
        const restore = await confirmDialog({
          title: 'Continue where you left off?',
          body: `You have unsaved changes to "${esc(d.draft.spec.name)}" from ${esc(timeAgo(d.draft.saved_at))}.`,
          confirm: 'Continue editing them', cancel: 'Open the saved plane',
        });
        if (restore) { doc.spec = d.draft.spec; doc.meshSet = d.draft.mesh_set; doc.sourceMeshes = d.source_meshes; doc.fromDraft = true; }
        else await api.del(`/api/drafts/${encodeURIComponent(p.id)}`).catch(() => {});
      } else if (d?.draft) await api.del(`/api/drafts/${encodeURIComponent(p.id)}`).catch(() => {});
    }
  } catch (e) {
    main.replaceChildren(html(`<div class="page"><div class="notice bad">${icon('warn')}<div><strong>This plane couldn't be opened.</strong><div>${esc(e.message)}</div>
      <div class="btn-row" style="margin-top:10px"><button type="button" class="btn" data-go="#/">Back to my planes</button></div></div></div></div>`));
    main.querySelector('[data-go]').onclick = () => go('#/');
    return {};
  }
  doc.spec = normalize(doc.spec);
  return builder(main, doc);
}

/** The v2 lists the page relies on (the server fills them too; this keeps older drafts working). */
function normalize(spec) {
  spec.shapes ||= [];
  spec.references ||= [];
  spec.editor ||= { hidden: [], locked: [] };
  spec.editor.hidden ||= [];
  spec.editor.locked ||= [];
  return spec;
}

// ---------------------------------------------------------------- template picker

async function templatePicker(main) {
  const page = html(`<div class="page">
    <div class="page-head"><div><h1>Build a new plane</h1><p>Pick a starting point. Every template is a complete plane that flies; change anything you like.</p></div>
      <button type="button" class="btn" id="open-saved">${icon('folder')}Open a saved plane</button></div>
    <div id="drafts"></div>
    <div class="template-grid" id="templates"><div class="loading-page"><span class="spinner"></span></div></div>
  </div>`);
  main.append(page);
  page.querySelector('#open-saved').onclick = () => go('#/');
  try {
    const [templates] = await Promise.all([api.get('/api/templates'), renderDrafts(page.querySelector('#drafts'), go)]);
    const grid = page.querySelector('#templates');
    grid.replaceChildren(...templates.map(t => {
      const c = html(`<button type="button" class="card template-card">
          <span class="thumb ${t.id === 'blank' ? 'blank' : ''}">${t.id === 'blank' ? icon('plus', 'big') : `<img src="/api/templates/${t.id}/thumb" alt="">`}</span>
          <span class="body"><strong>${esc(t.name)}</strong><span>${esc(t.description)}</span></span>
        </button>`);
      c.onclick = () => startFromTemplate(t.id);
      return c;
    }));
  } catch (e) { showError(e); }
  return { title: 'Build' };
}

async function startFromTemplate(id) {
  try {
    // Every new plane gets its own draft, so starting one never replaces unsaved work on another.
    const r = await api.get(`/api/templates/${id}`);
    const draftId = newDraftId();
    await api.put(`/api/drafts/${draftId}`, { spec: r.spec, mesh_set: '', plane_id: null });
    go(`#/build/draft/${draftId}`);
  } catch (e) { showError(e); }
}

// ---------------------------------------------------------------- the builder

const TOOLS = [
  { id: 'select', label: 'Select', icon: 'select', keys: 'W', title: 'Select (W): click parts, drag a box' },
  { id: 'move', label: 'Move', icon: 'move', keys: 'G', title: 'Move tool: drag the arrows (G moves with the mouse)' },
  { id: 'rotate', label: 'Rotate', icon: 'rotate', keys: 'R', title: 'Rotate tool: drag the rings (R rotates with the mouse)' },
  { id: 'scale', label: 'Scale', icon: 'scale', keys: 'S', title: 'Scale tool: drag the handles (S scales with the mouse)' },
];
const UNDO_BUDGET = 120e6; // characters of undo history

function builder(main, doc) {
  const S = {
    planeId: doc.planeId || null,
    fileName: doc.fileName || null,   // the plane's file, to find what the game reported about it
    gameReport: null,                 // what the mod did with it the last time the game started
    fileModified: doc.fileModified || null,   // the file's time when opened or last saved, to notice changes made elsewhere
    draftId: doc.draftId || doc.planeId || newDraftId(),
    spec: doc.spec,
    meshSet: doc.meshSet || '',
    sourceMeshes: doc.sourceMeshes || [],
    report: doc.report || [],
    savedJson: doc.savedJson,
    selected: [], active: null,
    mode: 'object', edit: null, lastElemMode: {},
    tool: 'select',
    view: { wire: false, xray: false, quad: false, snap: false, mirrorX: true, prop: false, propRadius: 1, markers: true, measure: false },
    analysis: null, past: [], future: [], current: JSON.stringify(doc.spec),
    modal: null, pending: null, gizmoSession: null, press: null, boxMode: false,
    destroyed: false, hadParts: false, saving: false, fixedOpen: false, status: '',
  };

  const root = html(`<div class="builder" data-mode="object">
    <div class="b-top">
      <button type="button" class="icon-btn" data-a="back" title="My planes" aria-label="Back to my planes">${icon('back')}</button>
      <label class="sr-only" for="plane-name">Plane name</label>
      <input type="text" id="plane-name" class="name-input" autocomplete="off">
      <span class="save-state" id="save-state" aria-live="polite"></span>
      <span class="grow"></span>
      <button type="button" class="icon-btn" data-cmd="undo" aria-label="Undo">${icon('undo')}</button>
      <button type="button" class="icon-btn" data-cmd="redo" aria-label="Redo">${icon('redo')}</button>
      <span class="sep"></span>
      <button type="button" class="btn" data-a="saveas">Save as…</button>
      <button type="button" class="btn primary" data-a="save" title="Save (Ctrl+S)">${icon('save', 'sm')}Save</button>
      <button type="button" class="btn" data-a="fly" title="Save, then start Nuclear Option straight into a flight in this plane">${icon('play', 'sm')}Test fly</button>
    </div>
    <div class="b-body">
      <div class="b-vpcol">
        <div class="vp-header" id="vp-header" role="toolbar" aria-label="View and mode"></div>
        <div class="vp-area">
          <div class="toolshelf" id="toolshelf" role="toolbar" aria-label="Tools" aria-orientation="vertical"></div>
          <div class="viewport-wrap" id="vp">
            <div class="vp-overlay vp-viewname" id="vp-viewname"></div>
            <div class="xf-readout" id="xf-readout" hidden></div>
            <div class="vp-overlay vp-legend"><button type="button" class="vp-chip mk" data-a="markers" aria-pressed="true" title="Show or hide the balance markers"><span class="cg"></span>Centre of gravity</button><button type="button" class="vp-chip mk" data-a="markers" aria-pressed="true" title="Show or hide the balance markers"><span class="np"></span>Neutral point</button></div>
            <div class="vp-empty" id="vp-empty" hidden><div><strong>An empty plane</strong><p style="margin-top:6px">Press <kbd>Shift</kbd>+<kbd>A</kbd> or use the + buttons in the outliner to add a fuselage, wings and an engine. The checks below say what's still missing.</p></div></div>
            <div class="checks-drawer" id="checks" hidden></div>
          </div>
        </div>
      </div>
      <div class="b-side" id="side">
        <section class="outliner" aria-label="Outliner"><div class="pane-head"><h2>Outliner</h2><button type="button" class="icon-btn" data-cmd="add" title="Add a part (Shift+A)" aria-label="Add a part">${icon('plus', 'sm')}</button></div><div class="tree" id="outliner"></div></section>
        <div class="side-split" id="side-split" role="separator" aria-orientation="horizontal" title="Drag to resize"></div>
        <section class="props" aria-label="Properties"><div class="pane-head"><h2>Properties</h2></div><div class="inspector" id="inspector"></div></section>
      </div>
    </div>
    <div class="readout-bar" id="readouts"></div>
    <div class="statusbar" id="statusbar" aria-live="polite"></div>
  </div>`);
  main.replaceChildren(root);
  main.style.overflow = 'hidden';
  const $ = s => root.querySelector(s);
  const vpWrap = $('#vp');

  // ------------------------------------------------------------ commands

  const C = {};
  const cmd = (id, o) => { C[id] = { id, ...o }; };
  const run = (id, ...a) => { const c = C[id]; if (!c) return; if (c.enabled && !c.enabled()) { if (c.why) say(c.why()); return; } c.run(...a); };
  const tip = id => { const c = C[id]; return c ? `${c.label}${c.keys ? ` (${c.keys})` : ''}` : ''; };

  const inEdit = () => S.mode === 'edit' && !!S.edit;
  const editKind = () => (S.edit ? parseKey(S.edit.key).kind : null);
  const movable = () => S.selected.filter(k => k !== 'plane' && !isLocked(k) && !isHidden(k));
  const hasSel = () => (inEdit() ? S.edit.sel.size > 0 : movable().length > 0);
  const hasParts = () => ['fuselages', 'wings', 'engines', 'shapes'].some(k => (S.spec[k] || []).length);
  const isHidden = k => S.spec.editor.hidden.includes(k);
  const isLocked = k => S.spec.editor.locked.includes(k) || (k.startsWith('reference:') && !!getItem(S.spec, k)?.locked);
  const lockedKeys = () => S.spec.editor.locked;
  const isFixedMesh = k => !!k && k.startsWith('fuselage:') && getItem(S.spec, k)?.kind === 'mesh';
  const isParamWing = () => inEdit() && editKind() === 'wing' && getItem(S.spec, S.edit.key)?.kind !== 'panels';

  cmd('undo', { label: 'Undo', group: 'General', keys: 'Ctrl+Z', icon: 'undo', run: () => undo() });
  cmd('redo', { label: 'Redo', group: 'General', keys: 'Ctrl+Shift+Z / Ctrl+Y', icon: 'redo', run: () => redo() });
  cmd('save', { label: 'Save', group: 'General', keys: 'Ctrl+S', icon: 'save', run: () => save() });
  cmd('help', { label: 'Keyboard and mouse cheat sheet', group: 'General', keys: '? / F1', icon: 'help', run: () => cheatSheet(Object.values(C)) });
  cmd('add', { label: 'Add a part', group: 'Object', keys: 'Shift+A', icon: 'plus', run: (x, y) => addMenu(x, y) });
  cmd('toggleEdit', { label: 'Object / Edit mode', group: 'Modes', keys: 'Tab', icon: 'edit', run: () => toggleEdit() });
  cmd('tool-select', { label: 'Select tool', group: 'Tools', keys: 'W', icon: 'select', run: () => setTool('select') });
  cmd('grab', { label: 'Move', group: 'Transform', keys: 'G', icon: 'move', enabled: hasSel, why: () => 'Select something to move first.', run: () => startTransform('translate') });
  cmd('rotate', { label: 'Rotate', group: 'Transform', keys: 'R', icon: 'rotate', enabled: hasSel, why: () => 'Select something to rotate first.', run: () => startTransform('rotate') });
  cmd('scale', { label: 'Scale', group: 'Transform', keys: 'S', icon: 'scale', enabled: hasSel, why: () => 'Select something to scale first.', run: () => startTransform('scale') });
  cmd('axisInfo', { label: 'Lock to an axis while moving', group: 'Transform', keys: 'X / Y / Z', run: () => {} });
  cmd('planeInfo', { label: 'Lock to a plane (all but one axis)', group: 'Transform', keys: 'Shift+X / Shift+Y / Shift+Z', run: () => {} });
  cmd('typeInfo', { label: 'Exact value while transforming', group: 'Transform', keys: 'type a number', run: () => {} });
  cmd('snapHold', { label: 'Snap to the grid while held', group: 'Transform', keys: 'Ctrl', run: () => {} });
  cmd('selectAll', { label: 'Select all', group: 'Select', keys: 'A', icon: 'select', run: () => selectAll() });
  cmd('selectNone', { label: 'Select none', group: 'Select', keys: 'Alt+A', run: () => selectNone() });
  cmd('selectInvert', { label: 'Invert selection', group: 'Select', keys: 'Ctrl+I', run: () => selectInvert() });
  cmd('boxSelect', { label: 'Box select', group: 'Select', keys: 'B', icon: 'select', run: () => { S.boxMode = true; say('Box select: drag a box over what you want. Shift adds, Ctrl removes.'); } });
  cmd('duplicate', { label: 'Duplicate', group: 'Object', keys: 'Shift+D', icon: 'copy', enabled: () => !inEdit() && movable().some(k => !k.startsWith('cockpit')), run: () => duplicate() });
  cmd('delete', { label: 'Delete', group: 'Object', keys: 'X / Delete', icon: 'trash', enabled: () => (inEdit() ? S.edit.sel.size > 0 : S.selected.some(k => k !== 'plane')), why: () => 'Select something to delete first.', run: (x, y) => del(x, y) });
  cmd('hide', { label: 'Hide selected', group: 'Object', keys: 'H', icon: 'eyeOff', enabled: () => !inEdit() && S.selected.some(k => k !== 'plane'), run: () => hideSelected() });
  cmd('hideOthers', { label: 'Hide the others', group: 'Object', keys: 'Shift+H', run: () => hideOthers() });
  cmd('unhide', { label: 'Show everything', group: 'Object', keys: 'Alt+H', icon: 'eye', run: () => unhideAll() });
  cmd('rename', { label: 'Rename', group: 'Object', keys: 'F2', icon: 'edit', enabled: () => !!S.active && S.active !== 'cockpit:cockpit', run: () => startRename($('#outliner'), S.active) });
  cmd('makeEditable', { label: 'Make editable', group: 'Object', icon: 'cube', enabled: () => isFixedMesh(S.active), run: () => makeEditable() });
  cmd('mirrorPart', { label: 'Mirror on the other side', group: 'Object', icon: 'mirror', enabled: () => !!S.active && 'mirror' in (getItem(S.spec, S.active) || {}), run: () => change(s => { const it = getItem(s, S.active); it.mirror = !it.mirror; }, true, { outline: true }) });
  cmd('frameSel', { label: 'Frame selected', group: 'View', keys: 'F / Numpad .', icon: 'frame', run: () => frameSelected() });
  cmd('frameAll', { label: 'Frame all', group: 'View', keys: 'Home', icon: 'frame', run: () => vp.frameBox(null) });
  cmd('viewFront', { label: 'Front view', group: 'View', keys: 'Numpad 1', run: () => vp.setView('front') });
  cmd('viewBack', { label: 'Back view', group: 'View', keys: 'Ctrl+Numpad 1', run: () => vp.setView('back') });
  cmd('viewRight', { label: 'Right view', group: 'View', keys: 'Numpad 3', run: () => vp.setView('right') });
  cmd('viewLeft', { label: 'Left view', group: 'View', keys: 'Ctrl+Numpad 3', run: () => vp.setView('left') });
  cmd('viewTop', { label: 'Top view', group: 'View', keys: 'Numpad 7', run: () => vp.setView('top') });
  cmd('viewBottom', { label: 'Bottom view', group: 'View', keys: 'Ctrl+Numpad 7', run: () => vp.setView('bottom') });
  cmd('viewUser', { label: '3D view', group: 'View', icon: 'cube', run: () => { const r = vp.activeRig; r.setView('user'); r.orthographic = false; r.autoOrtho = false; r.update(); vp.frameBox(null); } });
  cmd('viewCockpit', { label: 'Cockpit view (from the pilot\'s eye)', group: 'View', keys: 'Numpad 0', icon: 'cockpit', run: () => cockpitView() });
  cmd('ortho', { label: 'Perspective / orthographic', group: 'View', keys: 'Numpad 5', icon: 'grid', run: () => vp.toggleOrtho() });
  cmd('quad', { label: 'Quad view', group: 'View', keys: 'Ctrl+Alt+Q', icon: 'quad', run: () => setView({ quad: !S.view.quad }) });
  cmd('xray', { label: 'X-ray', group: 'View', keys: 'Alt+Z', icon: 'xray', run: () => setView({ xray: !S.view.xray }) });
  cmd('wire', { label: 'Wireframe', group: 'View', keys: 'Shift+Z', icon: 'wire', run: () => setView({ wire: !S.view.wire }) });
  cmd('measure', { label: 'Measure the selection', group: 'View', keys: 'Shift+M', icon: 'measure', run: () => setView({ measure: !S.view.measure }) });
  cmd('markers', { label: 'Balance markers', group: 'View', icon: 'eye', run: () => setView({ markers: !S.view.markers }) });
  cmd('snap', { label: 'Snapping', group: 'Transform', keys: 'Shift+Tab', icon: 'magnet', run: () => setView({ snap: !S.view.snap }) });
  cmd('mirrorX', { label: 'X mirror (edits mirror across the centre line)', group: 'Edit', icon: 'mirror', run: () => setView({ mirrorX: !S.view.mirrorX }) });
  cmd('prop', { label: 'Proportional editing', group: 'Edit', keys: 'O', icon: 'prop', run: () => setView({ prop: !S.view.prop }) });
  cmd('modeVert', { label: 'Vertex select', group: 'Edit', keys: '1', icon: 'vert', run: () => setElemMode('vert') });
  cmd('modeEdge', { label: 'Edge select (ring select on a fuselage)', group: 'Edit', keys: '2', icon: 'edge', run: () => setElemMode(editKind() === 'fuselage' ? 'ring' : 'edge') });
  cmd('modeFace', { label: 'Face select (strips on a wing)', group: 'Edit', keys: '3', icon: 'face', run: () => setElemMode(editKind() === 'fuselage' ? 'ring' : 'face') });
  cmd('extrude', { label: 'Extrude', group: 'Edit', keys: 'E', icon: 'extrude', enabled: () => inEdit() && editKind() !== 'wing' && S.edit.sel.size > 0, why: () => 'Select a ring (fuselage) or faces (shape) first.', run: () => extrude() });
  cmd('inset', { label: 'Inset faces', group: 'Edit', keys: 'I', icon: 'inset', enabled: () => inEdit() && editKind() === 'shape' && S.edit.sel.size > 0, why: () => 'Select faces of a shape first.', run: () => shapeOp('inset') });
  cmd('loopcut', { label: 'Loop cut', group: 'Edit', keys: 'Ctrl+R', icon: 'loopcut', enabled: () => inEdit() && editKind() !== 'wing', why: () => 'Loop cut works on a fuselage or a shape in Edit mode.', run: () => startLoopCut() });
  cmd('merge', { label: 'Merge vertices', group: 'Edit', keys: 'M', icon: 'merge', enabled: () => inEdit() && editKind() === 'shape' && S.edit.sel.size > 0, run: (x, y) => mergeMenu(x, y) });
  cmd('subdivideApply', { label: 'Subdivide', group: 'Edit', icon: 'smooth', enabled: () => inEdit() && editKind() === 'shape', run: () => shapeOp('subdivide') });
  cmd('recalc', { label: 'Make faces point outwards', group: 'Edit', keys: 'Shift+N', icon: 'refresh', enabled: () => inEdit() && editKind() === 'shape', run: () => shapeOp('recalc_outward') });
  cmd('flip', { label: 'Flip faces', group: 'Edit', icon: 'mirror', enabled: () => inEdit() && editKind() === 'shape' && S.edit.sel.size > 0, run: () => shapeOp('flip') });
  cmd('wingSweep', { label: 'Sweep the wing', group: 'Edit', icon: 'wing', enabled: () => isParamWing(), run: () => wingParam('sweep_deg', 'Sweep', '°', 0.2, -60, 75) });
  cmd('wingDihedral', { label: 'Dihedral', group: 'Edit', icon: 'wing', enabled: () => isParamWing(), run: () => wingParam('dihedral_deg', 'Dihedral', '°', 0.2, -45, 60) });
  cmd('wingTwist', { label: 'Twist the tip', group: 'Edit', icon: 'rotate', enabled: () => isParamWing(), run: () => wingParam('twist_deg', 'Tip twist', '°', 0.05, -30, 30) });
  cmd('smoothBody', { label: 'Smooth the body (off, ×2, ×4, ×8 rings)', group: 'Edit', keys: 'Shift+S', icon: 'smooth', enabled: () => inEdit() && editKind() === 'fuselage', why: () => 'Smoothing is for a fuselage in Edit mode.', run: () => {
    const key = S.edit.key;
    const next = ((+getItem(S.spec, key).smooth || 0) + 1) % 4;
    change(s => { getItem(s, key).smooth = next; }, true);
    say(next ? `Smoothing on: ${1 << next}× the rings between your rings (in the game too).` : 'Smoothing off.');
  } });
  cmd('addReference', { label: 'Add a reference image', group: 'Object', icon: 'image', run: (view = null, file = null) => addReference(typeof view === 'string' ? view : null, file) });
  cmd('importObj', { label: 'Import an .obj shape', group: 'Object', icon: 'import', run: (file = null) => importObj(file) });

  // ------------------------------------------------------------ the 3D view

  const vp = new Viewport(vpWrap, {
    onPointerDown: e => pointerDown(e),
    onLeftDown: e => leftDown(e),
    onPointerMove: e => pointerMove(e),
    onPointerUp: e => pointerUp(e),
    onRightClick: e => contextMenu(e.clientX, e.clientY),
    onWheel: e => {
      if (S.modal && S.view.prop && inEdit()) { S.view.propRadius = Math.max(0.05, S.view.propRadius * (e.deltaY > 0 ? 1.12 : 1 / 1.12)); S.modal.update(); showPropCircle(); return true; }
      return false;
    },
    onViewChange: () => { updateViewName(); markHeader(); if (S.view.prop && S.modal) showPropCircle(); },
    onGizmoStart: () => gizmoStart(),
    onGizmoChange: op => gizmoChange(op),
    onGizmoEnd: () => gizmoEnd(),
    onThemeChange: () => refreshEdit(),
  });
  vp.setSourceMeshes(S.sourceMeshes);
  vp.setPlane(S.spec);
  vp.setHiddenLocked(S.spec.editor.hidden, lockedKeys());
  vp.frameBox(null);

  // ------------------------------------------------------------ header, tool shelf, status bar

  function renderHeader() {
    const h = $('#vp-header');
    const b = (id, label, opts = {}) => `<button type="button" class="hb" data-cmd="${id}" title="${esc(tip(id))}" ${opts.pressed != null ? `aria-pressed="${opts.pressed}"` : ''}>${C[id].icon ? icon(C[id].icon) : ''}${label ? `<span>${esc(label)}</span>` : ''}</button>`;
    const modes = inEdit() ? editModes(S.spec, S.edit.key) || [] : [];
    const modeKey = m => (m === 'vert' ? 1 : m === 'edge' || m === 'ring' ? 2 : 3);
    h.innerHTML = `
      <div class="seg" role="group" aria-label="Mode"><button type="button" class="mode-btn" data-mode="object" aria-pressed="${!inEdit()}" title="Object mode (Tab)">Object</button><button type="button" class="mode-btn" data-mode="edit" aria-pressed="${inEdit()}" title="Edit mode (Tab): reshape the active part">Edit</button></div>
      ${modes.length ? `<div class="seg" role="group" aria-label="Select">${modes.map(m => `<button type="button" data-elem="${m}" aria-pressed="${S.edit.mode === m}" title="${MODE_LABEL[m]} select (${modeKey(m)})">${icon(m)}<span class="hide-narrow">${MODE_LABEL[m]}</span></button>`).join('')}</div>` : ''}
      <span class="sep"></span>
      ${b('add', 'Add')}
      <span class="sep"></span>
      
      <div class="seg" role="group" aria-label="Views">
        <button type="button" data-cmd="viewTop" title="${esc(tip('viewTop'))} · Ctrl+click: bottom">Top</button>
        <button type="button" data-cmd="viewFront" title="${esc(tip('viewFront'))} · Ctrl+click: back">Front</button>
        <button type="button" data-cmd="viewRight" title="${esc(tip('viewRight'))} · Ctrl+click: left">Side</button>
        <button type="button" data-cmd="viewUser" title="3D view">3D</button>
        <button type="button" data-cmd="viewCockpit" title="${esc(tip('viewCockpit'))}: check the interior and instruments">Cockpit</button>
      </div>
      ${b('ortho', '')}${b('frameSel', '')}${b('frameAll', '')}${b('quad', '', { pressed: S.view.quad })}
      <span class="sep"></span>
      <div class="seg" role="group" aria-label="Shading">
        <button type="button" data-shade="solid" aria-pressed="${!S.view.wire}" title="Solid">${icon('solid')}</button>
        <button type="button" data-shade="wire" aria-pressed="${S.view.wire}" title="${esc(tip('wire'))}">${icon('wire')}</button>
      </div>
      ${b('xray', '', { pressed: S.view.xray })}
      <span class="sep"></span>
      ${b('snap', '', { pressed: S.view.snap })}${b('mirrorX', 'X', { pressed: S.view.mirrorX })}${b('prop', '', { pressed: S.view.prop })}${b('measure', '', { pressed: S.view.measure })}
      <span class="grow"></span>
      ${b('help', '')}`;
    markHeader();
  }

  function markHeader() {
    const r = vp.activeRig;
    root.querySelectorAll('#vp-header [data-cmd^=view]').forEach(el => el.setAttribute('aria-pressed', String(
      (el.dataset.cmd === 'viewTop' && (r.view === 'top' || r.view === 'bottom')) || (el.dataset.cmd === 'viewFront' && (r.view === 'front' || r.view === 'back'))
      || (el.dataset.cmd === 'viewRight' && (r.view === 'right' || r.view === 'left')) || (el.dataset.cmd === 'viewUser' && r.view === 'user' && !r.cockpit)
      || (el.dataset.cmd === 'viewCockpit' && !!r.cockpit))));
    const o = root.querySelector('#vp-header [data-cmd=ortho]');
    if (o) { o.setAttribute('aria-pressed', String(r.orthographic)); o.title = `${r.orthographic ? 'Orthographic' : 'Perspective'}: click to switch (Numpad 5)`; }
  }

  $('#vp-header').addEventListener('click', e => {
    const t = e.target.closest('button');
    if (!t) return;
    if (t.dataset.mode) { if ((t.dataset.mode === 'edit') !== inEdit()) toggleEdit(); return; }
    if (t.dataset.elem) { setElemMode(t.dataset.elem); return; }
    if (t.dataset.shade) { setView({ wire: t.dataset.shade === 'wire' }); return; }
    const id = t.dataset.cmd;
    if (!id) return;
    if (e.ctrlKey && id === 'viewTop') return vp.setView('bottom');
    if (e.ctrlKey && id === 'viewFront') return vp.setView('back');
    if (e.ctrlKey && id === 'viewRight') return vp.setView('left');
    const r = t.getBoundingClientRect();
    run(id, r.left, r.bottom + 2);
  });

  function renderShelf() {
    const sh = $('#toolshelf');
    const tool = t => `<button type="button" class="tool" data-tool="${t.id}" aria-pressed="${S.tool === t.id}" title="${esc(t.title)}">${icon(t.icon)}<span class="t">${t.label}</span><kbd>${t.keys}</kbd></button>`;
    const act = (id, label, keys) => `<button type="button" class="tool" data-cmd="${id}" title="${esc(tip(id))}" ${C[id].enabled && !C[id].enabled() ? 'disabled' : ''}>${icon(C[id].icon)}<span class="t">${label}</span>${keys ? `<kbd>${keys}</kbd>` : ''}</button>`;
    let out = TOOLS.map(tool).join('');
    out += '<div class="shelf-sep"></div>';
    if (!inEdit()) {
      out += act('add', 'Add', '⇧A') + act('duplicate', 'Duplicate', '⇧D') + act('delete', 'Delete', 'X') + act('toggleEdit', 'Edit', 'Tab');
      out += '<div class="shelf-sep"></div>' + act('addReference', 'Reference', '') + act('measure', 'Measure', '⇧M');
    } else {
      const k = editKind();
      if (k === 'fuselage') out += act('extrude', 'Extrude', 'E') + act('loopcut', 'Loop cut', '^R') + act('delete', 'Delete', 'X') + act('smoothBody', 'Smooth', '⇧S');
      if (k === 'shape') out += act('extrude', 'Extrude', 'E') + act('inset', 'Inset', 'I') + act('loopcut', 'Loop cut', '^R') + act('merge', 'Merge', 'M') + act('delete', 'Delete', 'X');
      if (k === 'wing') out += act('wingSweep', 'Sweep', '') + act('wingDihedral', 'Dihedral', '') + act('wingTwist', 'Twist', '');
      out += '<div class="shelf-sep"></div>' + act('prop', 'Falloff', 'O') + act('toggleEdit', 'Done', 'Tab');
    }
    sh.innerHTML = out;
    sh.querySelectorAll('[data-cmd=prop]').forEach(b => b.setAttribute('aria-pressed', String(S.view.prop)));
    sh.querySelectorAll('[data-cmd=measure]').forEach(b => b.setAttribute('aria-pressed', String(S.view.measure)));
  }
  $('#toolshelf').addEventListener('click', e => {
    const t = e.target.closest('button');
    if (!t) return;
    if (t.dataset.tool) { setTool(t.dataset.tool); return; }
    const r = t.getBoundingClientRect();
    run(t.dataset.cmd, r.right + 4, r.top);
  });
  root.querySelectorAll('.b-top [data-cmd]').forEach(b => { b.title = tip(b.dataset.cmd); b.onclick = () => run(b.dataset.cmd); });

  function setTool(id) {
    S.tool = id;
    renderShelf();
    updateGizmo();
    updateStatus();
  }

  function updateStatus() {
    const k = (keys, what) => `<span class="hint">${keys.split(' ').map(x => `<kbd>${esc(x)}</kbd>`).join('')} ${esc(what)}</span>`;
    let hints;
    if (S.modal) hints = [k('Mouse', 'adjust'), k('X Y Z', 'axis'), k('Shift+X', 'plane'), k('0-9', 'exact value'), k('Ctrl', 'snap'), k('Enter', 'confirm'), k('Esc', 'cancel')];
    else if (S.pending?.type === 'loopcut') hints = [k('Click', 'cut here'), k('Esc', 'cancel')];
    else if (S.pending?.type === 'calibrate') hints = [k('Click', S.pending.pts.length ? 'second point' : 'first point'), k('Esc', 'cancel')];
    else if (inEdit()) {
      const kind = editKind();
      hints = [k('Click', 'select'), k('Shift+Click', 'add'), k('A', 'all'), k('G R S', 'move, rotate, scale')];
      if (kind === 'fuselage') hints.push(k('1 2', 'vertex, ring'), k('E', 'extrude ring'), k('Ctrl+R', 'loop cut'), k('O', 'falloff'));
      if (kind === 'shape') hints.push(k('1 2 3', 'vert, edge, face'), k('E', 'extrude'), k('I', 'inset'), k('Ctrl+R', 'loop cut'), k('M', 'merge'));
      if (kind === 'wing') hints.push(k('1 3', 'corners, strips'));
      hints.push(k('Tab', 'done'));
    } else if (S.selected.length) hints = [k('G', 'move'), k('R', 'rotate'), k('S', 'scale'), k('Shift+D', 'duplicate'), k('X', 'delete'), k('H', 'hide'), k('Tab', 'edit'), k('F', 'frame'), k('Right-click', 'menu')];
    else hints = [k('Click', 'select'), k('Drag', 'box select'), k('Shift+A', 'add'), k('Middle-drag', 'orbit'), k('Wheel', 'zoom'), k('?', 'all shortcuts')];
    const parts = allKeys(S.spec).filter(x => !x.startsWith('reference')).length;
    const stat = inEdit() ? `${countOf(S.edit.mode, S.edit.sel.size)} selected` : `${S.selected.length ? `${S.selected.length} of ` : ''}${parts} parts`;
    $('#statusbar').innerHTML = `${S.status ? `<span class="hint" style="color:var(--text)">${esc(S.status)}</span>` : hints.join('')}<span class="grow"></span><span class="stat">${esc(stat)}</span>`;
  }
  let statusTimer = null;
  function say(msg, ms = 4500) { S.status = msg; updateStatus(); clearTimeout(statusTimer); statusTimer = setTimeout(() => { S.status = ''; updateStatus(); }, ms); }

  function updateViewName() {
    const r = vp.activeRig;
    const name = S.active && S.active !== 'plane' ? itemName(S.spec, S.active) : S.spec.name;
    $('#vp-viewname').innerHTML = `<span><b>${esc(S.view.quad ? 'Quad view' : r.label())}</b> · ${inEdit() ? `Edit mode · ${esc(MODE_LABEL[S.edit.mode])}` : 'Object mode'} · ${esc(name || '')}</span>`;
    vpWrap.querySelectorAll('.quad-label, .quad-lines').forEach(e => e.remove());
    if (S.view.quad) {
      vpWrap.append(html('<div class="quad-lines"></div>'));
      [['Top', '8px', '26px'], ['Front', 'calc(50% + 8px)', '26px'], ['Right', '8px', 'calc(50% + 6px)'], ['User', 'calc(50% + 8px)', 'calc(50% + 6px)']].forEach(([t, l, tp]) => {
        const el = html(`<div class="quad-label">${t}</div>`); el.style.left = l; el.style.top = tp; vpWrap.append(el);
      });
    }
  }

  // ------------------------------------------------------------ state changes and undo

  /** Applies a change to the plane. `final` records an undo step; live changes (typing, dragging) don't. */
  function change(fn, final = true, opts = {}) { fn(S.spec); changed(final, opts); }

  function changed(final, opts = {}) {
    vp.setPlane(S.spec);
    vp.setHiddenLocked(S.spec.editor.hidden, lockedKeys());
    refreshEdit();
    if (opts.outline || final) renderOutline();
    if (opts.title && document.activeElement !== $('#plane-name')) $('#plane-name').value = S.spec.name;
    // The view's caption names the plane (or the active part): keep it in step with a rename.
    if (opts.title || !S.active || S.active === 'plane') updateViewName();
    if (final) commit();
    if (final && opts.inspector !== false) renderInspectorSoon();
    if (S.view.measure) showMeasure();
    analyzeSoon();
    draftSoon();
    if (final) updateSaveState(); else if (S.savedJson !== null) $('#save-state').textContent = 'Unsaved changes';
    if (!S.gizmoSession && !S.modal) updateGizmo();
    const parts = hasParts();
    $('#vp-empty').hidden = parts;
    if (parts && !S.hadParts) vp.frameBox(null);
    S.hadParts = parts;
  }

  function commit() {
    const now = JSON.stringify(S.spec);
    if (now === S.current) return;
    S.past.push(S.current);
    let total = S.past.reduce((n, s) => n + s.length, 0);
    while (S.past.length > 1 && (S.past.length > 300 || total > UNDO_BUDGET)) total -= S.past.shift().length;
    S.future = [];
    S.current = now;
    updateUndo();
    updateSaveState();
  }

  function restore(json) {
    S.spec = normalize(JSON.parse(json));
    S.current = json;
    S.selected = S.selected.filter(k => k === 'plane' || getItem(S.spec, k));
    if (S.active && !getItem(S.spec, S.active)) S.active = S.selected[S.selected.length - 1] || null;
    if (S.edit && !getItem(S.spec, S.edit.key)) exitEdit(false);
    if (S.edit) { const el = buildElements(S.spec, S.edit.key); const ok = new Set(allIds(el, S.edit.mode)); S.edit.sel = new Set([...S.edit.sel].filter(id => ok.has(id))); }
    vp.setPlane(S.spec);
    vp.setHiddenLocked(S.spec.editor.hidden, lockedKeys());
    vp.setSelection(S.selected, S.active);
    $('#plane-name').value = S.spec.name;
    refreshEdit(); renderOutline(); renderInspector_(); analyzeSoon(); draftSoon(); updateUndo(); updateSaveState(); updateGizmo(); renderShelf(); updateStatus();
  }
  function undo() { if (S.modal) return; if (!S.past.length) return; commit(); if (!S.past.length) return; S.future.push(S.current); restore(S.past.pop()); say('Undone.', 1500); }
  function redo() { if (S.modal) return; if (!S.future.length) return; S.past.push(S.current); restore(S.future.pop()); say('Redone.', 1500); }

  function updateUndo() {
    root.querySelector('[data-cmd=undo]').disabled = !S.past.length && JSON.stringify(S.spec) === S.current;
    root.querySelector('[data-cmd=redo]').disabled = !S.future.length;
  }

  const isDirty = () => S.savedJson === null || JSON.stringify(S.spec) !== S.savedJson;
  function updateSaveState() {
    document.title = `${S.spec.name || 'Untitled plane'} · OpenPlane`;
    const el = $('#save-state');
    if (S.saving) { el.textContent = 'Saving…'; return; }
    el.textContent = S.savedJson === null ? 'Not saved yet' : isDirty() ? 'Unsaved changes' : 'All changes saved';
  }

  // ------------------------------------------------------------ selection

  function setSelection(keys, active = keys[keys.length - 1] || null) {
    S.selected = [...new Set(keys.filter(Boolean))];
    S.active = active && S.selected.includes(active) ? active : S.selected[S.selected.length - 1] || null;
    selectionChanged();
  }

  function selectionChanged() {
    vp.setSelection(S.selected, S.active);
    renderOutline();
    $('#outliner .ol-row.active')?.scrollIntoView({ block: 'nearest' });
    renderInspector_();
    updateGizmo();
    renderShelf();
    updateStatus();
    updateViewName();
    if (S.view.measure) showMeasure();
  }

  /** Click on a part (object mode) or a row: replace, add (Shift) or toggle (Ctrl). */
  function select(key, { add = false, toggle = false } = {}) {
    if (inEdit() && key !== S.edit.key) exitEdit();
    if (!key || key === 'plane') { setSelection([], null); return; }
    if (toggle || (add && S.selected.includes(key) && S.active === key)) setSelection(S.selected.filter(k => k !== key), S.active === key ? null : S.active);
    else if (add) setSelection([...S.selected.filter(k => k !== key), key], key);
    else setSelection([key], key);
  }

  function selectAll() {
    if (inEdit()) { const el = buildElements(S.spec, S.edit.key); const all = allIds(el, S.edit.mode); S.edit.sel = S.edit.sel.size === all.length ? new Set() : new Set(all); editSelChanged(); return; }
    const keys = allKeys(S.spec).filter(k => !isHidden(k) && !isLocked(k) && !k.startsWith('reference:'));
    setSelection(S.selected.length === keys.length ? [] : keys);
  }
  function selectNone() { if (inEdit()) { S.edit.sel = new Set(); editSelChanged(); return; } setSelection([]); }
  function selectInvert() {
    if (inEdit()) { const el = buildElements(S.spec, S.edit.key); S.edit.sel = new Set(allIds(el, S.edit.mode).filter(id => !S.edit.sel.has(id))); editSelChanged(); return; }
    setSelection(allKeys(S.spec).filter(k => !isHidden(k) && !isLocked(k) && !k.startsWith('reference:') && !S.selected.includes(k)));
  }

  // ------------------------------------------------------------ outliner and properties

  function renderOutline() {
    renderOutliner($('#outliner'), {
      spec: S.spec, selected: new Set(S.selected.length ? S.selected : ['plane']), active: S.active || (S.selected.length ? null : 'plane'),
      hidden: new Set(S.spec.editor.hidden), locked: new Set(S.spec.editor.locked), editKey: S.edit?.key, fixedOpen: S.fixedOpen,
      setFixedOpen: v => { S.fixedOpen = v; },
      select: (key, mods) => select(key, mods),
      toggleHidden: key => toggleHidden(key),
      toggleLocked: key => toggleLocked(key),
      rename: (key, name) => change(s => { if (key === 'plane') s.name = name; else getItem(s, key).name = name; }, true, { outline: true, title: true }),
      rerender: () => renderOutline(),
      add: (section, x, y) => addMenu(x, y, section),
    });
  }

  function toggleHidden(key) {
    if (key.startsWith('reference:')) { change(s => { const r = getItem(s, key); r.visible = r.visible === false; }, true, { outline: true }); return; }
    change(s => { const h = s.editor.hidden; const i = h.indexOf(key); if (i >= 0) h.splice(i, 1); else h.push(key); }, true, { outline: true });
    if (isHidden(key)) setSelection(S.selected.filter(k => k !== key));
  }
  function toggleLocked(key) {
    if (key.startsWith('reference:')) { change(s => { const r = getItem(s, key); r.locked = !r.locked; }, true, { outline: true }); return; }
    change(s => { const l = s.editor.locked; const i = l.indexOf(key); if (i >= 0) l.splice(i, 1); else l.push(key); }, true, { outline: true });
  }
  function hideSelected() {
    const keys = S.selected.filter(k => k !== 'plane');
    change(s => {
      for (const k of keys) {
        if (k.startsWith('reference:')) getItem(s, k).visible = false;
        else if (!s.editor.hidden.includes(k)) s.editor.hidden.push(k);
      }
    }, true, { outline: true });
    setSelection([]);
    say(`Hid ${keys.length} part${keys.length === 1 ? '' : 's'}. Alt+H shows everything again.`);
  }
  function hideOthers() {
    const keep = new Set(S.selected);
    change(s => { s.editor.hidden = allKeys(s).filter(k => !keep.has(k) && !k.startsWith('reference:')); }, true, { outline: true });
  }
  function unhideAll() {
    const n = S.spec.editor.hidden.length;
    change(s => { s.editor.hidden = []; }, true, { outline: true });
    say(n ? `Showing ${n} hidden part${n === 1 ? '' : 's'} again.` : 'Nothing was hidden.');
  }

  let inspTimer = null;
  function renderInspectorSoon() { clearTimeout(inspTimer); inspTimer = setTimeout(renderInspector_, 0); }
  function renderInspector_() {
    if (S.destroyed) return;
    const host = $('#inspector');
    const sc = host.parentElement;
    const focusables = () => [...host.querySelectorAll('input, select, textarea, button')];
    const idx = host.contains(document.activeElement) ? focusables().indexOf(document.activeElement) : -1;
    const scroll = sc.scrollTop;
    const key = inEdit() ? S.edit.key : S.selected.length === 1 ? S.selected[0] : S.selected.length ? null : 'plane';
    renderInspector(host, {
      spec: S.spec, key: key || 'plane', count: inEdit() ? 1 : S.selected.length, analysis: S.analysis, sourceMeshes: S.sourceMeshes, report: S.report,
      edit: inEdit() ? { ...S.edit, proportional: S.view.prop } : null,
      change, select: k => select(k), run: id => run(id),
      actions: {
        autoGear, resize: resizePlane, makeEditable, calibrate, preset: id => applyRingPreset(id),
        setSides: n => setSides(n),
        selectRing: i => { enterEditOn(key, 'ring'); S.edit.sel = new Set([i]); S.edit.active = i; editSelChanged(); },
        insertRingAfter: n => insertRingAfter(n), addEndRing: nose => addEndRing(nose),
        selectSurface: i => { enterEditOn(key, 'face'); S.edit.sel = new Set([`s${i}`]); editSelChanged(); },
        showReport: () => modal(close => {
          const el = html(`<div style="display:contents"><h2>What changed in conversion</h2><div></div><div class="btn-row"><button type="button" class="btn primary">Close</button></div></div>`);
          el.children[1].replaceWith(reportGroups(S.report));
          el.querySelector('button').onclick = () => close();
          return el;
        }, { wide: true, label: 'Conversion report' }),
      },
    });
    sc.scrollTop = scroll;
    if (idx >= 0) focusables()[idx]?.focus();
  }

  // ------------------------------------------------------------ edit mode

  function toggleEdit() {
    if (S.modal) return;
    if (inEdit()) { exitEdit(); return; }
    const key = S.active;
    if (!key || key === 'plane') { say('Select a fuselage, shape or wing first, then press Tab.'); return; }
    enterEditOn(key);
  }

  function enterEditOn(key, mode = null) {
    if (inEdit() && S.edit.key === key) { if (mode && S.edit.mode !== mode) setElemMode(mode); return; }
    const modes = editModes(S.spec, key);
    if (!modes) {
      if (isFixedMesh(key)) toast('This body keeps its exact shape from a converted plane. Make it editable to change its vertices.', { action: { label: 'Make editable', run: () => makeEditable() } });
      else say(`${KIND_LABEL[parseKey(key).kind]}s have no Edit mode: change them with the numbers on the right, or move, rotate and scale them.`);
      return;
    }
    if (isHidden(key)) toggleHidden(key);
    const kind = parseKey(key).kind;
    S.mode = 'edit';
    S.edit = { key, mode: mode || (modes.includes(S.lastElemMode[kind]) ? S.lastElemMode[kind] : modes[0]), sel: new Set(), active: null };
    S.selected = [key]; S.active = key;
    root.dataset.mode = 'edit';
    vp.setSelection(S.selected, S.active);
    vp.setEditKey(key);
    refreshEdit();
    renderHeader(); renderShelf(); renderOutline(); renderInspector_(); updateGizmo(); updateStatus(); updateViewName();
  }

  function exitEdit(rerender = true) {
    if (!S.edit) return;
    cancelPending();
    S.lastElemMode[parseKey(S.edit.key).kind] = S.edit.mode;
    S.mode = 'object'; S.edit = null;
    root.dataset.mode = 'object';
    vp.setEditKey(null);
    vp.setEditOverlay(null);
    vp.setPropCircle(null);
    if (!rerender) return;
    renderHeader(); renderShelf(); renderOutline(); renderInspector_(); updateGizmo(); updateStatus(); updateViewName();
  }

  function setElemMode(m) {
    if (!inEdit()) return;
    const modes = editModes(S.spec, S.edit.key) || [];
    if (!modes.includes(m)) return;
    const el = buildElements(S.spec, S.edit.key);
    S.edit.sel = convertSelection(el, S.edit.mode, m, S.edit.sel);
    S.edit.mode = m;
    S.edit.active = null;
    renderHeader();
    editSelChanged();
  }

  function refreshEdit() {
    if (!inEdit()) return;
    const el = buildElements(S.spec, S.edit.key);
    if (!el) { exitEdit(); return; }
    vp.setEditOverlay(overlayFor(el, S.edit.mode, S.edit.sel, S.edit.active));
  }

  function editSelChanged() {
    refreshEdit();
    renderInspector_();
    updateGizmo();
    renderShelf();
    updateStatus();
    updateViewName();
  }

  /** The edit element under the cursor, or null. Vertices hidden behind the surface are skipped unless X-ray is on. */
  function pickElement(x, y) {
    const el = buildElements(S.spec, S.edit.key);
    const mode = S.edit.mode;
    const vpr = vp.viewportAt(x, y);
    const cands = [];
    const camPos = vp.cameraPlanePos(x, y);
    const seeThrough = S.view.xray || S.view.wire;
    const occluded = (p, sx, sy) => {
      if (seeThrough) return false;
      const d = vec.len(vec.sub(p, camPos));
      return vp.surfaceDistance(sx, sy) < d - Math.max(0.03, d * 0.004);
    };
    if (mode === 'vert' || mode === 'ring' || mode === 'face') {
      for (const id of allIds(el, mode)) {
        const p = elementPoint(el, mode, id);
        const s = vp.projectIn(p, vpr);
        if (!s.visible) continue;
        const d = Math.hypot(s.cx - x, s.cy - y);
        if (d < (mode === 'vert' ? 14 : 20)) cands.push({ id, d, p, s });
      }
      if (mode === 'ring') {
        for (const r of el.rings) {
          if (r.point) continue;
          let best = Infinity;
          for (let k = 0; k < r.pts.length; k++) {
            const a = vp.projectIn(r.pts[k], vpr), b = vp.projectIn(r.pts[(k + 1) % r.pts.length], vpr);
            best = Math.min(best, segDist(x, y, a.cx, a.cy, b.cx, b.cy));
          }
          if (best < 8) cands.push({ id: r.id, d: best + 2, p: r.c, s: null });
        }
      }
    } else if (mode === 'edge') {
      for (const e of el.edges) {
        const a = vp.projectIn(el.verts.get(e.a).p, vpr), b = vp.projectIn(el.verts.get(e.b).p, vpr);
        if (!a.visible || !b.visible) continue;
        const d = segDist(x, y, a.cx, a.cy, b.cx, b.cy);
        if (d < 9) cands.push({ id: e.id, d, p: vec.lerp(el.verts.get(e.a).p, el.verts.get(e.b).p, 0.5), s: null });
      }
    }
    cands.sort((a, b) => a.d - b.d);
    for (const c of cands.slice(0, 6)) {
      if (!c.s) return c.id;
      if (!occluded(c.p, c.s.cx, c.s.cy)) return c.id;
    }
    // Rings: a click anywhere on the body picks the nearest ring along it.
    if (mode === 'ring') {
      const hit = vp.pickObject(x, y);
      if (hit && hit.key === S.edit.key) {
        const it = getItem(S.spec, S.edit.key);
        const l = fuselageTransform(it).fromPlane(hit.point);
        let best = null;
        it.rings.forEach((r, i) => { const d = Math.abs(l[2] - (+r.z || 0)); if (!best || d < best.d) best = { i, d }; });
        if (best) return best.i;
      }
    }
    return null;
  }

  // ------------------------------------------------------------ pointer

  function pointerDown(e) {
    if (S.modal) {
      if (e.button === 0) S.modal.confirm();
      else if (e.button === 2) S.modal.cancel();
      return true;
    }
    if (S.pending && e.button === 0) { pendingClick(e); return true; }
    if (S.pending && e.button === 2) { cancelPending(); $('#xf-readout').hidden = true; return true; }
    if (menuOpen()) closeMenu();
    return false;
  }

  function leftDown(e) {
    const hit = inEdit() ? null : vp.pickObject(e.clientX, e.clientY);
    const elemHit = inEdit() ? pickElement(e.clientX, e.clientY) : null;
    S.press = { x: e.clientX, y: e.clientY, shift: e.shiftKey, ctrl: e.ctrlKey, hit, elemHit, box: S.boxMode, moved: false };
    S.boxMode = false;
  }

  function pointerMove(e) {
    if (S.modal) { S.modal.move(e); if (S.view.prop && inEdit()) showPropCircle(); return; }
    if (S.pending) { pendingHover(e); return; }
    const p = S.press;
    if (!p || e.buttons !== 1) return;
    const d = Math.hypot(e.clientX - p.x, e.clientY - p.y);
    if (!p.moved && d < 5) return;
    if (!p.moved) {
      p.moved = true;
      // Drag on a part (or on an element) moves it; drag on empty space draws a box.
      if (!p.box && !inEdit() && p.hit && !p.shift && !p.ctrl) {
        if (!S.selected.includes(p.hit.key)) setSelection([p.hit.key]);
        S.press = null;
        startTransform('translate', { x: p.x, y: p.y });
        S.modal?.move(e);
        if (S.modal) S.modal.releaseConfirms = true;
        return;
      }
      if (!p.box && inEdit() && p.elemHit != null && !p.shift && !p.ctrl) {
        if (!S.edit.sel.has(p.elemHit)) { S.edit.sel = new Set([p.elemHit]); S.edit.active = p.elemHit; editSelChanged(); }
        S.press = null;
        startTransform('translate', { x: p.x, y: p.y });
        S.modal?.move(e);
        if (S.modal) S.modal.releaseConfirms = true;
        return;
      }
      p.boxEl = html('<div class="box-select"></div>');
      vpWrap.append(p.boxEl);
    }
    if (p.boxEl) {
      const r = vpWrap.getBoundingClientRect();
      const x0 = Math.min(p.x, e.clientX), x1 = Math.max(p.x, e.clientX), y0 = Math.min(p.y, e.clientY), y1 = Math.max(p.y, e.clientY);
      Object.assign(p.boxEl.style, { left: `${x0 - r.left}px`, top: `${y0 - r.top}px`, width: `${x1 - x0}px`, height: `${y1 - y0}px` });
    }
  }

  function pointerUp(e) {
    // A drag-to-move ends when the button comes up.
    if (S.modal?.releaseConfirms) { S.modal.confirm(); return; }
    const p = S.press;
    S.press = null;
    if (!p) return;
    if (p.boxEl) {
      p.boxEl.remove();
      const rect = { x0: Math.min(p.x, e.clientX), x1: Math.max(p.x, e.clientX), y0: Math.min(p.y, e.clientY), y1: Math.max(p.y, e.clientY) };
      boxSelect(rect, p.shift, p.ctrl, vp.viewportAt(p.x, p.y));
      return;
    }
    if (p.moved) return;
    if (inEdit()) {
      const id = p.elemHit;
      if (id == null) { if (!p.shift && !p.ctrl) { S.edit.sel = new Set(); S.edit.active = null; editSelChanged(); } return; }
      const sel = new Set(p.shift || p.ctrl ? S.edit.sel : []);
      if ((p.shift || p.ctrl) && sel.has(id) && (p.ctrl || S.edit.active === id)) sel.delete(id); else sel.add(id);
      S.edit.sel = sel; S.edit.active = sel.has(id) ? id : null;
      editSelChanged();
      return;
    }
    if (p.hit) { select(p.hit.key, { add: p.shift, toggle: p.ctrl }); return; }
    const ref = vp.pickReference(e.clientX, e.clientY);
    if (ref && !ref.locked) { select(keyOf('reference', ref.id), { add: p.shift, toggle: p.ctrl }); return; }
    if (!p.shift && !p.ctrl) select('plane');
  }

  function boxSelect(rect, add, remove, vpr) {
    if (inEdit()) {
      const el = buildElements(S.spec, S.edit.key);
      const ids = allIds(el, S.edit.mode).filter(id => {
        const s = vp.projectIn(elementPoint(el, S.edit.mode, id), vpr);
        return s.visible && s.cx >= rect.x0 && s.cx <= rect.x1 && s.cy >= rect.y0 && s.cy <= rect.y1;
      });
      const sel = new Set(add || remove ? S.edit.sel : []);
      ids.forEach(id => (remove ? sel.delete(id) : sel.add(id)));
      S.edit.sel = sel;
      editSelChanged();
      return;
    }
    const keys = vp.objectsInRect(rect, vpr);
    if (remove) setSelection(S.selected.filter(k => !keys.includes(k)));
    else setSelection(add ? [...S.selected, ...keys] : keys);
  }

  // ------------------------------------------------------------ transforms

  /** Everything a transform needs: targets, pivot, and how to apply an op from the snapshot. */
  function transformTargets() {
    if (inEdit()) {
      const key = S.edit.key;
      const orig = JSON.parse(JSON.stringify(getItem(S.spec, key)));
      const el = buildElements(S.spec, key);
      const kind = parseKey(key).kind;
      // Wing strips slide along the span (G) or change their chord (S) instead.
      if (kind === 'wing' && S.edit.mode === 'face') return S.edit.sel.size ? { strip: true, key, orig } : null;
      const vsel = selectedVerts(el, S.edit.mode, S.edit.sel);
      if (!vsel.size) return null;
      const pts = [...vsel].map(id => el.verts.get(id).p);
      const pivot = vec.mul(pts.reduce(vec.add, [0, 0, 0]), 1 / pts.length);
      const T = kind === 'fuselage' ? fuselageTransform(orig) : kind === 'shape' ? shapeTransform(orig) : null;
      const mirror = S.view.mirrorX && !!T;
      const partners = new Map();
      if (mirror) for (const id of vsel) { const p = mirrorPartner(el, orig, id); if (p != null && !vsel.has(p)) partners.set(p, id); }
      const onCentre = new Set();
      if (mirror) for (const id of vsel) { const l = T.fromPlane(el.verts.get(id).p); if (Math.abs(l[0]) < 1e-4) onCentre.add(id); }
      // Proportional editing: weights for the other vertices of this part.
      const weights = new Map();
      if (S.view.prop) {
        const R = S.view.propRadius;
        for (const [id, v] of el.verts) {
          if (vsel.has(id) || partners.has(id) || v.strip != null) continue;
          let dmin = Infinity;
          for (const p of pts) dmin = Math.min(dmin, vec.len(vec.sub(v.p, p)));
          if (dmin < R) { const t = dmin / R; weights.set(id, 1 - t * t * (3 - 2 * t)); }
        }
      }
      const ringSel = S.edit.mode === 'ring' ? [...S.edit.sel] : [];
      return {
        edit: true, key, orig, pivot,
        apply(op) {
          const moved = new Map();
          for (const id of vsel) {
            let np = xfPoint(el.verts.get(id).p, op);
            if (onCentre.has(id)) { const l = T.fromPlane(np); l[0] = 0; np = T.toPlane(l); }
            moved.set(id, np);
          }
          for (const [p, src] of partners) { const l = T.fromPlane(moved.get(src)); l[0] = -l[0]; moved.set(p, T.toPlane(l)); }
          for (const [id, w] of weights) { const p = el.verts.get(id).p; moved.set(id, vec.add(p, vec.mul(vec.sub(xfPoint(p, op), p), w))); }
          const ringOps = new Map();
          for (const i of ringSel) { const r = el.rings.find(x => x.id === i); if (r) ringOps.set(i, { type: op.type, s: op.s, center: xfPoint(r.c, op) }); }
          writeVerts(S.spec, key, orig, el, moved, { ringOps });
          return null;
        },
      };
    }
    const keys = movable();
    if (!keys.length) return null;
    const pivot = keys.length === 1 ? originOf(S.spec, keys[0], S.sourceMeshes) : medianOf(S.spec, keys, S.sourceMeshes);
    const snap = new Map(keys.map(k => [k, JSON.parse(JSON.stringify(getItem(S.spec, k)))]));
    return {
      keys, pivot,
      apply(op) {
        let note = null;
        for (const k of keys) note = applyToPart(S.spec, k, snap.get(k), { ...op, pivot: op.pivot || pivot }, { sourceMeshes: S.sourceMeshes, multi: keys.length > 1 }) || note;
        return note;
      },
    };
  }

  function startTransform(type, { x = null, y = null, axis = null, label = null, after = null } = {}) {
    if (S.modal) return;
    closeMenu();
    const t = transformTargets();
    if (!t) { say(inEdit() ? `Select some ${PLURAL_OF(S.edit.mode)} first.` : 'Select something first.'); return; }
    if (t.strip) { stripTransform(type, t, x, y); return; }
    const snapJson = JSON.stringify(S.spec);
    const pos = x != null ? { x, y } : lastMouse();
    hideGizmo();
    S.modal = new Modal({
      vp, type, pivot: t.pivot, x: pos.x, y: pos.y, axis, label, snap: S.view.snap,
      readout: $('#xf-readout'),
      restore: () => { S.spec = JSON.parse(snapJson); },
      apply: op => { const n = t.apply(op); changed(false, { inspector: false }); return n; },
      done: (ok, note) => {
        S.modal = null;
        vp.setPropCircle(null);
        if (ok) { changed(true); if (note) say(note, 6000); after?.(false); }
        else { S.spec = JSON.parse(snapJson); changed(false); after?.(true); }
        updateStatus();
        updateGizmo();
      },
    });
    if (S.view.prop && inEdit()) showPropCircle();
    updateStatus();
  }

  /** A one-number drag (sweep, dihedral, twist, strip slides). */
  function paramDrag({ label, unit, value, perPx, min, max, step, set, x = null, y = null }) {
    if (S.modal) return;
    const snapJson = JSON.stringify(S.spec);
    const pos = x != null ? { x, y } : lastMouse();
    hideGizmo();
    S.modal = new Modal({
      vp, type: 'param', x: pos.x, y: pos.y, param: { label, unit, value, perPx, min, max, step }, snap: S.view.snap,
      readout: $('#xf-readout'),
      restore: () => { S.spec = JSON.parse(snapJson); },
      apply: op => { set(S.spec, op.value); changed(false, { inspector: false }); return null; },
      done: ok => { S.modal = null; if (ok) changed(true); else { S.spec = JSON.parse(snapJson); changed(false); } updateStatus(); updateGizmo(); },
    });
    updateStatus();
  }

  function wingParam(field, label, unit, perPx, min, max) {
    const w = getItem(S.spec, S.edit.key);
    paramDrag({ label, unit, value: +w[field] || 0, perPx, min, max, step: 0.5, set: (spec, v) => { getItem(spec, S.edit.key)[field] = Math.round(v * 100) / 100; } });
  }

  function stripTransform(type, t, x, y) {
    const i = +String([...S.edit.sel][0]).replace(/^s/, '');
    const w = getItem(S.spec, t.key);
    const s = w.surfaces?.[i];
    if (!s) return;
    if (type === 'scale') {
      paramDrag({ label: 'Surface chord', unit: '%', value: +s.chord_pct, perPx: 0.2, min: 5, max: 60, step: 1, x, y, set: (spec, v) => { getItem(spec, t.key).surfaces[i].chord_pct = Math.round(v); } });
    } else if (type === 'translate') {
      const from = +s.from_pct, to = +s.to_pct;
      paramDrag({
        label: 'Slide along the span', unit: '%', value: 0, perPx: 0.25, min: -Math.min(from, to), max: 100 - Math.max(from, to), step: 1, x, y,
        set: (spec, v) => { const q = getItem(spec, t.key).surfaces[i]; q.from_pct = Math.round(from + v); q.to_pct = Math.round(to + v); },
      });
    } else say('Strips slide (G) and change their chord (S).');
  }

  let mouse = { x: 0, y: 0 };
  vpWrap.addEventListener('pointermove', e => { mouse = { x: e.clientX, y: e.clientY }; }, true);
  const lastMouse = () => {
    const r = vpWrap.getBoundingClientRect();
    return mouse.x >= r.left && mouse.x <= r.right && mouse.y >= r.top && mouse.y <= r.bottom ? mouse : { x: r.left + r.width / 2, y: r.top + r.height / 2 + 60 };
  };

  function showPropCircle() { if (S.modal?.pivot) vp.setPropCircle(S.modal.pivot, S.view.propRadius); }

  // The gizmo (Move / Rotate / Scale tools) runs the same transforms.
  function updateGizmo() {
    if (S.gizmoSession) return;
    const mode = { move: 'translate', rotate: 'rotate', scale: 'scale' }[S.tool];
    if (!mode || S.modal || S.pending) { vp.setGizmo(null); return; }
    let at = null;
    if (inEdit()) {
      if (editKind() === 'wing' && S.edit.mode === 'face') { vp.setGizmo(null); return; }
      const el = buildElements(S.spec, S.edit.key);
      const vs = [...selectedVerts(el, S.edit.mode, S.edit.sel)].map(id => el.verts.get(id).p);
      if (vs.length) at = vec.mul(vs.reduce(vec.add, [0, 0, 0]), 1 / vs.length);
    } else {
      const keys = movable();
      if (keys.length) at = keys.length === 1 ? originOf(S.spec, keys[0], S.sourceMeshes) : medianOf(S.spec, keys, S.sourceMeshes);
    }
    vp.setGizmoSnap(S.view.snap);
    vp.setGizmo(at ? mode : null, at);
  }
  function hideGizmo() { vp.setGizmo(null); }

  function gizmoStart() {
    const t = transformTargets();
    if (!t || t.strip) return;
    S.gizmoSession = { t, snap: JSON.stringify(S.spec) };
  }
  function gizmoChange(op) {
    const g = S.gizmoSession;
    if (!g) return;
    const n = g.t.apply({ ...op, pivot: g.t.pivot });   // absolute from the snapshot taken when the drag began
    g.note = n || g.note;
    changed(false, { inspector: false });
  }
  function gizmoEnd() {
    const g = S.gizmoSession;
    S.gizmoSession = null;
    if (!g) return;
    changed(true);
    if (g.note) say(g.note, 6000);
    updateGizmo();
  }

  // ------------------------------------------------------------ object commands

  function duplicate() {
    const keys = movable().filter(k => !k.startsWith('cockpit'));
    if (!keys.length) return;
    const copies = [];
    change(s => { for (const k of keys) { const d = duplicatePart(s, k); if (d) copies.push(addPart(s, d.kind, d.item)); } }, false, { outline: true });
    setSelection(copies);
    startTransform('translate', { after: cancelled => { if (cancelled) { change(s => { for (const k of copies) removePart(s, k); }, false, { outline: true }); setSelection(keys); } } });
    say(`Duplicated ${copies.length}. Move it into place and click (Esc puts it back).`, 5000);
  }

  function del(x, y) {
    if (inEdit()) {
      const k = editKind();
      if (k === 'fuselage') return deleteRings();
      if (k === 'shape') {
        const p = x != null ? { x, y } : lastMouse();
        return showMenu(p.x, p.y, 'Delete', [
          { label: 'Vertices', icon: 'vert', run: () => shapeOp('delete', { what: 'verts' }) },
          { label: 'Edges', icon: 'edge', run: () => shapeOp('delete', { what: 'edges' }) },
          { label: 'Faces', icon: 'face', run: () => shapeOp('delete', { what: 'faces' }) },
        ]);
      }
      if (k === 'wing' && S.edit.mode === 'face') {
        const i = +String([...S.edit.sel][0]).replace(/^s/, '');
        change(s => { getItem(s, S.edit.key).surfaces.splice(i, 1); }, true);
        S.edit.sel = new Set(); editSelChanged();
      }
      return;
    }
    const keys = S.selected.filter(k => k !== 'plane');
    if (!keys.length) return;
    const names = keys.map(k => itemName(S.spec, k));
    change(s => { for (const k of keys) removePart(s, k); }, true, { outline: true });
    setSelection([]);
    toast(keys.length === 1 ? `Deleted "${names[0]}".` : `Deleted ${keys.length} parts.`, { action: { label: 'Undo', run: undo } });
  }

  function frameSelected() {
    if (inEdit()) {
      const el = buildElements(S.spec, S.edit.key);
      const vs = [...selectedVerts(el, S.edit.mode, S.edit.sel)].map(id => el.verts.get(id).p);
      const pts = vs.length ? vs : [...el.verts.values()].map(v => v.p);
      const min = [0, 1, 2].map(i => Math.min(...pts.map(p => p[i]))), max = [0, 1, 2].map(i => Math.max(...pts.map(p => p[i])));
      const c = min.map((v, i) => (v + max[i]) / 2), h = Math.max(0.8, ...max.map((v, i) => (v - min[i]) / 2));
      vp.frameBox([c.map(v => v - h), c.map(v => v + h)]);
      return;
    }
    const keys = S.selected.filter(k => k !== 'plane' && !k.startsWith('reference:'));
    vp.frameBox(keys.length ? vp.boxOf(keys) : null);
  }

  /** Puts the camera at the pilot's eye looking forward, to check the interior and instruments. */
  function cockpitView() {
    const c = S.spec.cockpit;
    if (!c) { say('Add a cockpit first: Shift+A, then Cockpit.'); return; }
    if (S.view.quad) setView({ quad: false });
    vp.setCockpitView(eyeOf(c));
    say('Cockpit view: looking forward from the pilot\'s eye. Middle-drag looks around, the wheel zooms; Top, Front, Side or 3D leaves it.', 7000);
  }

  function setView(o) {
    const before = { ...S.view };
    Object.assign(S.view, o);
    if ('quad' in o && o.quad !== before.quad) vp.setQuad(S.view.quad);
    if (('wire' in o && o.wire !== before.wire) || ('xray' in o && o.xray !== before.xray)) vp.setShading({ wire: S.view.wire, xray: S.view.xray });
    if ('markers' in o) { vp.setMarkersVisible(S.view.markers); root.querySelectorAll('[data-a=markers]').forEach(b => b.setAttribute('aria-pressed', String(S.view.markers))); }
    if ('measure' in o) showMeasure();
    if ('snap' in o) vp.setGizmoSnap(S.view.snap);
    if ('prop' in o) say(S.view.prop ? 'Proportional editing on: nearby vertices follow. Scroll while moving to change the circle.' : 'Proportional editing off.', S.view.prop ? 4500 : 2000);
    refreshEdit();
    renderHeader(); renderShelf(); updateViewName(); updateStatus();
    if ('prop' in o) renderInspector_();
  }

  function showMeasure() {
    if (!S.view.measure) { vp.setMeasure(null); return; }
    const keys = S.selected.filter(k => k !== 'plane' && !k.startsWith('reference:'));
    vp.setMeasure(vp.boxOf(keys.length ? keys : null));
  }

  // ------------------------------------------------------------ adding parts

  function addMenu(x, y, section = null) {
    const p = x != null ? { x, y } : lastMouse();
    const a = S.analysis;
    const put = (kind, item, msg = null) => { exitEdit(); let k; change(s => { k = addPart(s, kind, item); }, true, { outline: true }); setSelection([k]); say(msg || `Added "${itemName(S.spec, k)}". Press G to move it, or Tab to shape it.`, msg ? 7000 : 4500); };
    const inst = kind => put('instrument', D.instrument(a, S.spec, kind), `Added "${D.INSTRUMENT_KINDS[kind].name}" in front of the pilot${S.spec.cockpit ? '' : ' (add a cockpit to set where the pilot sits)'}. Cockpit view (Numpad 0) shows it from the seat; G moves it.`);
    const lamp = kind => put('light', D.light(a, S.spec, kind), `Added "${D.LIGHT_KINDS[kind].name}". G moves it; it glows in the game.`);
    const prim = async (kind, extra = {}) => {
      try {
        const r = await api.post('/api/shapes/primitive', { kind });
        const L = D.layout(a);
        put('shape', { ...r.shape, position: [0, Math.round((L.cgY + 0.2) * 100) / 100, Math.round(L.cgZ * 100) / 100], ...extra });
      } catch (e) { showError(e); }
    };
    const canopy = () => {
      const c = S.spec.cockpit?.position || [0, 0.8, 2];
      prim('capsule', { name: 'Canopy', material: 'glass', position: [0, Math.round((c[1] + 0.05) * 100) / 100, Math.round(c[2] * 100) / 100], scale: [0.55, 0.45, 0.75], subdivision: 1 });
    };
    const groups = {
      bodies: [
        { label: 'Fuselage (rings)', icon: 'body', run: () => put('fuselage', D.fuselage(a)) },
        { label: 'Canopy (glass)', icon: 'cockpit', run: canopy },
        { label: 'Box', icon: 'cube', run: () => prim('box') },
        { label: 'Cylinder', icon: 'cube', run: () => prim('cylinder') },
        { label: 'Sphere', icon: 'cube', run: () => prim('sphere') },
        { label: 'Cone', icon: 'cube', run: () => prim('cone') },
        { label: 'Capsule', icon: 'cube', run: () => prim('capsule') },
        { label: 'Import .obj…', icon: 'import', run: () => importObj() },
      ],
      wings: [
        { label: 'Wing', icon: 'wing', run: () => put('wing', D.wing(a)) },
        { label: 'Tailplane', icon: 'wing', run: () => put('wing', D.tailplane(a)) },
        { label: 'Fin', icon: 'wing', run: () => put('wing', D.fin(a)) },
      ],
      engines: [
        { label: 'Jet engine', icon: 'engine', run: () => put('engine', D.jet(a)) },
        { label: 'Propeller engine', icon: 'engine', run: () => put('engine', D.prop(a)) },
      ],
      gear: [
        { label: 'Wheel', icon: 'gear', run: () => put('gear', D.gear(a)) },
        { label: 'Auto-place all gear', icon: 'magic', run: () => autoGear() },
      ],
      weapons: [
        { label: 'Hardpoint', icon: 'weapon', run: () => put('hardpoint', D.hardpoint(a)) },
        { label: 'Gun', icon: 'weapon', run: () => put('gun', D.gun(a)) },
      ],
      cockpit: [{ label: 'Cockpit', icon: 'cockpit', disabled: !!S.spec.cockpit, run: () => put('cockpit', D.cockpit(a)) }],
      instruments: Object.entries(D.INSTRUMENT_KINDS).map(([k, d]) => ({ label: d.label, icon: 'prop', run: () => inst(k) })),
      lights: Object.entries(D.LIGHT_KINDS).map(([k, d]) => ({ label: d.label, icon: 'magic', run: () => lamp(k) })),
      references: [
        { label: 'Top view picture…', icon: 'image', run: () => addReference('top') },
        { label: 'Side view picture…', icon: 'image', run: () => addReference('side') },
        { label: 'Front view picture…', icon: 'image', run: () => addReference('front') },
      ],
    };
    if (section && groups[section]) return showMenu(p.x, p.y, 'Add', groups[section]);
    showMenu(p.x, p.y, 'Add', [
      { label: 'Body or shape', icon: 'body', sub: groups.bodies },
      { label: 'Wing, tail or fin', icon: 'wing', sub: groups.wings },
      { label: 'Engine', icon: 'engine', sub: groups.engines },
      { label: 'Landing gear', icon: 'gear', sub: groups.gear },
      { label: 'Weapon', icon: 'weapon', sub: groups.weapons },
      { label: 'Cockpit', icon: 'cockpit', disabled: !!S.spec.cockpit, run: () => put('cockpit', D.cockpit(a)) },
      { label: 'Instrument', icon: 'prop', sub: groups.instruments },
      { label: 'Light', icon: 'magic', sub: groups.lights },
      'sep',
      { label: 'Reference image', icon: 'image', sub: groups.references },
    ]);
  }

  function pickFile(accept) {
    return new Promise(resolve => {
      const input = document.createElement('input');
      input.type = 'file'; input.accept = accept;
      input.onchange = () => resolve(input.files[0] || null);
      input.click();
    });
  }

  async function importObj(file = null) {
    const f = file || await pickFile('.obj');
    if (!f) return;
    try {
      const r = await api.upload(`/api/shapes/import-obj?name=${encodeURIComponent(f.name)}`, f);
      exitEdit();
      let k;
      change(s => { k = addPart(s, 'shape', r.shape); }, true, { outline: true });
      setSelection([k]);
      vp.frameBox(vp.boxOf([k]));
      toast(r.warning || `Imported "${r.shape.name}" (${(r.triangles || 0).toLocaleString()} triangles) as a shape.`, { kind: r.warning ? 'info' : 'good', timeout: 9000 });
    } catch (e) { showError(e, "Couldn't import the .obj:"); }
  }

  async function makeEditable() {
    const key = S.active;
    const f = getItem(S.spec, key);
    if (!f || f.kind !== 'mesh') return;
    try {
      const r = await withMeshSet(() => api.post('/api/shapes/from-mesh', { mesh_set: S.meshSet, fuselage: f }));
      let k;
      const hidden = isHidden(key);
      change(s => { removePart(s, key); k = addPart(s, 'shape', r.shape); if (hidden) s.editor.hidden.push(k); }, true, { outline: true });
      setSelection([k]);
      const n = (r.shape.vertices || []).length;
      say(`"${r.shape.name}" is now a shape you can edit (${n.toLocaleString()} vertices). Press Tab to edit it.`, 7000);
    } catch (e) { showError(e, "Couldn't make it editable:"); }
  }

  async function addReference(view = null, file = null) {
    const f = file || await pickFile('image/png,image/jpeg,image/webp,.png,.jpg,.jpeg,.webp');
    if (!f) return;
    try {
      const r = await api.upload(`/api/references?name=${encodeURIComponent(f.name)}`, f);
      const rig = vp.activeRig;
      const v = view || (rig.view === 'top' ? 'top' : rig.view === 'right' ? 'side' : rig.view === 'front' ? 'front' : 'top');
      const ref = D.reference(S.analysis, r.image, v);
      ref.name = f.name.replace(/\.[a-z0-9]+$/i, '') || ref.name;
      exitEdit();
      let k;
      change(s => { k = addPart(s, 'reference', ref); }, true, { outline: true });
      setSelection([k]);
      vp.setView(v === 'side' ? 'right' : v);
      vp.frameBox(null);
      say('Reference added. Set its scale from a known length on the right, then move it with G.', 7000);
    } catch (e) { showError(e, "Couldn't add the picture:"); }
  }

  function calibrate() {
    const key = S.active;
    const r = getItem(S.spec, key);
    if (!r) return;
    vp.setView(r.view === 'side' ? 'right' : r.view || 'top');
    S.pending = { type: 'calibrate', key, pts: [] };
    hideGizmo();
    say('Click the first point on the picture.', 8000);
    updateStatus();
  }

  // ------------------------------------------------------------ fuselage ring tools

  const ringsOfSelection = () => (S.edit.mode === 'ring' ? [...S.edit.sel] : [...new Set([...S.edit.sel].map(v => +String(v).split(':')[0]))]);

  function deleteRings() {
    const key = S.edit.key;
    const f = getItem(S.spec, key);
    const ids = ringsOfSelection();
    if (f.rings.length - ids.length < 2) { say('A fuselage needs at least two rings.'); return; }
    change(s => { const it = getItem(s, key); it.rings = it.rings.filter((_, i) => !ids.includes(i)); }, true);
    S.edit.sel = new Set(); editSelChanged();
    say(`Deleted ${ids.length} ring${ids.length === 1 ? '' : 's'}.`);
  }

  function extrude() {
    if (editKind() === 'shape') return shapeOp('extrude');
    const key = S.edit.key;
    const f = getItem(S.spec, key);
    const ring = ringsOfSelection()[0];
    if (ring == null || !f.rings[ring]) { say('Select a ring to extrude.'); return; }
    let res;
    change(s => { res = extrudeRing(getItem(s, key), ring); }, false);
    S.edit.mode = 'ring';
    S.edit.sel = new Set([res.index]);
    renderHeader();
    refreshEdit();
    startTransform('translate', { axis: 'z', label: 'Extrude ring', after: cancelled => { if (cancelled) { change(s => { getItem(s, key).rings.pop(); }, false); S.edit.sel = new Set([ring]); editSelChanged(); } } });
  }

  function startLoopCut() {
    S.pending = { type: 'loopcut', kind: editKind() };
    hideGizmo();
    say(editKind() === 'fuselage' ? 'Point between two rings and click to add a ring there.' : 'Point at an edge and click: the cut runs across the quads.', 6000);
    updateStatus();
  }

  function cancelPending() {
    if (!S.pending) return;
    S.pending = null;
    vp.setPreviewLine(null);
    updateGizmo(); updateStatus();
  }

  function loopCutTarget(e) {
    const key = S.edit.key;
    const it = getItem(S.spec, key);
    const vpr = vp.viewportAt(e.clientX, e.clientY);
    if (S.pending.kind === 'fuselage') {
      const order = orderedRings(it);
      const T = fuselageTransform(it);
      let best = null;
      for (let j = 0; j + 1 < order.length; j++) {
        const a = vp.projectIn(T.toPlane([0, +order[j].y || 0, +order[j].z]), vpr);
        const b = vp.projectIn(T.toPlane([0, +order[j + 1].y || 0, +order[j + 1].z]), vpr);
        const dx = b.cx - a.cx, dy = b.cy - a.cy, L2 = dx * dx + dy * dy || 1;
        let t = ((e.clientX - a.cx) * dx + (e.clientY - a.cy) * dy) / L2;
        t = Math.max(0.05, Math.min(0.95, t));
        const d = Math.hypot(a.cx + dx * t - e.clientX, a.cy + dy * t - e.clientY);
        if (!best || d < best.d) best = { j, t: Math.round(t * 20) / 20, d };
      }
      if (!best || best.d > 200) return null;
      const ring = ringBetween(it, best.j, best.t);
      const n = clampSides(it.sides);
      const pts = ringOffsets(ring, n).map(o => T.toPlane([o[0], (+ring.y || 0) + o[1], ring.z + o[2]]));
      return { ...best, ring, pts, closed: true };
    }
    // Shape: the nearest edge, then its ring of edges across quads.
    const el = buildElements(S.spec, key);
    let best = null;
    for (const ed of el.edges) {
      const a = vp.projectIn(el.verts.get(ed.a).p, vpr), b = vp.projectIn(el.verts.get(ed.b).p, vpr);
      const d = segDist(e.clientX, e.clientY, a.cx, a.cy, b.cx, b.cy);
      if (!best || d < best.d) best = { d, a: ed.a, b: ed.b };
    }
    if (!best || best.d > 30) return null;
    const ringEdges = edgeRing(it, best.a, best.b);
    const pts = ringEdges.map(([a, b]) => vec.lerp(el.verts.get(a).p, el.verts.get(b).p, 0.5));
    return { edge: [best.a, best.b], pts, closed: pts.length > 2 };
  }

  function pendingHover(e) {
    if (S.pending.type !== 'loopcut') return;
    const t = loopCutTarget(e);
    S.pending.target = t;
    vp.setPreviewLine(t?.pts || null, t?.closed);
    const ro = $('#xf-readout');
    if (t && t.t != null) { ro.hidden = false; ro.innerHTML = `<b>Loop cut</b><span class="val">${Math.round(t.t * 100)}%</span><span class="muted">between ring ${t.j + 1} and ${t.j + 2} · click to cut · Esc cancels</span>`; }
    else if (t) { ro.hidden = false; ro.innerHTML = `<b>Loop cut</b><span class="muted">${t.pts.length} edges · click to cut · Esc cancels</span>`; }
    else ro.hidden = true;
  }

  async function pendingClick(e) {
    const p = S.pending;
    if (p.type === 'loopcut') {
      const t = loopCutTarget(e);
      $('#xf-readout').hidden = true;
      S.pending = null;
      vp.setPreviewLine(null);
      if (!t) { updateStatus(); updateGizmo(); return; }
      const key = S.edit.key;
      if (p.kind === 'fuselage') {
        let idx;
        change(s => { const it = getItem(s, key); it.rings.push(t.ring); idx = it.rings.length - 1; }, true);
        S.edit.mode = 'ring'; S.edit.sel = new Set([idx]); S.edit.active = idx;
        renderHeader(); editSelChanged();
        say('Ring added. G moves it, S scales it.');
      } else await shapeOp('loopcut', { edge: t.edge, cuts: 1, factor: 0.5 });
      updateStatus(); updateGizmo();
      return;
    }
    if (p.type === 'calibrate') {
      const hit = vp.pickReference(e.clientX, e.clientY);
      if (!hit || hit.id !== parseKey(p.key).id) { say('Click on the picture (look at it straight from its side).'); return; }
      p.pts.push(hit.point);
      if (p.pts.length === 1) { vp.setPreviewLine([hit.point, vec.add(hit.point, [0, 0.001, 0])]); say('Now click the second point.', 8000); updateStatus(); return; }
      vp.setPreviewLine(p.pts);
      S.pending = null;
      const measured = vec.len(vec.sub(p.pts[1], p.pts[0]));
      const v = await promptDialog({ title: 'How long is that in real life?', body: `The line is ${fmt(measured, 2)} m on the picture now. Type its real length in metres (for example the wingspan).`, label: 'Real length (m)', value: '', confirm: 'Set scale' });
      vp.setPreviewLine(null);
      const real = parseFloat(String(v || '').replace(',', '.'));
      if (Number.isFinite(real) && real > 0 && measured > 1e-4) {
        const k = real / measured;
        change(s => {
          const r = getItem(s, p.key);
          const a = p.pts[0];
          const c = r.center || [0, 0];
          const pa = r.view === 'side' ? [a[2], a[1]] : r.view === 'front' ? [a[0], a[1]] : [a[0], a[2]];
          r.width_m = Math.round((+r.width_m || 10) * k * 1e4) / 1e4;
          r.center = [pa[0] + (c[0] - pa[0]) * k, pa[1] + (c[1] - pa[1]) * k].map(x => Math.round(x * 1e4) / 1e4);
        }, true);
        say(`Scaled the picture by ${fmt(k, 3)}×. Now move it (G) so it lines up with your plane.`, 7000);
      }
      updateStatus(); updateGizmo();
    }
  }

  function applyRingPreset(id) {
    const key = S.edit?.key || S.active;
    const f = getItem(S.spec, key);
    if (!f?.rings) return;
    let rings = S.edit ? ringsOfSelection() : [];
    if (!rings.length) rings = f.rings.map((_, i) => i);
    change(s => { const it = getItem(s, key); for (const i of rings) if (it.rings[i] && (+it.rings[i].width > 1e-3 || +it.rings[i].height > 1e-3 || it.rings[i].points)) it.rings[i] = applyPreset(it.rings[i], id, it.sides); }, true);
    say(`${rings.length} ring${rings.length === 1 ? '' : 's'} changed.`);
  }

  function setSides(n) {
    const key = S.edit?.key || S.active;
    change(s => {
      const it = getItem(s, key);
      // Custom rings are resampled so they keep their shape with the new number of points.
      it.rings = it.rings.map(r => (Array.isArray(r.points) && r.points.length >= 3 ? { ...r, points: resampleRing(r.points.map(p => [+p[0], +p[1], +p[2] || 0]), clampSides(n)).map(p => p.map(v => Math.round(v * 1e5) / 1e5)) } : r));
      it.sides = n;
    }, true);
  }

  function insertRingAfter(n) {
    const key = S.edit?.key || S.active;
    const f = getItem(S.spec, key);
    const order = orderedRings(f);
    const nr = n + 1 < order.length ? ringBetween(f, n, 0.5) : { ...JSON.parse(JSON.stringify(order[n])), z: Math.round((order[n].z - 1) * 100) / 100 };
    change(s => { getItem(s, key).rings.push(nr); }, true);
  }

  function addEndRing(nose) {
    const key = S.edit?.key || S.active;
    const f = getItem(S.spec, key);
    const order = orderedRings(f);
    const ref = (nose ? order[0] : order[order.length - 1]) || { z: 0, y: 0, width: 1, height: 1, roundness: 1 };
    const nr = { ...JSON.parse(JSON.stringify(ref)), z: Math.round((ref.z + (nose ? 1 : -1)) * 100) / 100, width: ref.width * 0.7, height: ref.height * 0.7 };
    if (Array.isArray(nr.points)) nr.points = nr.points.map(p => [p[0] * 0.7, p[1] * 0.7, p[2] || 0]);
    change(s => { getItem(s, key).rings.push(nr); }, true);
  }

  // ------------------------------------------------------------ shape ops (on the server, where they're tested)

  async function shapeOp(op, params = {}) {
    const key = S.edit.key;
    const sh = getItem(S.spec, key);
    let selIds = [...S.edit.sel];
    // With X mirror on, the mirrored half of the selection takes part too.
    if (S.view.mirrorX && (op === 'extrude' || op === 'inset' || op === 'delete' || op === 'flip')) {
      const el = buildElements(S.spec, key);
      if (S.edit.mode === 'vert') for (const v of [...selIds]) { const p = mirrorPartner(el, sh, v); if (p != null && !selIds.includes(p)) selIds.push(p); }
      if (S.edit.mode === 'face') {
        const vsel = selectedVerts(el, 'face', new Set(selIds));
        const mv = new Set([...vsel].map(v => mirrorPartner(el, sh, v) ?? v));
        for (const f of el.faces) if (!selIds.includes(f.id) && f.verts.every(v => mv.has(v))) selIds.push(f.id);
      }
    }
    const mode = S.edit.mode;
    const selection = { mode, verts: mode === 'vert' ? selIds : [], edges: mode === 'edge' ? selIds.map(id => String(id).split('|').map(Number)) : [], faces: mode === 'face' ? selIds : [] };
    let r;
    try { r = await api.post('/api/shapes/op', { shape: sh, op, selection, params }); }
    catch (e) { showError(e); return; }
    const before = JSON.stringify(S.spec);
    const beforeSel = new Set(S.edit.sel);
    change(s => { const it = getItem(s, key); it.vertices = r.shape.vertices; it.faces = r.shape.faces; }, false);
    const rs = r.selection || {};
    const nextSel = mode === 'vert' ? rs.verts : mode === 'edge' ? (rs.edges || []).map(([a, b]) => (a < b ? `${a}|${b}` : `${b}|${a}`)) : rs.faces;
    S.edit.sel = new Set(nextSel || []);
    if (op === 'loopcut') { S.edit.mode = 'vert'; S.edit.sel = new Set(rs.verts || []); renderHeader(); }
    editSelChanged();
    if (r.note) say(r.note, 7000);
    if (r.motion?.length && (op === 'extrude' || op === 'inset')) {
      const motion = r.motion;
      const T = shapeTransform(getItem(S.spec, key));
      const o = vec.mul(motion.map(m => m.origin).reduce(vec.add, [0, 0, 0]), 1 / motion.length);
      const d = vec.norm(vec.mul(motion.map(m => m.dir).reduce(vec.add, [0, 0, 0]), 1 / motion.length));
      const pivot = T.toPlane(o), dirPlane = vec.sub(T.toPlane(vec.add(o, d)), pivot);
      const snapJson = JSON.stringify(S.spec);
      hideGizmo();
      const pos = lastMouse();
      S.modal = new Modal({
        vp, type: 'slide', pivot, x: pos.x, y: pos.y, label: op === 'extrude' ? 'Extrude' : 'Inset', snap: S.view.snap,
        slide: { dir: dirPlane, mode: op, max: op === 'inset' ? r.max_amount ?? null : null },
        readout: $('#xf-readout'),
        restore: () => { S.spec = JSON.parse(snapJson); },
        apply: opv => {
          const it = getItem(S.spec, key);
          const vs = it.vertices.map(v => [...v]);
          for (const m of motion) vs[m.v] = m.origin.map((c, i) => Math.round((c + m.dir[i] * opv.amount) * 1e5) / 1e5);
          it.vertices = vs;
          changed(false, { inspector: false });
          return null;
        },
        done: ok => {
          S.modal = null;
          if (ok) changed(true);
          else { S.spec = JSON.parse(before); S.edit.sel = beforeSel; changed(false); editSelChanged(); }
          updateStatus(); updateGizmo();
        },
      });
      updateStatus();
    } else changed(true);
  }

  function mergeMenu(x, y) {
    const p = x != null ? { x, y } : lastMouse();
    showMenu(p.x, p.y, 'Merge', [
      { label: 'At centre', icon: 'merge', run: () => shapeOp('merge', { mode: 'center' }) },
      { label: 'At first', icon: 'merge', run: () => shapeOp('merge', { mode: 'first' }) },
      { label: 'At last', icon: 'merge', run: () => shapeOp('merge', { mode: 'last' }) },
      { label: 'By distance (1 mm)', icon: 'merge', run: () => shapeOp('merge_distance', { distance: 0.001 }) },
    ]);
  }

  // ------------------------------------------------------------ right-click menu

  function contextMenu(x, y) {
    if (S.modal || S.pending) return;
    if (inEdit()) {
      const k = editKind();
      const items = [
        { label: 'Move', icon: 'move', keys: 'G', run: () => run('grab') },
        { label: 'Rotate', icon: 'rotate', keys: 'R', run: () => run('rotate') },
        { label: 'Scale', icon: 'scale', keys: 'S', run: () => run('scale') },
        'sep',
      ];
      if (k === 'fuselage') items.push({ label: 'Extrude ring', icon: 'extrude', keys: 'E', run: () => run('extrude') }, { label: 'Loop cut', icon: 'loopcut', keys: 'Ctrl+R', run: () => run('loopcut') },
        { label: 'Cross-section', icon: 'ring', sub: [['circle', 'Circle'], ['oval', 'Oval'], ['square', 'Rounded square'], ['teardrop', 'Teardrop'], ['flat', 'Flat bottom']].map(([id, l]) => ({ label: l, run: () => applyRingPreset(id) })) },
        { label: 'Delete ring', icon: 'trash', keys: 'X', run: () => run('delete') },
        { label: `Smooth the body: ${['off', '×2', '×4', '×8'][+getItem(S.spec, S.edit.key).smooth || 0]}`, icon: 'smooth', keys: 'Shift+S', run: () => run('smoothBody') });
      if (k === 'shape') items.push({ label: 'Extrude', icon: 'extrude', keys: 'E', run: () => run('extrude') }, { label: 'Inset', icon: 'inset', keys: 'I', run: () => run('inset') },
        { label: 'Loop cut', icon: 'loopcut', keys: 'Ctrl+R', run: () => run('loopcut') }, { label: 'Merge', icon: 'merge', keys: 'M', sub: [
          { label: 'At centre', run: () => shapeOp('merge', { mode: 'center' }) }, { label: 'At first', run: () => shapeOp('merge', { mode: 'first' }) },
          { label: 'At last', run: () => shapeOp('merge', { mode: 'last' }) }, { label: 'By distance', run: () => shapeOp('merge_distance', { distance: 0.001 }) }] },
        { label: 'Subdivide', icon: 'smooth', run: () => run('subdivideApply') }, { label: 'Make faces point outwards', icon: 'refresh', keys: 'Shift+N', run: () => run('recalc') },
        { label: 'Flip faces', icon: 'mirror', run: () => run('flip') },
        { label: 'Delete', icon: 'trash', keys: 'X', sub: [{ label: 'Vertices', run: () => shapeOp('delete', { what: 'verts' }) }, { label: 'Edges', run: () => shapeOp('delete', { what: 'edges' }) }, { label: 'Faces', run: () => shapeOp('delete', { what: 'faces' }) }] });
      if (k === 'wing') items.push({ label: 'Sweep', icon: 'wing', run: () => run('wingSweep') }, { label: 'Dihedral', icon: 'wing', run: () => run('wingDihedral') }, { label: 'Twist', icon: 'rotate', run: () => run('wingTwist') });
      items.push('sep', { label: 'Select all', keys: 'A', run: () => run('selectAll') }, { label: 'Select none', keys: 'Alt+A', run: () => run('selectNone') },
        { label: S.view.prop ? 'Proportional editing: on' : 'Proportional editing: off', icon: 'prop', keys: 'O', run: () => run('prop') },
        { label: 'Back to Object mode', icon: 'edit', keys: 'Tab', run: () => run('toggleEdit') });
      showMenu(x, y, `Edit · ${itemName(S.spec, S.edit.key)}`, items);
      return;
    }
    // Object mode: right-clicking a part selects it first (like the outliner).
    const hit = vp.pickObject(x, y);
    if (hit && !S.selected.includes(hit.key)) setSelection([hit.key]);
    const any = S.selected.some(k => k !== 'plane');
    const k = S.active;
    const it = k ? getItem(S.spec, k) : null;
    const items = any ? [
      { label: 'Move', icon: 'move', keys: 'G', run: () => run('grab') },
      { label: 'Rotate', icon: 'rotate', keys: 'R', run: () => run('rotate') },
      { label: 'Scale', icon: 'scale', keys: 'S', run: () => run('scale') },
      'sep',
      editModes(S.spec, k) ? { label: 'Edit mode', icon: 'edit', keys: 'Tab', run: () => run('toggleEdit') } : null,
      isFixedMesh(k) ? { label: 'Make editable', icon: 'cube', run: () => run('makeEditable') } : null,
      { label: 'Duplicate', icon: 'copy', keys: 'Shift+D', disabled: !C.duplicate.enabled(), run: () => run('duplicate') },
      it && 'mirror' in it ? { label: it.mirror ? 'Mirror: on (×2)' : 'Mirror: off', icon: 'mirror', run: () => run('mirrorPart') } : null,
      { label: 'Rename', icon: 'edit', keys: 'F2', disabled: !C.rename.enabled(), run: () => run('rename') },
      { label: 'Hide', icon: 'eyeOff', keys: 'H', run: () => run('hide') },
      { label: 'Frame selected', icon: 'frame', keys: 'F', run: () => run('frameSel') },
      'sep',
      { label: 'Delete', icon: 'trash', keys: 'X', run: () => run('delete') },
    ] : [
      { label: 'Add', icon: 'plus', keys: 'Shift+A', run: () => addMenu(x, y) },
      { label: 'Select all', icon: 'select', keys: 'A', run: () => run('selectAll') },
      { label: 'Show everything', icon: 'eye', keys: 'Alt+H', run: () => run('unhide') },
      { label: 'Frame all', icon: 'frame', keys: 'Home', run: () => run('frameAll') },
      'sep',
      { label: 'View', icon: 'cube', sub: [
        { label: 'Top', keys: 'Numpad 7', run: () => vp.setView('top') }, { label: 'Front', keys: 'Numpad 1', run: () => vp.setView('front') },
        { label: 'Right', keys: 'Numpad 3', run: () => vp.setView('right') }, { label: 'Perspective / ortho', keys: 'Numpad 5', run: () => vp.toggleOrtho() },
        { label: 'Cockpit view', keys: 'Numpad 0', disabled: !S.spec.cockpit, run: () => run('viewCockpit') },
        { label: 'Quad view', keys: 'Ctrl+Alt+Q', run: () => run('quad') }] },
      { label: 'Keyboard and mouse…', icon: 'help', keys: '?', run: () => run('help') },
    ];
    showMenu(x, y, any ? (S.selected.length > 1 ? `${S.selected.length} parts` : itemName(S.spec, k)) : 'Plane', items);
  }

  // ------------------------------------------------------------ whole-plane tools

  async function autoGear() {
    try {
      const r = await withMeshSet(() => api.post('/api/autogear', { spec: S.spec, mesh_set: S.meshSet }));
      change(s => { s.gear = r.gear; }, true, { outline: true });
      setSelection(r.gear.map(g => keyOf('gear', g.id)));
      toast('Placed a nose wheel and two main wheels under the plane.', { kind: 'good' });
    } catch (e) { showError(e); }
  }

  function resizePlane(f) {
    const r = v => Math.round(v * 1000) / 1000;
    const sc = a => a && a.map(v => r(v * f));
    change(s => {
      for (const fu of s.fuselages || []) {
        if (fu.kind === 'mesh') {
          const c = meshCenter(S.sourceMeshes[fu.mesh_index]?.vertices || []);
          const o = fu.position || [0, 0, 0];
          fu.position = o.map((v, i) => r(v * f + (f - 1) * c[i]));
          fu.scale = r((fu.scale || 1) * f);
        } else {
          fu.position = sc(fu.position || [0, 0, 0]);
          fu.rings = (fu.rings || []).map(x => ({ ...x, z: r(x.z * f), y: r((x.y || 0) * f), width: r(x.width * f), height: r(x.height * f), ...(Array.isArray(x.points) ? { points: x.points.map(p => p.map(v => r(v * f))) } : {}) }));
        }
      }
      for (const sh of s.shapes || []) { sh.position = sc(sh.position || [0, 0, 0]); sh.scale = (sh.scale || [1, 1, 1]).map(v => r(v * f)); }
      for (const w of s.wings || []) {
        if (w.kind === 'panels') {
          const c = panelsPivot(w.panels || []);
          const o = w.offset || [0, 0, 0];
          w.offset = o.map((v, i) => r(v * f + (f - 1) * c[i]));
          w.scale = r((w.scale || 1) * f);
        } else { w.root = sc(w.root); w.span = r(w.span * f); w.root_chord = r(w.root_chord * f); w.tip_chord = r(w.tip_chord * f); }
      }
      for (const e of s.engines || []) e.position = sc(e.position);
      for (const g of s.gear || []) { g.position = sc(g.position); g.strut_length = r(g.strut_length * f); g.wheel_radius = r(g.wheel_radius * f); }
      for (const h of s.hardpoints || []) h.position = sc(h.position);
      for (const g of s.guns || []) g.position = sc(g.position);
      for (const n of s.instruments || []) n.position = sc(n.position);
      for (const l of s.lights || []) l.position = sc(l.position);
      if (s.cockpit) { s.cockpit.position = sc(s.cockpit.position); if (Array.isArray(s.cockpit.eye)) s.cockpit.eye = sc(s.cockpit.eye); }
    }, true, { outline: true });
    vp.frameBox(null);
    toast(`Resized the plane by ${f}×.`, { action: { label: 'Undo', run: undo } });
  }

  // ------------------------------------------------------------ analysis, readouts and click-to-fix checks

  let analyzeSeq = 0;
  const analyzeSoon = debounce(async () => {
    const seq = ++analyzeSeq;
    try {
      const a = await withMeshSet(() => api.post('/api/analyze', { spec: S.spec, mesh_set: S.meshSet }));
      if (seq !== analyzeSeq || S.destroyed) return;
      const first = !S.analysis;
      S.analysis = a;
      vp.setAnalysis(a);
      const auto = $('#inspector option[value="auto"]');
      if (auto) auto.textContent = `Automatic: ${donorLabel(a.donor)}`;
      renderReadouts();
      if (first) renderInspector_();
    } catch (e) {
      if (seq === analyzeSeq) $('#readouts').innerHTML = `<div class="notice bad" style="flex:1">${icon('warn')}<div>Couldn't update the readouts: ${esc(e.message)}</div></div>`;
    }
  }, 140);

  async function withMeshSet(fn) {
    try { return await fn(); }
    catch (e) {
      if (e.status === 409 && /mesh_set_unknown/.test(e.message)) {
        const r = await api.post('/api/meshsets', { meshes: S.sourceMeshes });
        S.meshSet = r.mesh_set;
        return await fn();
      }
      throw e;
    }
  }

  function renderReadouts() {
    const a = S.analysis;
    if (!a) return;
    const f = a.flight, st = a.stats;
    const g = (k, v, unit, title = '') => `<div class="gauge" title="${esc(title)}"><div class="k">${k}</div><div class="v">${v}${unit ? `<small>${unit}</small>` : ''}</div></div>`;
    const problems = f.checks.filter(c => c.level === 'problem').length;
    const warnings = f.checks.filter(c => c.level === 'warning').length + a.notes.filter(n => n.level === 'warning').length;
    const cg = f.center_of_mass;
    $('#readouts').innerHTML = `
      <div class="readouts">
        ${g('Empty', fmtKg(f.empty_mass_kg), '', 'Estimated empty weight')}
        ${g('Gross', fmtKg(f.gross_mass_kg), '', 'With fuel')}
        ${g('CG', fmt(cg[2], 2), 'm', 'Centre of gravity along the body (z, forward is +)')}
        ${g('NP', fmt(f.neutral_point_z, 2), 'm', 'Neutral point along the body (z)')}
        ${g('Margin', fmt(f.static_margin_percent, 0), '%', 'Static margin: how far the neutral point is behind the centre of gravity, in mean chords')}
        ${g('Thrust/weight', fmt(f.thrust_to_weight, 2), '')}
        ${g('Stall', fmt(f.stall_speed_knots, 0), 'kt', `${fmt(f.stall_speed_ms * 3.6, 0)} km/h`)}
        ${g('Span', fmt(st.span_m, 1), 'm')}
        ${g('Length', fmt(st.length_m, 1), 'm')}
        ${g('Wing area', fmt(f.wing_area_m2, 1), 'm²', 'Wings and tailplanes')}
        ${g('Loading', fmt(f.wing_loading_kg_m2, 0), 'kg/m²', 'Wing loading')}
      </div>
      <div class="stab"><span class="label-caps">Stability</span>${stabilityPill(f)}</div>
      <div class="checks-toggle">
        <button type="button" class="btn ${problems ? 'primary' : ''}" data-a="checks" aria-expanded="${!$('#checks').hidden}">
          ${icon(problems ? 'xCircle' : warnings ? 'warn' : 'checkCircle', 'sm')}${problems ? `${problems} problem${problems === 1 ? '' : 's'}` : warnings ? `${warnings} warning${warnings === 1 ? '' : 's'}` : 'All good'}
        </button>
      </div>`;
    $('#readouts [data-a=checks]').onclick = toggleChecks;
    if (!$('#checks').hidden) renderChecks();
  }

  /** A one-click fix for a flight check, when there is one: { label, run }. */
  function fixFor(c) {
    const m = c.message;
    const a = S.analysis;
    const add = (kind, item, label) => ({ label, run: () => { let k; change(s => { k = addPart(s, kind, item); }, true, { outline: true }); setSelection([k]); } });
    const area = w => (w.kind === 'panels' ? (w.panels || []).reduce((t, p) => t + quadArea(p.root_leading, p.root_trailing, p.tip_trailing, p.tip_leading), 0) * (w.scale || 1) ** 2 : (+w.span || 0) * ((+w.root_chord || 0) + (+w.tip_chord || 0)) / 2);
    const mainWing = () => (S.spec.wings || []).filter(w => !w.vertical).sort((x, y) => area(y) - area(x))[0];
    const addSurface = (w, surf, label) => ({ label, run: () => { change(s => { const it = getItem(s, keyOf('wing', w.id)); it.surfaces = [...(it.surfaces || []), surf]; }, true); setSelection([keyOf('wing', w.id)]); } });
    if (/no wings/.test(m)) return add('wing', D.wing(a), 'Add a wing');
    if (/no engine/.test(m)) return add('engine', D.jet(a), 'Add a jet');
    if (/no cockpit/.test(m)) return add('cockpit', D.cockpit(a), 'Add a cockpit');
    if (/no fin|controls yaw/.test(m)) {
      const fin = (S.spec.wings || []).find(w => w.vertical && w.kind !== 'panels');
      if (fin && /controls yaw/.test(m)) return addSurface(fin, { type: 'rudder', edge: 'trailing', from_pct: 0, to_pct: 100, chord_pct: 35 }, 'Add a rudder');
      return add('wing', D.fin(a), 'Add a fin');
    }
    if (/controls pitch/.test(m)) {
      const tail = (S.spec.wings || []).find(w => !w.vertical && w.kind !== 'panels' && /tail|stab/i.test(w.name));
      if (tail) return addSurface(tail, { type: 'elevator', edge: 'trailing', from_pct: 0, to_pct: 100, chord_pct: 35 }, 'Add an elevator');
      return add('wing', D.tailplane(a), 'Add a tailplane');
    }
    if (/controls roll/.test(m)) {
      const w = mainWing();
      if (w && w.kind !== 'panels') return addSurface(w, { type: 'aileron', edge: 'trailing', from_pct: 60, to_pct: 95, chord_pct: 25 }, 'Add ailerons');
    }
    if (/landing gear|three wheels/.test(m)) return { label: 'Auto-place gear', run: autoGear };
    if (/neutral point|unstable|Unstable|Very stable/.test(m) && a?.flight?.mean_chord_m > 0) {
      const sm = a.flight.static_margin_percent;
      const target = sm > 25 ? 15 : 8;
      // Moving the wing also moves some weight, so it takes about 1.6 times the naive distance.
      const dz = Math.round(((target - sm) / 100) * a.flight.mean_chord_m * 1.6 * 100) / 100;
      if (Math.abs(dz) < 0.01) return null;
      // Move the main wing only: the tail stays put, so the neutral point moves much more than the weight.
      const w = mainWing();
      if (!w) return null;
      const k = keyOf('wing', w.id);
      return { label: `Move the ${w.name || 'wing'} ${dz > 0 ? 'back' : 'forward'} ${fmt(Math.abs(dz), 2)} m`, run: () => {
        change(s => applyToPart(s, k, JSON.parse(JSON.stringify(getItem(s, k))), { type: 'translate', d: [0, 0, -dz] }, { sourceMeshes: S.sourceMeshes }), true);
        setSelection([k]);
        say('Moved the wing. The readouts update in a moment; click again if it needs more.');
      } };
    }
    if (/pitch reach/.test(m)) {
      // Builder tails: deeper elevators across the whole span. Converted tails: the tail panels 15 % larger.
      const pitchTypes = new Set(['elevator', 'elevon', 'ruddervator']);
      const param = (S.spec.wings || []).filter(w => w.kind !== 'panels' && (w.surfaces || []).some(sf => pitchTypes.has(sf.type)));
      if (param.length) return { label: 'Enlarge the elevator', run: () => {
        change(s => { for (const t of param) { const it = getItem(s, keyOf('wing', t.id)); it.surfaces = (it.surfaces || []).map(sf => pitchTypes.has(sf.type) ? { ...sf, chord_pct: Math.min(60, Math.round((+sf.chord_pct || 30) * 1.35)), from_pct: 0, to_pct: 100 } : sf); } }, true);
        setSelection([keyOf('wing', param[0].id)]);
        say('Made the elevator deeper and full span. The readouts update in a moment; click again if it needs more.');
      } };
      const pitchesPanel = p => (p.inputs || []).some(i => i.axis === 'pitch') || (p.surfaces || []).some(sf => (sf.inputs || []).some(i => i.axis === 'pitch'));
      const tails = (S.spec.wings || []).filter(w => w.kind === 'panels' && (w.panels || []).some(p => pitchesPanel(p) && Math.abs((p.up || [0, 1, 0])[1]) > 0.7));
      if (tails.length) return { label: 'Enlarge the tail 15%', run: () => {
        change(s => { for (const t of tails) { const it = getItem(s, keyOf('wing', t.id)); it.scale = Math.round((+it.scale || 1) * 1.15 * 1000) / 1000; } }, true);
        setSelection([keyOf('wing', tails[0].id)]);
        say('Made the tail 15 % larger. The readouts update in a moment; click again if it needs more.');
      } };
    }
    if (/blocks the view/.test(m) && S.spec.cockpit) {
      // The eye buried in the body: raise it 25 cm at a time and look from it.
      return { label: 'Raise the eye 25 cm', run: () => {
        change(s => {
          const c = s.cockpit;
          const eye = Array.isArray(c.eye) ? c.eye : [c.position[0], c.position[1] + 0.7, c.position[2]];
          c.eye = [eye[0], Math.round((eye[1] + 0.25) * 1000) / 1000, eye[2]];
        }, true);
        setSelection(['cockpit:cockpit']);
        cockpitView();
        say('Raised the eye 25 cm and switched to the cockpit view. Click again if the view is still blocked, or move the eye in the cockpit panel.');
      } };
    }
    if (/No hardpoints/.test(m)) return add('hardpoint', D.hardpoint(a), 'Add a pair of hardpoints');
    if (/runway to take off/.test(m) && c.level === 'warning') { const e = (S.spec.engines || [])[0]; if (e) return { label: 'Select the engine', run: () => setSelection([keyOf('engine', e.id)]) }; }
    if (/Underpowered/.test(m)) { const e = (S.spec.engines || [])[0]; if (e) return { label: 'Select the engine', run: () => setSelection([keyOf('engine', e.id)]) }; }
    if (/wing loading|Stall speed/.test(m)) { const w = mainWing(); if (w) return { label: 'Select the wing', run: () => setSelection([keyOf('wing', w.id)]) }; }
    return null;
  }

  function renderChecks() {
    const a = S.analysis;
    const box = $('#checks');
    if (!a) return;
    const order = { problem: 0, warning: 1, note: 2, good: 3 };
    const checks = [...a.flight.checks].sort((x, y) => order[x.level] - order[y.level]);
    const ic = l => icon(l === 'good' ? 'checkCircle' : l === 'note' ? 'info' : l === 'warning' ? 'warn' : 'xCircle');
    const fixes = [];
    box.innerHTML = `<h3>Flight check <button type="button" class="icon-btn" aria-label="Close">${icon('x')}</button></h3>
      ${takeoffSection(fixes)}
      <ul class="checks">${checks.map(c => {
        const fx = c.level !== 'good' && (c.level !== 'note' || /pitch reach/.test(c.message)) ? fixFor(c) : null;
        if (fx) fixes.push(fx);
        return `<li class="${c.level}">${ic(c.level)}<span class="grow">${esc(c.message)}</span>${fx ? `<button type="button" class="btn small fix" data-fix="${fixes.length - 1}">${icon('fix', 'sm')}${esc(fx.label)}</button>` : ''}</li>`;
      }).join('')}
      ${a.notes.map((n, i) => `<li class="${n.level === 'warning' ? 'warning' : 'note'}">${ic(n.level === 'warning' ? 'warning' : 'note')}<span class="grow">${n.item_id ? `<button type="button" class="note-link" data-note="${i}">${esc(n.message)}</button>` : esc(n.message)}</span></li>`).join('')}</ul>
      ${gameSection()}
      <p class="faint small" style="margin-top:8px">Estimates from the shape, not a flight simulation. The real test is flying it.</p>`;
    box.querySelector('h3 button').onclick = toggleChecks;
    box.querySelectorAll('[data-fix]').forEach(b => b.onclick = () => fixes[+b.dataset.fix].run());
    box.querySelectorAll('[data-note]').forEach(b => b.onclick = () => {
      const n = a.notes[+b.dataset.note];
      const kind = Object.keys(LISTS).find(k => (S.spec[LISTS[k]] || []).some(x => x.id === n.item_id));
      if (kind) { setSelection([keyOf(kind, n.item_id)]); frameSelected(); }
    });
  }

  /**
   * Takeoff at a glance: the run it needs against every Heartland runway, whether its elevator can lift the nose, and
   * one-click fixes (a bigger elevator, more thrust). Adds its fixes to <paramref name="fixes"/> (shared data-fix ids).
   */
  function takeoffSection(fixes) {
    const a = S.analysis, f = a?.flight, t = a?.takeoff;
    if (!f || !t || !(f.runway_needed_m > 0)) return '';
    const need = f.runway_needed_m;
    const runways = [...t.runways].sort((x, y) => x.length_m - y.length_m);
    const longest = runways.length ? runways[runways.length - 1].length_m : need;
    const usable = runways.filter(r => r.length_m >= need);
    const reach = f.pitch_reach;
    const reachOk = reach == null || reach >= t.min_pitch_reach;
    const twOk = f.thrust_to_weight >= 0.25;
    const push = (fx) => { fixes.push(fx); return fixes.length - 1; };
    const btns = [];
    if (!reachOk) {
      const fx = fixFor({ level: 'warning', message: 'pitch reach' });
      if (fx) btns.push(`<button type="button" class="btn small fix" data-fix="${push(fx)}">${icon('fix', 'sm')}${esc(fx.label)}</button>`);
    }
    // More thrust only helps when the plane's own run is what's too long (not the stock plane's minimum runway).
    const ownLimits = (t.own_run_m || 0) > (t.stock_min_m || 0);
    if (((usable.length < runways.length && ownLimits) || !twOk) && (S.spec.engines || []).length) {
      const fx = { label: 'More thrust (+20%)', run: () => {
        change(s => {
          for (const e of s.engines || []) {
            if (e.variant === 'piston' || e.variant === 'electric') e.power_kw = Math.round((+e.power_kw || 0) * 1.2);
            else if (e.sizing === 'designed') e.diameter_m = Math.round((+e.diameter_m || 0.9) * Math.sqrt(1.2) * 1000) / 1000;
            else { e.thrust_kn = Math.round((+e.thrust_kn || 0) * 12) / 10; if (+e.afterburner_kn > 0) e.afterburner_kn = Math.round(+e.afterburner_kn * 12) / 10; }
            e.estimated = false;
          }
        }, true);
        say('Gave every engine 20 % more thrust. The takeoff numbers update in a moment.');
      } };
      btns.push(`<button type="button" class="btn small fix" data-fix="${push(fx)}">${icon('fix', 'sm')}${esc(fx.label)}</button>`);
    }
    const bar = r => {
      const ok = r.length_m >= need;
      return `<li class="${ok ? 'ok' : 'short'}" title="${esc(r.name)}: ${fmt(r.length_m, 0)} m">
          <span class="rw-name">${esc(r.name.replace(/^the /, ''))}</span>
          <span class="rw-bar"><span style="width:${Math.min(100, r.length_m / longest * 100)}%"></span><i style="left:${Math.min(100, need / longest * 100)}%"></i></span>
          <span class="rw-len num">${fmt(r.length_m, 0)} m</span>${icon(ok ? 'check' : 'x', 'sm')}</li>`;
    };
    return `<div class="takeoff">
        <div class="takeoff-head"><strong>Takeoff</strong>
          <span class="pill ${usable.length ? 'good' : 'bad'}">${usable.length ? `${usable.length} of ${runways.length} Heartland airbases` : 'No runway is long enough'}</span></div>
        <div class="takeoff-facts">
          <span>Run <b class="num">~${fmt(need, 0)} m</b></span>
          <span>Thrust/weight <b class="num ${twOk ? '' : 'warn-text'}">${fmt(f.thrust_to_weight, 2)}</b></span>
          <span title="How hard the elevator can lift the nose against the plane's stability; the game needs about ${fmt(t.min_pitch_reach, 1)}. Below that OpenPlane rebalances the plane in the game, which changes how it handles.">Lifts its nose <b class="${reachOk || reach >= 0.4 ? '' : 'warn-text'}">${reach == null ? 'yes (its nose isn\'t held down: it has no pitch stability)' : reach <= 0 ? 'no (it has no elevator)' : reachOk ? `yes (${fmt(reach, 1)})` : reach >= 0.4 ? `with OpenPlane's help (${fmt(reach, 1)} of ${fmt(t.min_pitch_reach, 1)})` : `barely (${fmt(reach, 1)} of ${fmt(t.min_pitch_reach, 1)})`}</b></span>
        </div>
        <ul class="runways" aria-label="Heartland runways; the marker shows the run this plane needs">${runways.map(bar).join('')}</ul>
        ${!ownLimits && t.stock_min_m > 0 ? `<span class="faint small">Its own run is about ${fmt(t.own_run_m, 0)} m, but the game never offers a plane at a runway shorter than its stock plane's takeoff setting (${fmt(t.stock_min_m, 0)} m). A plane built on the CI-22 Cricket can use the short strips and carriers.</span>` : ''}
        ${btns.length ? `<div class="btn-row">${btns.join('')}</div>` : ''}
      </div>`;
  }

  /** What the game did with this plane the last time it started (from the mod's report), as a section of the flight check. */
  function gameSection() {
    const r = S.gameReport;
    if (!r) return '';
    const items = [
      ...(r.skipped ? [['problem', `The game didn't load it: ${r.skipped}`]] : []),
      ...(r.host_blocked ? [['warning', `Not flyable in games you host: ${r.host_blocked}. Change the limits in Settings, Multiplayer.`]] : []),
      ...(r.warnings || []).map(t => ['warning', t]),
      ...(r.changes || []).map(t => ['note', t]),
      ...((r.not_offered_at || []).length ? [['note', `Not offered at: ${r.not_offered_at.join(', ')} (runway too short; it needs about ${Math.round(r.runway_needed_m || 0)} m).`]] : []),
    ];
    if (!items.length) return '';
    const ic = l => icon(l === 'note' ? 'info' : l === 'warning' ? 'warn' : 'xCircle');
    return `<h4 class="label-caps" style="margin:12px 0 6px">The last time the game loaded it</h4>
      <ul class="checks">${items.map(([l, t]) => `<li class="${l}">${ic(l)}<span class="grow">${esc(t)}</span></li>`).join('')}</ul>
      <p class="faint small">From the game's own log of this file. If you changed the plane since, start the game again to see the new result.</p>`;
  }
  api.get('/api/game-report').then(g => {
    if (!g.found || !S.fileName) return;
    S.gameReport = (g.report?.planes || []).find(r => String(r.file || '').toLowerCase() === S.fileName.toLowerCase()) || null;
    if (S.gameReport && !$('#checks').hidden) renderChecks();
  }).catch(() => {});

  function toggleChecks() {
    const box = $('#checks');
    box.hidden = !box.hidden;
    if (!box.hidden) renderChecks();
    $('#readouts [data-a=checks]')?.setAttribute('aria-expanded', String(!box.hidden));
  }

  // ------------------------------------------------------------ drafts and saving

  // Each plane has its own draft (S.draftId: the plane's id, or "new-..." until a new plane is first saved).
  const draftUrl = () => `/api/drafts/${encodeURIComponent(S.draftId)}`;
  const draftBody = () => ({ spec: S.spec, mesh_set: S.meshSet, plane_id: S.planeId });
  async function saveDraftNow() {
    if (S.destroyed || !isDirty() || !S.draftId) return;
    try { await withMeshSet(() => api.put(draftUrl(), draftBody())); S.draftFailed = false; }
    catch (e) {
      // The next change tries again; say so once, so a player never assumes unsaved work is kept when it isn't.
      if (!S.draftFailed) say(`Couldn't keep a draft of your changes (${e?.message || e}). Save the plane to keep them.`, 9000);
      S.draftFailed = true;
    }
  }
  const draftSoon = debounce(saveDraftNow, 1200);

  /** The page is going away (window closed, reloaded): send the draft in a request the browser finishes on its own. */
  const onPageHide = () => {
    if (S.destroyed || !isDirty() || !S.draftId) return;
    draftSoon.cancel();
    const body = JSON.stringify(draftBody());
    try {
      fetch(draftUrl(), { method: 'PUT', headers: { 'X-OpenPlane': '1', 'Content-Type': 'application/json' }, body, keepalive: body.length < 60000 });
    } catch { /* nothing more can be done while the page closes */ }
  };
  window.addEventListener('pagehide', onPageHide);
  window.addEventListener('beforeunload', onPageHide);

  async function save({ asNew = false, name = null, force = false } = {}) {
    if (S.saving) return false;
    if (S.modal) S.modal.confirm();
    S.saving = true;
    updateSaveState();
    try {
      commit();
      const spec = name ? { ...JSON.parse(JSON.stringify(S.spec)), name } : S.spec;
      if (name && spec.origin?.source !== 'flyout') spec.origin = null;
      // A frozen copy: the player can keep working while the file is written.
      const sent = JSON.stringify(spec);
      const startedAs = JSON.stringify(S.spec);
      const body = { spec: JSON.parse(sent), mesh_set: S.meshSet, base_modified: S.planeId && !asNew ? S.fileModified : null, force };
      const r = await withMeshSet(() => (S.planeId && !asNew ? api.put(`/api/planes/${S.planeId}`, body) : api.post('/api/planes', body)));
      // Anything changed since the save started stays on screen as unsaved work, on top of the saved plane.
      const editedMeanwhile = JSON.stringify(S.spec) !== startedAs;
      if (name) {
        if (editedMeanwhile) { S.spec.name = name; if (S.spec.origin?.source !== 'flyout') S.spec.origin = null; }
        else S.spec = spec;
        $('#plane-name').value = S.spec.name;
      }
      S.planeId = r.plane.id;
      // Reopen from disk: fixed meshes are renumbered in the saved file.
      const fresh = await api.get(`/api/planes/${r.plane.id}`);
      S.fileModified = fresh.modified;
      const freshSpec = normalize(fresh.spec);
      S.savedJson = JSON.stringify(freshSpec);
      if (!editedMeanwhile) {
        // Undo history survives a save unless the file came back different (renumbered meshes would break old steps).
        const renumbered = S.savedJson !== JSON.stringify(normalize(JSON.parse(sent)));
        S.spec = freshSpec; S.meshSet = fresh.mesh_set; S.sourceMeshes = fresh.source_meshes;
        S.current = S.savedJson;
        if (renumbered) { S.past = []; S.future = []; }
        vp.setSourceMeshes(S.sourceMeshes);
        vp.setPlane(S.spec);
        S.selected = S.selected.filter(k => k === 'plane' || getItem(S.spec, k));
        vp.setSelection(S.selected, S.active);
        if (S.edit && !getItem(S.spec, S.edit.key)) exitEdit();
        refreshEdit();
      }
      draftSoon.cancel();
      // The work is in the file now: drop its draft. From here on the plane's own id names its draft.
      if (S.draftId) await api.del(draftUrl()).catch(() => {});
      S.draftId = r.plane.id;
      if (editedMeanwhile) draftSoon();
      history.replaceState(null, '', `#/build/${r.plane.id}`);
      renderOutline(); renderInspector_(); updateUndo();
      toast(`Saved "${r.plane.name}" to your planes folder.`, { kind: 'good' });
      return true;
    } catch (e) {
      if (e?.status === 409 && !force) {
        S.saving = false; updateSaveState();
        const replace = await confirmDialog({ title: 'The file changed', body: `<p>${esc(e.message)}</p><p>Replace it with what you have here, or save yours as a new plane?</p>`, confirm: 'Replace the file', cancel: 'Save as a new plane' });
        return replace ? await save({ asNew, name, force: true }) : await saveAs();
      }
      if (e?.status === 404 && S.planeId && !asNew) {
        S.saving = false; updateSaveState();
        const again = await confirmDialog({ title: 'The plane file is gone', body: `<p>${esc(e.message)}</p><p>Save what you have here as a new plane?</p>`, confirm: 'Save as a new plane', cancel: 'Not now' });
        if (again) { S.planeId = null; return await save({ asNew: true }); }
        return false;
      }
      showError(e, "Couldn't save:"); return false;
    }
    finally { S.saving = false; updateSaveState(); }
  }

  async function saveAs() {
    const name = await promptDialog({ title: 'Save as a new plane', label: 'Name', value: `${S.spec.name} copy`, confirm: 'Save' });
    if (!name) return false;
    return await save({ asNew: true, name });
  }

  async function testFly() {
    if ((isDirty() || !S.planeId) && !(await save())) return;
    const a = S.analysis;
    if (a?.flight?.has_problems) {
      const ok = await confirmDialog({ title: 'Fly it with problems?', body: `<p>The flight check found problems:</p><ul class="about-list" style="margin-top:6px">${a.flight.checks.filter(c => c.level === 'problem').map(c => `<li>${esc(c.message)}</li>`).join('')}</ul>`, confirm: 'Start the game anyway', cancel: 'Fix them first' });
      if (!ok) return;
    }
    await launchGame({ planeName: S.spec.name, donor: donorLabel(a?.donor || ''), testFlyPlaneId: S.planeId });
  }

  // ------------------------------------------------------------ keyboard

  $('#plane-name').value = S.spec.name;
  $('#plane-name').addEventListener('input', e => { S.spec.name = e.target.value; changed(false, { inspector: false }); });
  $('#plane-name').addEventListener('change', () => { commit(); renderOutline(); if (!S.selected.length) renderInspectorSoon(); });
  $('[data-a=back]').onclick = () => go('#/');
  $('[data-a=save]').onclick = () => save();
  $('[data-a=saveas]').onclick = saveAs;
  $('[data-a=fly]').onclick = testFly;
  root.querySelectorAll('[data-a=markers]').forEach(b => { b.onclick = () => run('markers'); });

  // Outliner / properties split.
  $('#side-split').addEventListener('pointerdown', e => {
    const side = $('#side'), r = side.getBoundingClientRect();
    const el = e.currentTarget;
    el.setPointerCapture(e.pointerId);
    const mv = ev => side.style.setProperty('--outliner-h', `${Math.max(15, Math.min(80, (ev.clientY - r.top) / r.height * 100))}%`);
    const up = () => { el.removeEventListener('pointermove', mv); el.removeEventListener('pointerup', up); };
    el.addEventListener('pointermove', mv); el.addEventListener('pointerup', up);
  });

  const onKey = e => {
    if (dialogOpen() || menuOpen()) return;
    const mod = e.ctrlKey || e.metaKey;
    const ae = document.activeElement;
    // Typing into a text field (not a checkbox, slider or button) keeps the keys for itself.
    const TEXT_INPUTS = /^(text|search|email|url|tel|password|number|date|time|datetime-local|month|week)$/i;
    const typing = (ae?.tagName === 'INPUT' && TEXT_INPUTS.test(ae.type || 'text')) || /^(TEXTAREA|SELECT)$/.test(ae?.tagName || '') || !!ae?.isContentEditable;
    const k = e.key.toLowerCase();
    if (mod && k === 's') { e.preventDefault(); save(); return; }
    if (S.modal) { if (S.modal.key(e)) e.preventDefault(); return; }
    if (typing) return;
    const code = e.code;
    if (S.pending && e.key === 'Escape') { e.preventDefault(); cancelPending(); $('#xf-readout').hidden = true; return; }
    const go1 = (id, ...a) => { e.preventDefault(); run(id, ...a); };
    // Number pad views (and the same with Ctrl for the opposite side).
    if (code === 'Numpad1') return go1(mod ? 'viewBack' : 'viewFront');
    if (code === 'Numpad3') return go1(mod ? 'viewLeft' : 'viewRight');
    if (code === 'Numpad7') return go1(mod ? 'viewBottom' : 'viewTop');
    if (code === 'Numpad5') return go1('ortho');
    if (code === 'Numpad0') return go1('viewCockpit');
    if (code === 'NumpadDecimal') return go1('frameSel');
    if (code === 'Numpad8' || code === 'Numpad2' || code === 'Numpad4' || code === 'Numpad6') {
      e.preventDefault();
      const r = vp.activeRig;
      if (code === 'Numpad8') r.orbit(0, -37.5); else if (code === 'Numpad2') r.orbit(0, 37.5); else if (code === 'Numpad4') r.orbit(37.5, 0); else r.orbit(-37.5, 0);
      vp.requestRender(); updateViewName(); markHeader();
      return;
    }
    if (mod && e.altKey && k === 'q') return go1('quad');
    if (mod && !e.shiftKey && k === 'z') return go1('undo');
    if (mod && (k === 'y' || (k === 'z' && e.shiftKey))) return go1('redo');
    if (mod && k === 'r') return go1('loopcut');
    if (mod && k === 'i') return go1('selectInvert');
    if (mod) return;
    if (e.altKey && k === 'a') return go1('selectNone');
    if (e.altKey && k === 'h') return go1('unhide');
    if (e.altKey && k === 'z') return go1('xray');
    if (e.altKey) return;
    if (e.key === 'Tab' && e.shiftKey) return go1('snap');
    if (e.key === 'Tab') return go1('toggleEdit');
    if (e.key === '?' || e.key === 'F1') return go1('help');
    if (e.key === 'F2') return go1('rename');
    if (e.key === 'Home') return go1('frameAll');
    if (e.key === 'Delete' || e.key === 'Backspace') return go1('delete');
    if (e.key === 'Escape') {
      if (!$('#checks').hidden) toggleChecks();
      else if (S.boxMode) S.boxMode = false;
      return;
    }
    const m = lastMouse();
    if (e.shiftKey) {
      if (k === 'a') return go1('add', m.x, m.y);
      if (k === 'd') return go1('duplicate');
      if (k === 'h') return go1('hideOthers');
      if (k === 'z') return go1('wire');
      if (k === 'm') return go1('measure');
      if (k === 's') return go1('smoothBody');
      if (k === 'n') return go1('recalc');
      return;
    }
    switch (k) {
      case 'g': return go1('grab');
      case 'r': return go1('rotate');
      case 's': return go1('scale');
      case 'w': return go1('tool-select');
      case 'a': return go1('selectAll');
      case 'b': return go1('boxSelect');
      case 'x': return go1('delete', m.x, m.y);
      case 'h': return go1('hide');
      case 'f': return go1('frameSel');
      case 'e': return go1('extrude');
      case 'i': return go1('inset');
      case 'm': return go1('merge', m.x, m.y);
      case 'o': return go1('prop');
      case '1': if (inEdit()) return go1('modeVert'); return;
      case '2': if (inEdit()) return go1('modeEdge'); return;
      case '3': if (inEdit()) return go1('modeFace'); return;
    }
  };
  const onKeyUp = e => { if (S.modal) S.modal.keyUp(e); };
  window.addEventListener('keydown', onKey);
  window.addEventListener('keyup', onKeyUp);

  // ------------------------------------------------------------ start

  if (doc.note) toast(doc.note, { timeout: 9000 });
  renderHeader();
  renderShelf();
  renderOutline();
  renderInspector_();
  updateUndo();
  updateSaveState();
  updateStatus();
  updateViewName();
  S.hadParts = hasParts();
  $('#vp-empty').hidden = S.hadParts;
  analyzeSoon.flush();
  if (doc.fromDraft && !S.planeId) draftSoon.cancel();
  firstRunTips(vpWrap, () => run('help'));
  // For automated checks (read-only state and the command runner), and for the app around the builder: the header's
  // Play button saves through it, and closing the window flushes the draft.
  window.openplaneBuilder = {
    get state() { return S; }, run, select, vp,
    acceptDrop(files) {
      const ok = files.every(f => /.(obj|png|jpe?g|webp)$/i.test(f.name));
      if (!ok) return false;
      for (const f of files) { if (/.obj$/i.test(f.name)) importObj(f); else addReference(null, f); }
      return true;
    },
    hasUnsavedChanges: () => !S.destroyed && (isDirty() || !S.planeId),
    planeName: () => S.spec.name,
    donor: () => donorLabel(S.analysis?.donor || ''),
    save: () => save(),
    async flushDraft() { draftSoon.cancel(); await saveDraftNow(); },
  };

  return {
    title: S.spec.name,
    async canLeave() {
      if (S.modal) S.modal.cancel();
      if (!isDirty() || S.destroyed) return true;
      const choice = await modal(close => {
        const el = html(`<div style="display:contents"><h2>Save "${esc(S.spec.name)}" first?</h2>
            <p class="muted">${S.planeId ? 'You have unsaved changes.' : "This plane hasn't been saved yet."} If you leave without saving, OpenPlane keeps it as an unsaved draft you can continue later.</p>
            <div class="btn-row"><button type="button" class="btn" data-c="cancel">Stay</button><button type="button" class="btn" data-c="leave">Leave without saving</button><button type="button" class="btn primary" data-c="save">Save and leave</button></div></div>`);
        el.querySelectorAll('[data-c]').forEach(b => b.onclick = () => close(b.dataset.c));
        return el;
      }, { label: 'Unsaved changes' });
      if (choice === 'save') return await save();
      if (choice === 'leave') { await saveDraftNow(); return true; }
      return false;
    },
    destroy() {
      S.destroyed = true;
      analyzeSoon.cancel();
      closeMenu();
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('pagehide', onPageHide);
      window.removeEventListener('beforeunload', onPageHide);
      vp.dispose();
      if (window.openplaneBuilder?.state === S) delete window.openplaneBuilder;
      main.style.overflow = '';
    },
  };
}

const PLURAL_OF = mode => countOf(mode, 2).slice(2);

function segDist(px, py, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
  const t = L ? Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / L)) : 0;
  return Math.hypot(ax + dx * t - px, ay + dy * t - py);
}
