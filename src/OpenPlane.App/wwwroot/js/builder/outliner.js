// The outliner: every part of the plane in a tree, with eye (hide) and lock toggles and rename (double-click or F2).
// Click selects, Shift+click adds, Ctrl+click toggles. One delegated listener, so planes with hundreds of pieces stay fast.
import { esc } from '../ui.js';
import { icon } from '../icons.js';
import { keyOf, KIND_ICON, shapeMaterial } from './parts.js';
import { hasTwin } from './geometry.js';

/**
 * ctx: { spec, selected: Set, active, hidden: Set, locked: Set, editKey, select(key, mods), toggleHidden(key),
 *        toggleLocked(key), rename(key, name), add(section, x, y), frame(key) }
 */
export function renderOutliner(host, ctx) {
  const s = ctx.spec;
  const scroll = host.scrollTop;
  const row = (key, name, ic, tag = '', child = false) => {
    const sel = ctx.selected.has(key), act = ctx.active === key, hid = ctx.hidden.has(key), lock = ctx.locked.has(key);
    const plane = key === 'plane';
    return `<div class="ol-row ${child ? 'child' : ''} ${sel ? 'sel' : ''} ${act ? 'active' : ''} ${hid ? 'hidden-part' : ''}" data-key="${esc(key)}" role="treeitem" aria-selected="${sel}" tabindex="-1" title="${esc(name)}">
        ${icon(ic)}<span class="nm">${esc(name)}${ctx.editKey === key ? ' <span class="tag">editing</span>' : ''}</span>${tag ? `<span class="tag">${esc(tag)}</span>` : ''}
        ${plane ? '' : `<button type="button" class="tg ${hid ? 'off' : ''}" data-t="eye" title="${hid ? 'Show' : 'Hide'} (H, Alt+H shows all)" aria-label="${hid ? 'Show' : 'Hide'} ${esc(name)}" aria-pressed="${!hid}">${icon(hid ? 'eyeOff' : 'eye')}</button>
        <button type="button" class="tg ${lock ? 'on-lock' : 'off'}" data-t="lock" title="${lock ? 'Unlock' : 'Lock'}: a locked part can't be selected in the view" aria-label="${lock ? 'Unlock' : 'Lock'} ${esc(name)}" aria-pressed="${lock}">${icon(lock ? 'lock' : 'unlock')}</button>`}
      </div>`;
  };
  const twinTag = it => (it.mirror ? '×2' : '');
  const pointTag = it => (hasTwin(it.position, it.mirror) ? '×2' : '');
  const sec = (title, addKey, body) => `<div class="ol-sec"><span>${title}</span><span class="grow"></span>${addKey ? `<button type="button" class="icon-btn" data-add="${addKey}" title="Add ${title.toLowerCase()} (Shift+A)" aria-label="Add to ${esc(title)}">${icon('plus', 'sm')}</button>` : ''}</div>${body || '<div class="ol-empty">None yet</div>'}`;

  const lofts = (s.fuselages || []).filter(f => f.kind !== 'mesh');
  const fixed = (s.fuselages || []).filter(f => f.kind === 'mesh');
  const tags = (...t) => t.filter(Boolean).join(' ');
  const inside = it => (it.interior ? 'inside' : '');
  let bodies = lofts.map(f => row(keyOf('fuselage', f.id), f.name || 'Fuselage', 'body', tags(inside(f), twinTag(f)))).join('');
  bodies += (s.shapes || []).map(sh => row(keyOf('shape', sh.id), sh.name || 'Shape', 'cube', tags(inside(sh), shapeMaterial(s, sh).kind === 'glass' ? 'glass' : '', twinTag(sh)))).join('');
  const fixedRows = fixed.map(f => row(keyOf('fuselage', f.id), f.name || 'Body', 'body', tags('fixed', inside(f), f.mirror ? '×2' : ''), fixed.length > 8)).join('');
  if (fixed.length > 8) {
    const openG = fixed.some(f => ctx.selected.has(keyOf('fuselage', f.id))) || ctx.fixedOpen;
    bodies += `<details class="ol-group" ${openG ? 'open' : ''}><summary class="ol-row" data-group="fixed"><span class="caret"></span>${icon('body')}<span class="nm">${fixed.length} fixed body pieces</span><span class="tag">fixed</span></summary>${fixedRows}</details>`;
  } else bodies += fixedRows;

  const wings = (s.wings || []).map(w => row(keyOf('wing', w.id), w.name || 'Wing', 'wing', `${w.kind === 'panels' ? 'fixed ' : ''}${w.vertical ? 'fin ' : ''}${twinTag(w)}`.trim())).join('');
  const engines = (s.engines || []).map(e => row(keyOf('engine', e.id), e.name || 'Engine', 'engine', `${e.variant || e.type} ${pointTag(e)}`.trim())).join('');
  const gear = (s.gear || []).map(g => row(keyOf('gear', g.id), g.name || 'Gear', 'gear', pointTag(g))).join('');
  const weapons = [...(s.hardpoints || []).map(h => row(keyOf('hardpoint', h.id), h.name || 'Hardpoint', 'weapon', pointTag(h))),
    ...(s.guns || []).map(g => row(keyOf('gun', g.id), g.name || 'Gun', 'weapon', `${+g.caliber_mm || 0} mm ${pointTag(g)}`.trim()))].join('');
  const cockpit = s.cockpit ? row('cockpit:cockpit', 'Cockpit', 'cockpit', s.cockpit.own_view === false ? 'stock view' : '') : '';
  const INSTRUMENT_TAG = { gauge: 'gauge', attitude: 'horizon', compass: 'compass', screen: 'screen', hud: 'HUD', mirror: 'mirror', lever: 'lever' };
  const instruments = (s.instruments || []).map(n => row(keyOf('instrument', n.id), n.name || 'Instrument', KIND_ICON.instrument, tags(INSTRUMENT_TAG[n.kind] || 'gauge', pointTag(n)))).join('');
  const LIGHT_TAG = { nav: 'nav', strobe: 'strobe', beacon: 'beacon', landing: 'landing', strip: 'strip' };
  const lights = (s.lights || []).map(l => row(keyOf('light', l.id), l.name || 'Light', KIND_ICON.light, tags(LIGHT_TAG[l.kind] || 'nav', pointTag(l)))).join('');
  const refs = (s.references || []).map(r => {
    const key = keyOf('reference', r.id);
    return row(key, r.name || 'Reference', 'image', r.view || 'top');
  }).join('');

  host.innerHTML = `<div role="tree" aria-label="Parts of the plane">${row('plane', s.name || 'Plane', KIND_ICON.plane, 'settings')}
    ${sec('Bodies', 'bodies', bodies)}${sec('Wings and tails', 'wings', wings)}${sec('Engines', 'engines', engines)}
    ${sec('Landing gear', 'gear', gear)}${sec('Weapons', 'weapons', weapons)}${sec('Cockpit', s.cockpit ? '' : 'cockpit', cockpit)}
    ${sec('Instruments', 'instruments', instruments)}${sec('Lights', 'lights', lights)}
    ${sec('Reference images', 'references', refs)}</div>`;
  host.scrollTop = scroll;

  // Reference images use their own "visible" and "locked" flags; show them on the toggles.
  for (const r of s.references || []) {
    const el = host.querySelector(`[data-key="${CSS.escape(keyOf('reference', r.id))}"]`);
    if (!el) continue;
    el.classList.toggle('hidden-part', r.visible === false);
    const eye = el.querySelector('[data-t=eye]'); eye.classList.toggle('off', r.visible === false); eye.innerHTML = icon(r.visible === false ? 'eyeOff' : 'eye');
    const lk = el.querySelector('[data-t=lock]'); lk.classList.toggle('on-lock', !!r.locked); lk.classList.toggle('off', !r.locked); lk.innerHTML = icon(r.locked ? 'lock' : 'unlock');
  }

  if (!host.dataset.wired) {
    host.dataset.wired = '1';
    host.addEventListener('click', e => {
      const c = host._ctx;
      const add = e.target.closest('[data-add]');
      if (add) { const r = add.getBoundingClientRect(); c.add(add.dataset.add, r.left, r.bottom + 2); return; }
      const tg = e.target.closest('[data-t]');
      const rowEl = e.target.closest('[data-key]');
      if (tg && rowEl) { e.stopPropagation(); if (tg.dataset.t === 'eye') c.toggleHidden(rowEl.dataset.key); else c.toggleLocked(rowEl.dataset.key); return; }
      if (e.target.closest('summary[data-group]')) { c.setFixedOpen?.(!e.target.closest('details').open); return; }
      if (rowEl && !e.target.closest('input')) c.select(rowEl.dataset.key, { add: e.shiftKey, toggle: e.ctrlKey || e.metaKey });
    });
    host.addEventListener('dblclick', e => {
      const rowEl = e.target.closest('[data-key]');
      if (!rowEl || e.target.closest('[data-t]')) return;
      startRename(host, rowEl.dataset.key);
    });
  }
  host._ctx = ctx;
}

/** Turns a row's name into a text box (F2 or double-click). Enter saves, Esc cancels. */
export function startRename(host, key) {
  const c = host._ctx;
  const rowEl = host.querySelector(`[data-key="${CSS.escape(key)}"]`);
  if (!rowEl || key === 'cockpit:cockpit') return;
  rowEl.scrollIntoView({ block: 'nearest' });
  const nm = rowEl.querySelector('.nm');
  const old = rowEl.getAttribute('title') || '';
  nm.innerHTML = `<input type="text" aria-label="New name" value="${esc(old)}">`;
  const input = nm.querySelector('input');
  input.focus(); input.select();
  let done = false;
  const finish = save => { if (done) return; done = true; const v = input.value.trim(); if (save && v && v !== old) c.rename(key, v); else c.rerender(); };
  input.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') finish(true); else if (e.key === 'Escape') finish(false); });
  input.addEventListener('blur', () => finish(true));
}
