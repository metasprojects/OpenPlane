// Shared UI helpers: element builder, toasts, dialogs, formatting.
import { icon, hydrateIcons } from './icons.js';
import { api, ApiError } from './api.js';

export const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** Builds DOM from an HTML string (trusted templates only; put user text through esc()). */
export function html(str) {
  const t = document.createElement('template');
  t.innerHTML = str.trim();
  hydrateIcons(t.content);
  return t.content.childElementCount === 1 ? t.content.firstElementChild : t.content;
}

export function $(sel, root = document) { return root.querySelector(sel); }
export function $$(sel, root = document) { return [...root.querySelectorAll(sel)]; }

export function debounce(fn, ms) {
  let t = null;
  const d = (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
  d.flush = (...a) => { clearTimeout(t); fn(...a); };
  d.cancel = () => clearTimeout(t);
  return d;
}

// ---------------------------------------------------------------- numbers
export function fmt(v, digits = 1) {
  if (v === null || v === undefined || !isFinite(v)) return '–';
  return Number(v).toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
export const fmtInt = v => fmt(v, 0);
export function fmtKg(v) { return !isFinite(v) ? '–' : v >= 10000 ? `${fmt(v / 1000, 1)} t` : `${fmtInt(v)} kg`; }
export function timeAgo(iso) {
  const t = new Date(iso).getTime();
  if (!t) return '';
  const s = (Date.now() - t) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.round(s / 86400)} days ago`;
  return new Date(iso).toLocaleDateString();
}

// ---------------------------------------------------------------- toasts
export function toast(message, { kind = 'info', action = null, timeout = 5000 } = {}) {
  const host = document.getElementById('toasts');
  const el = html(`<div class="toast ${kind}" role="${kind === 'bad' ? 'alert' : 'status'}">
      ${kind === 'good' ? icon('checkCircle') : kind === 'bad' ? icon('warn') : icon('info')}
      <span class="msg">${esc(message)}</span>
      ${action ? `<button type="button" class="btn small">${esc(action.label)}</button>` : ''}
      <button type="button" class="close" aria-label="Dismiss">${icon('x', 'sm')}</button>
    </div>`);
  const close = () => el.remove();
  el.querySelector('.close').onclick = close;
  if (action) el.querySelector('.btn').onclick = () => { close(); action.run(); };
  host.append(el);
  if (timeout) setTimeout(close, action ? timeout * 1.6 : timeout);
  return close;
}

export function showError(e, prefix = '') {
  const msg = e instanceof ApiError || e instanceof Error ? e.message : String(e);
  toast(prefix ? `${prefix} ${msg}` : msg, { kind: 'bad', timeout: 9000 });
}

// ---------------------------------------------------------------- dialogs (in-page, keyboard friendly)
let openDialogs = 0;
export const dialogOpen = () => openDialogs > 0;

/**
 * Opens a modal. `build(close)` returns the dialog's content element. Resolves with the value given to close().
 * Escape and clicking the backdrop close it with `undefined`. Focus moves in and returns afterwards.
 */
export function modal(build, { wide = false, label = 'Dialog' } = {}) {
  return new Promise(resolve => {
    const previous = document.activeElement;
    const backdrop = html(`<div class="dialog-backdrop"><div class="dialog ${wide ? 'wide' : ''}" role="dialog" aria-modal="true" aria-label="${esc(label)}"></div></div>`);
    const box = backdrop.firstElementChild;
    let done = false;
    const close = value => {
      if (done) return;
      done = true;
      openDialogs--;
      backdrop.remove();
      document.removeEventListener('keydown', onKey, true);
      if (previous && previous.focus) previous.focus();
      resolve(value);
    };
    const onKey = e => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(undefined); }
      else if (e.key === 'Tab') {
        const f = [...box.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')].filter(x => !x.disabled && x.offsetParent !== null);
        if (!f.length) return;
        const first = f[0], last = f[f.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    };
    box.append(build(close));
    hydrateIcons(box);
    backdrop.addEventListener('mousedown', e => { if (e.target === backdrop) close(undefined); });
    document.addEventListener('keydown', onKey, true);
    document.body.append(backdrop);
    openDialogs++;
    const auto = box.querySelector('[autofocus]') || box.querySelector('input, select, textarea, .btn.primary, button');
    auto?.focus();
  });
}

/** A yes/no question. Resolves true for the confirm button. */
export function confirmDialog({ title, body, confirm = 'OK', cancel = 'Cancel', danger = false }) {
  return modal(close => {
    const el = html(`<div><h2>${esc(title)}</h2></div>`);
    const frag = document.createDocumentFragment();
    frag.append(el);
    const p = document.createElement('div');
    p.className = 'muted';
    if (body instanceof Node) p.append(body); else p.innerHTML = body ?? '';
    frag.append(p);
    const row = html(`<div class="btn-row">
        <button type="button" class="btn" data-a="no">${esc(cancel)}</button>
        <button type="button" class="btn ${danger ? 'danger solid' : 'primary'}" data-a="yes" autofocus>${esc(confirm)}</button>
      </div>`);
    row.querySelector('[data-a=no]').onclick = () => close(false);
    row.querySelector('[data-a=yes]').onclick = () => close(true);
    frag.append(row);
    const wrap = document.createElement('div');
    wrap.style.display = 'contents';
    wrap.append(frag);
    return wrap;
  }, { label: title }).then(v => v === true);
}

/** Asks for a line of text. Resolves with the text, or null when cancelled. */
export function promptDialog({ title, body = '', label, value = '', confirm = 'OK', placeholder = '' }) {
  return modal(close => {
    const el = html(`<form style="display:contents">
        <h2>${esc(title)}</h2>
        ${body ? `<p class="muted">${body}</p>` : ''}
        <label class="field"><span class="label">${esc(label)}</span><input type="text" name="v" value="${esc(value)}" placeholder="${esc(placeholder)}" autofocus></label>
        <div class="btn-row"><button type="button" class="btn" data-a="no">Cancel</button><button type="submit" class="btn primary">${esc(confirm)}</button></div>
      </form>`);
    el.querySelector('[data-a=no]').onclick = () => close(null);
    el.onsubmit = e => { e.preventDefault(); close(el.querySelector('input').value.trim()); };
    setTimeout(() => el.querySelector('input').select(), 0);
    return el;
  }, { label: title }).then(v => (v === undefined ? null : v));
}

/** Asks for a folder: the native dialog in the OpenPlane window, a text box in a browser. */
export async function pickFolder(title, current = '') {
  try {
    const r = await api.post('/api/dialog/folder', { title, default_path: current || null });
    return r.path || null;
  } catch (e) {
    if (e.status !== 409) { showError(e); return null; }
    const v = await promptDialog({ title, label: 'Folder path', value: current, confirm: 'Use this folder', body: 'Paste or type the full path of the folder.' });
    return v || null;
  }
}

export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; }
  catch {
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.append(ta); ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { }
    ta.remove();
    return ok;
  }
}

export function openLink(url) {
  api.post('/api/open-link', { path: url }).catch(e => showError(e));
}

/** Friendly names for the stock planes OpenPlane builds on. */
export const DONORS = {
  COIN: 'Light propeller plane (COIN, the CI-22)',
  Fighter1: 'Single-engine fighter (Fighter1)',
  Multirole1: 'Twin-engine multirole jet (Multirole1)',
  CAS1: 'Ground-attack jet (CAS1)',
  FastBomber1: 'Fast bomber (FastBomber1)',
  QuadVTOL1: 'Four-fan VTOL (QuadVTOL1)',
  VTOLTrainer1: 'VTOL trainer (VTOLTrainer1)',
};
export const donorLabel = key => DONORS[key] || key;

export function originText(origin, author) {
  if (!origin) return author ? `By ${author}` : '';
  if (origin.source === 'flyout') {
    const who = origin.builder ? `built by ${origin.builder}` : 'builder unknown';
    return `Converted from Flyout (${origin.craft_name || 'craft'}), ${who}`;
  }
  return origin.builder || author ? `Built in OpenPlane by ${origin.builder || author}` : 'Built in OpenPlane';
}
