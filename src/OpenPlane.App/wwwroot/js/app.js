// OpenPlane page: routing between the five screens, theme, global import (drag and drop), the Play button.
import { api, getStatus } from './api.js';
import { hydrateIcons, icon } from './icons.js';
import { toast, showError, esc, html, confirmDialog, dialogOpen, modal } from './ui.js';

const screens = {
  home: () => import('./screens/home.js'),
  convert: () => import('./screens/convert.js'),
  build: () => import('./screens/build.js'),
  share: () => import('./screens/share.js'),
  settings: () => import('./screens/settings.js'),
};

let current = null;       // { name, module, instance }
let navigating = false;
let pending = false;      // the address changed while a screen was still loading
let lastHash = location.hash;

function parseHash() {
  // The query ("#/convert?select=…") is not part of the screen's path.
  const parts = (location.hash.replace(/^#\/?/, '').split('?')[0] || '').split('/').filter(Boolean).map(decodeURIComponent);
  const name = screens[parts[0]] ? parts[0] : 'home';
  return { name, args: parts.slice(1), query: new URLSearchParams(location.hash.split('?')[1] || '') };
}

export function go(hash) {
  if (location.hash === hash) route();
  else location.hash = hash;
}

async function route() {
  if (navigating) { pending = true; return; }
  // In-page anchors ("#main" from the skip link) are not screens: put the screen address back and stay.
  if (current && location.hash.length > 1 && !location.hash.startsWith('#/')) { history.replaceState(null, '', lastHash?.startsWith('#/') ? lastHash : '#/'); return; }
  const target = parseHash();
  if (current?.instance?.canLeave && !(await current.instance.canLeave())) {
    // Stay: put the old address back without routing again.
    navigating = true;
    history.replaceState(null, '', lastHash || '#/');
    navigating = false;
    return;
  }
  lastHash = location.hash;
  navigating = true;
  try {
    current?.instance?.destroy?.();
    const main = document.getElementById('main');
    main.innerHTML = '<div class="loading-page"><span class="spinner"></span></div>';
    document.querySelectorAll('[data-nav]').forEach(a => {
      if (a.dataset.nav === target.name) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });
    const module = await screens[target.name]();
    main.innerHTML = '';
    main.scrollTop = 0;
    const instance = await module.render(main, target.args, target.query) || {};
    current = { name: target.name, module, instance };
    document.title = instance.title ? `${instance.title} · OpenPlane` : 'OpenPlane';
    hydrateIcons(main);
  } catch (e) {
    showError(e);
  } finally {
    navigating = false;
    // A click that arrived while this screen was loading wins: show where the address bar now points.
    if (pending) { pending = false; if (location.hash !== lastHash) route(); }
  }
}

// ---------------------------------------------------------------- theme
export function applyTheme(theme) {
  // Dark is the default look (the same as Open Skies); "light" is Bone; "system" follows the OS.
  document.documentElement.dataset.theme = theme === 'light' || theme === 'system' ? theme : 'dark';
  window.dispatchEvent(new Event('openplane-theme'));
}
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => window.dispatchEvent(new Event('openplane-theme')));

// ---------------------------------------------------------------- header status and Play
export async function refreshHeader(force = false) {
  try {
    const st = await getStatus(force);
    const badge = document.getElementById('mod-badge');
    const by = document.getElementById('byline');
    if (by && st.version) by.textContent = `planes for Nuclear Option · v${st.version}`;
    const m = st.mod;
    let dot = 'good', text = 'Mod installed';
    if (m.state === 'no-game') { dot = 'warn'; text = 'Game not found'; }
    else if (m.state === 'needs-install' && m.bepinex_incomplete) { dot = 'warn'; text = 'BepInEx incomplete'; }
    else if (m.state === 'needs-install' && m.mod_installed && !m.bepinex_installed) { dot = 'warn'; text = 'BepInEx missing'; }
    else if (m.state === 'blueprinter-old') { dot = 'warn'; text = 'Blueprinter too old'; }
    else if (m.state === 'needs-install' || m.state === 'no-bundle') { dot = 'warn'; text = 'Mod not installed'; }
    else if (m.state === 'needs-update') { dot = 'warn'; text = 'Mod update ready'; }
    badge.innerHTML = `<span class="dot ${dot}"></span>${esc(text)}`;
    badge.title = m.summary;
    badge.hidden = false;
    return st;
  } catch { return null; }
}

export async function launchGame({ planeName = null, donor = null, testFlyPlaneId = null } = {}) {
  const st = await refreshHeader(true);
  if (st?.mod?.game_running) {
    // The mod reads planes once, when the game starts.
    toast('Nuclear Option is already running. It loads planes when it starts, so quit the game and start it again to fly your latest changes.', { kind: 'info', timeout: 12000 });
    return false;
  }
  if (st && st.mod.state !== 'ready' && st.mod.state !== 'needs-update') {
    const go2 = await confirmDialog({
      title: 'Start Nuclear Option anyway?',
      body: `<p>${esc(st.mod.summary)}</p><p style="margin-top:8px">Without the OpenPlane mod the game won't load your planes. You can install it in Settings.</p>`,
      confirm: 'Start the game', cancel: 'Not now',
    });
    if (!go2) return false;
  }
  try {
    const r = await api.post('/api/game/launch', testFlyPlaneId ? { test_fly: { plane_id: testFlyPlaneId } } : {});
    if (r.test_fly) {
      const air = (st?.settings?.test_fly_start || 'air') !== 'hangar';
      toast(r.how === 'free-flight'
        ? `Starting a test flight: Nuclear Option opens straight into Free Flight on Heartland with "${planeName}" ${air ? 'in the air over an airbase' : 'in a hangar'}. It takes a minute to load.`
        : `Starting Nuclear Option… Steam's program wasn't found, so the game opens at its menu: start any single-player mission in the next 15 minutes and you'll get "${planeName}" there.`,
        { kind: 'good', timeout: 12000 });
      return true;
    }
    const where = donor ? ` Your plane${planeName ? ` "${planeName}"` : ''} is in the hangars that offer the stock plane it's built on: ${donor}.` : '';
    toast(`Starting Nuclear Option through Steam…${where}${r.note ? ` (${r.note})` : ''}`, { kind: 'good', timeout: 9000 });
    return true;
  } catch (e) { showError(e); return false; }
}

/** The header's Play button: with a plane open in the builder, offer to save it first (the game loads saved files only). */
async function playFromHeader() {
  const b = window.openplaneBuilder;
  if (b?.hasUnsavedChanges?.()) {
    const choice = await modal(close => {
      const el = html(`<div style="display:contents"><h2>Save "${esc(b.planeName())}" before playing?</h2>
          <p class="muted">Nuclear Option loads your planes from the planes folder when it starts, so unsaved changes won't be in the game.</p>
          <div class="btn-row"><button type="button" class="btn" data-c="cancel">Cancel</button><button type="button" class="btn" data-c="play">Play without saving</button><button type="button" class="btn primary" data-c="save">Save and play</button></div></div>`);
      el.querySelectorAll('[data-c]').forEach(x => x.onclick = () => close(x.dataset.c));
      return el;
    }, { label: 'Save before playing' });
    if (choice === 'save') { if (!(await b.save())) return; }
    else if (choice !== 'play') return;
    return launchGame({ planeName: b.planeName(), donor: b.donor() });
  }
  return launchGame(b ? { planeName: b.planeName(), donor: b.donor() } : {});
}

document.getElementById('play-btn').addEventListener('click', () => playFromHeader());

// ---------------------------------------------------------------- closing the window (the OpenPlane app's own window)
// The window asks the page before it closes, so the builder's unsaved work is in its draft first. (In a browser tab,
// the builder sends its draft on pagehide instead.)
if (typeof window.external?.receiveMessage === 'function') {
  try {
    window.external.receiveMessage(async message => {
      if (message !== 'openplane:closing') return;
      try { await window.openplaneBuilder?.flushDraft?.(); } catch { /* close anyway */ }
      window.external.sendMessage('openplane:ready-to-close');
    });
  } catch { /* not the OpenPlane window */ }
}

// ---------------------------------------------------------------- import (file picker and drag and drop anywhere)
export async function importFiles(files) {
  const results = [];
  for (const f of files) {
    const name = f.name || 'file';
    if (!/\.(oplane|zip)$/i.test(name)) {
      toast(`"${name}" can't be imported. OpenPlane imports .oplane planes and .zip files of Flyout craft folders.`, { kind: 'bad', timeout: 9000 });
      continue;
    }
    try {
      const r = await api.upload(`/api/import?name=${encodeURIComponent(name)}`, f);
      results.push(r);
      reportImport(r, name);
    } catch (e) { showError(e, `Couldn't import "${name}":`); }
  }
  if (results.length) window.dispatchEvent(new CustomEvent('openplane-imported', { detail: results }));
  return results;
}

/** Tells the player what one import did: a plane, a plane pack or Flyout craft. */
function reportImport(r, name) {
  if (r.kind === 'plane') {
    if (r.outcome === 'same') { toast(r.plane.warning || `You already have "${r.plane.name}".`, { kind: 'info', timeout: 9000 }); return; }
    toast(`Imported "${r.plane.name}" into your planes.`, { kind: 'good', action: { label: 'Open in builder', run: () => go(`#/build/${r.plane.id}`) } });
    if (r.plane.warning) toast(r.plane.warning, { kind: 'info', timeout: 15000 });
  } else if (r.kind === 'pack') {
    const list = r.results || [];
    const added = list.filter(x => x.outcome === 'added').length;
    const replaced = list.filter(x => x.outcome === 'replaced');
    const same = list.filter(x => x.outcome === 'same').length;
    const failed = list.filter(x => x.outcome === 'error');
    const newer = replaced.filter(x => x.replaced_newer).length;
    const parts = [];
    if (added) parts.push(`${added} new plane${added === 1 ? '' : 's'} added`);
    if (replaced.length) parts.push(`${replaced.length} switched to the pack's version (${newer ? `${newer === replaced.length ? (newer === 1 ? 'yours was newer' : 'yours were newer') : `${newer} of yours were newer`}; ` : ''}the old one${replaced.length === 1 ? ' is' : 's are'} in Deleted planes)`);
    if (same) parts.push(`${same} you already had`);
    if (!added && !replaced.length) {
      if (same) toast(`Plane pack "${name}": you already have ${same === 1 ? 'the plane' : `all ${same} planes`} in it.`, { kind: 'info', timeout: 9000 });
    } else toast(`Plane pack "${name}": ${parts.join(', ')}. Restart Nuclear Option to fly them.`, { kind: 'good', timeout: 12000, action: { label: 'My planes', run: () => go('#/') } });
    if (failed.length) toast(`${failed.length} plane${failed.length === 1 ? '' : 's'} in "${name}" couldn't be imported: ${failed.map(x => x.error).join(' ')}`, { kind: 'bad', timeout: 20000 });
    list.filter(x => x.outcome === 'added' && x.plane?.warning).forEach(x => toast(x.plane.warning, { kind: 'info', timeout: 15000 }));
  } else if (r.kind === 'craft') {
    const names = r.craft.map(c => c.name).join(', ');
    toast(`Added the Flyout craft ${names}. Convert it to use it in Nuclear Option.`, { kind: 'good', action: { label: 'Convert now', run: () => go(`#/convert?select=${r.craft.map(c => c.id).join(',')}`) } });
  }
}

/** Files opened with OpenPlane from Windows (double-click, "Open with"): imported by the app; say what happened. */
async function collectOpened() {
  try {
    const { opened } = await api.get('/api/opened');
    if (!opened?.length) return;
    for (const o of opened) {
      if (!o.ok) toast(o.message, { kind: 'bad', timeout: 12000 });
      else reportImport(o.kind === 'pack' ? { ...o, results: o.results } : o, o.file);
    }
    window.dispatchEvent(new CustomEvent('openplane-imported', { detail: opened }));
  } catch { /* the server is gone: the window is closing */ }
}

export function chooseFilesToImport() {
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = '.oplane,.zip';
  input.multiple = true;
  input.onchange = () => { if (input.files.length) importFiles([...input.files]); };
  input.click();
}

(() => {
  const overlay = document.getElementById('drop-overlay');
  let depth = 0;
  const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
  const editable = t => !!t && (t.isContentEditable || /^(INPUT|TEXTAREA)$/.test(t.tagName || ''));
  // Anything else dropped on the window (a link, some text) must never make the window open it instead of OpenPlane:
  // every drag and drop that isn't text going into a text field is handled here.
  window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) { if (!editable(e.target)) e.preventDefault(); return; }
    e.preventDefault(); depth++; overlay.hidden = false;
  });
  window.addEventListener('dragover', e => {
    if (!hasFiles(e)) { if (!editable(e.target)) { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'none'; } return; }
    e.preventDefault(); e.dataTransfer.dropEffect = 'copy';
  });
  window.addEventListener('dragleave', e => { if (!hasFiles(e)) return; depth = Math.max(0, depth - 1); if (!depth) overlay.hidden = true; });
  window.addEventListener('drop', e => {
    if (!hasFiles(e)) { if (!editable(e.target)) e.preventDefault(); return; }
    e.preventDefault();
    depth = 0; overlay.hidden = true;
    if (dialogOpen()) return;
    const files = [...e.dataTransfer.files];
    // The builder takes pictures (reference images) and .obj files (shapes) itself.
    if (files.length && window.openplaneBuilder?.acceptDrop?.(files)) return;
    if (files.length) importFiles(files);
  });
})();

// ---------------------------------------------------------------- start
window.addEventListener('hashchange', route);
// The skip link moves keyboard focus to the screen without changing the address.
document.querySelector('.skip-link')?.addEventListener('click', e => { e.preventDefault(); document.getElementById('main')?.focus(); });
hydrateIcons(document.querySelector('.app-header'));
hydrateIcons(document.getElementById('drop-overlay'));
getStatus().then(st => { applyTheme(st.settings.theme); refreshHeader(); }).catch(() => {});
route();
collectOpened();
// A newer OpenPlane on GitHub (asked once per start; off in Settings).
api.get('/api/update').then(u => {
  if (!u?.newer) return;
  const b = document.getElementById('update-badge');
  b.innerHTML = `<span class="dot good"></span>OpenPlane ${esc(u.latest)} is out`;
  b.title = 'Open the release page to download it';
  b.onclick = () => api.post('/api/open-link', { path: u.url }).catch(showError);
  b.hidden = false;
}).catch(() => {});
setInterval(() => { if (!document.hidden) collectOpened(); }, 3000);

export { icon, html };
