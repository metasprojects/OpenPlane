// Home / My planes: the three big actions, game and mod status, and the saved planes.
import { api, getStatus } from '../api.js';
import { html, esc, fmt, toast, showError, timeAgo, originText, donorLabel } from '../ui.js';
import { icon } from '../icons.js';
import { go, chooseFilesToImport, refreshHeader, launchGame } from '../app.js';
import { shareDialog } from './share.js';
import { installModFlow } from './settings.js';
import { renderDrafts } from '../drafts.js';

export async function render(main) {
  const page = html(`<div class="page">
    <div class="page-head">
      <div>
        <h1>My planes</h1>
        <p>Planes saved here show up in Nuclear Option once the OpenPlane mod is installed.</p>
      </div>
    </div>
    <div class="hero-actions">
      <button type="button" class="hero-action" data-go="#/convert">
        <span class="badge">${icon('convert')}</span>
        <span><strong>Convert from Flyout</strong><span>Turn a plane you built in Flyout into a Nuclear Option plane.</span></span>
      </button>
      <button type="button" class="hero-action" data-go="#/build">
        <span class="badge">${icon('build')}</span>
        <span><strong>Build a new plane</strong><span>Start from a trainer, fighter or bomber, or from scratch.</span></span>
      </button>
      <button type="button" class="hero-action" data-act="import">
        <span class="badge">${icon('import')}</span>
        <span><strong>Import a plane</strong><span>Add a .oplane file someone shared, or a zipped Flyout craft. You can also drop files anywhere.</span></span>
      </button>
    </div>
    <div class="status-strip" id="status-strip"></div>
    <div id="checklist"></div>
    <div id="drafts"></div>
    <section>
      <div class="section-title"><h2 id="planes-title">Saved planes</h2><span class="muted small" id="planes-folder"></span></div>
      <div id="planes"><div class="loading-page"><span class="spinner"></span></div></div>
    </section>
  </div>`);
  main.append(page);
  page.querySelectorAll('[data-go]').forEach(b => b.onclick = () => go(b.dataset.go));
  page.querySelector('[data-act=import]').onclick = chooseFilesToImport;

  const onImported = () => { loadPlanes(page); renderChecklist(page); };
  renderDrafts(page.querySelector('#drafts'), go, () => loadPlanes(page));
  window.addEventListener('openplane-imported', onImported);
  renderStatus(page);
  loadPlanes(page);
  renderChecklist(page);
  return {
    title: 'My planes',
    destroy() { window.removeEventListener('openplane-imported', onImported); },
  };
}

async function renderStatus(page) {
  const strip = page.querySelector('#status-strip');
  let st;
  try { st = await getStatus(true); } catch (e) { strip.innerHTML = ''; return; }
  const m = st.mod;
  const game = st.paths.game;
  const gameOk = game.valid;
  const gameItem = html(`<div class="card status-item">
      <span class="dot ${gameOk ? 'good' : 'warn'}"></span>
      <div class="text"><strong>${gameOk ? 'Nuclear Option found' : 'Nuclear Option not found'}</strong>
        <span title="${esc(game.path || '')}">${gameOk ? esc(game.path) : 'OpenPlane needs the game folder to install its mod.'}</span></div>
      ${gameOk ? '' : `<button type="button" class="btn small" data-fix="game">Find it</button>`}
    </div>`);
  let modText = m.summary, modDot = 'good', fix = '';
  if (m.state === 'ready') modText = `Installed (version ${m.mod_version || '?'}). Blueprinter ${m.blueprinter_installed ? 'and BepInEx are ready.' : ''}`;
  if (m.state === 'needs-install') { modDot = 'warn'; fix = `<button type="button" class="btn small primary" data-fix="install">Install</button>`; }
  if (m.state === 'needs-update') { modDot = 'warn'; fix = `<button type="button" class="btn small primary" data-fix="install">Update</button>`; }
  if (m.state === 'no-game') { modDot = 'warn'; modText = 'Install it once the game is found.'; }
  if (m.state === 'no-bundle') { modDot = 'bad'; }
  if (m.state === 'blueprinter-old') modDot = 'warn';
  const modItem = html(`<div class="card status-item">
      <span class="dot ${modDot}"></span>
      <div class="text"><strong>OpenPlane mod</strong><span title="${esc(m.summary)}">${esc(modText)}</span></div>
      ${fix}
    </div>`);
  strip.replaceChildren(gameItem, modItem);
  gameItem.querySelector('[data-fix=game]')?.addEventListener('click', () => go('#/settings?focus=game'));
  modItem.querySelector('[data-fix=install]')?.addEventListener('click', async () => {
    await installModFlow();
    renderStatus(page);
    refreshHeader(true);
  });
}

/** Adds the three starter planes; tells the player what happened. */
export async function addStarters() {
  try {
    const r = await api.post('/api/planes/starters');
    const added = r.results.filter(x => x.outcome === 'added').map(x => x.plane.name);
    toast(added.length ? `Added ${added.join(', ')}. Press Test fly on one to fly it.` : 'You already have the three starter planes.', { kind: added.length ? 'good' : 'info', timeout: 9000 });
    window.dispatchEvent(new CustomEvent('openplane-imported', { detail: r.results }));
  } catch (e) { showError(e); }
}

/**
 * "Getting started": the steps from a fresh install to flying and sharing, ticked off from what OpenPlane can see
 * (game found, mod installed, a plane saved, the game started with the mod, a plane shared). Hidden when all are done or
 * when the player closes it.
 */
async function renderChecklist(page) {
  const host = page.querySelector('#checklist');
  let st, planes, report;
  try {
    [st, planes, report] = await Promise.all([getStatus(true), api.get('/api/planes'), api.get('/api/game-report').catch(() => ({ found: false }))]);
  } catch { host.replaceChildren(); return; }
  const s = st.settings || {};
  const list = planes.planes.filter(p => !p.error);
  const flown = report.found && (report.report?.planes || []).some(p => !p.skipped);
  const modOk = st.mod.state === 'ready' || st.mod.state === 'needs-update';
  // The plane to fly first: the starter trainer if it's there (the easiest to fly), else the newest plane.
  const first = list.find(p => p.design_id === '57a47e4000000001') || list[0];
  const steps = [
    { done: st.paths.game.valid, title: 'Find Nuclear Option', text: 'OpenPlane installs its mod into the game.', actions: [['Find it', () => go('#/settings?focus=game')]] },
    { done: modOk, title: 'Install the OpenPlane mod', text: 'It loads your planes into the game (with BepInEx and Blueprinter).', actions: [['Install', async () => { await installModFlow(); refreshHeader(true); renderStatus(page); renderChecklist(page); }]] },
    { done: list.length > 0, title: 'Get a plane', text: 'Add the three starter planes, convert one from Flyout, or build your own.',
      actions: [['Add starter planes', () => addStarters(), true], ['Convert', () => go('#/convert')], ['Build', () => go('#/build')]] },
    { done: flown, title: 'Fly it', text: 'Test fly opens the game straight into a flight in your plane.',
      actions: first ? [[`Test fly ${first.name}`, () => launchGame({ planeName: first.name, donor: donorLabel(first.donor), testFlyPlaneId: first.id })]] : [] },
    { done: !!s.shared_once, title: 'Share it', text: 'Send a plane, or all of them as a pack, to fly together.', actions: [['Share', () => go('#/share')]] },
  ];
  const done = steps.filter(x => x.done).length;
  if (s.getting_started_hidden || done === steps.length) { host.replaceChildren(); return; }
  const next = steps.findIndex(x => !x.done);
  const el = html(`<section class="card pad checklist" aria-labelledby="gs-h">
      <div class="section-title"><h2 id="gs-h">Getting started</h2>
        <div class="btn-row tight"><span class="muted small">${done} of ${steps.length} done</span><button type="button" class="btn small ghost" data-hide>Hide</button></div></div>
      <ol class="gs-steps">${steps.map((x, i) => `<li class="${x.done ? 'done' : i === next ? 'next' : ''}">
          <span class="gs-mark">${x.done ? icon('check', 'sm') : i + 1}</span>
          <div class="gs-text"><strong>${esc(x.title)}</strong><span class="muted small">${esc(x.text)}</span></div>
          <div class="btn-row tight">${x.done ? '' : x.actions.map(([label], j) => `<button type="button" class="btn small ${i === next && j === 0 ? 'primary' : ''}" data-s="${i}" data-j="${j}">${esc(label)}</button>`).join('')}</div>
        </li>`).join('')}</ol>
    </section>`);
  el.querySelectorAll('[data-s]').forEach(b => b.onclick = () => steps[+b.dataset.s].actions[+b.dataset.j][1]());
  el.querySelector('[data-hide]').onclick = async () => {
    try { const cur = (await api.get('/api/settings')).settings; await api.put('/api/settings', { ...cur, getting_started_hidden: true }); host.replaceChildren(); toast('Hidden. You can show it again in Settings.', { kind: 'info' }); }
    catch (e) { showError(e); }
  };
  host.replaceChildren(el);
}

async function loadPlanes(page) {
  const host = page.querySelector('#planes');
  let data;
  let drafts = new Set();
  try {
    const [planes, d, g] = await Promise.all([api.get('/api/planes'), api.get('/api/drafts').catch(() => ({ drafts: [] })), api.get('/api/game-report').catch(() => ({ found: false }))]);
    data = planes;
    gameReports = new Map(((g.found && g.report?.planes) || []).map(r => [String(r.file || '').toLowerCase(), r]));
    drafts = new Set(d.drafts.filter(x => x.plane_id).map(x => x.plane_id));
  }
  catch (e) { host.innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; return; }
  page.querySelector('#planes-folder').textContent = data.folder.path ? `Folder: ${data.folder.path}` : '';
  page.querySelector('#planes-title').textContent = data.planes.length ? `Saved planes (${data.planes.length})` : 'Saved planes';
  if (!data.planes.length) {
    host.replaceChildren(html(`<div class="card empty">
        ${icon('plane', 'big')}
        <h3>No planes yet</h3>
        <p>Convert one from Flyout or build your own, or start with OpenPlane's three starter planes. Planes you import also appear here.</p>
        <div class="btn-row"><button type="button" class="btn primary" data-starters>Add starter planes</button><button type="button" class="btn" data-go="#/convert">Convert from Flyout</button><button type="button" class="btn" data-go="#/build">Build a new plane</button></div>
      </div>`));
    host.querySelectorAll('[data-go]').forEach(b => b.onclick = () => go(b.dataset.go));
    host.querySelector('[data-starters]').onclick = addStarters;
    return;
  }
  const grid = html(`<div class="plane-grid"></div>`);
  for (const p of data.planes) grid.append(planeCard(p, page, drafts.has(p.id)));
  host.replaceChildren(grid);
}

/** What the OpenPlane mod did with each plane file the last time the game started, by file name. */
let gameReports = new Map();

/** One line for a plane card about the last game start, or ''. */
function gameLine(p) {
  const r = gameReports.get(String(p.file_name || '').toLowerCase());
  if (!r) return '';
  const text = r.skipped ? `Not in the game: ${r.skipped}`
    : r.host_blocked ? `Not flyable in games you host: ${r.host_blocked} (Settings, Multiplayer).`
    : r.warnings?.length ? `In the game: ${r.warnings[0]}`
    : r.not_offered_at?.length ? `In the game: not offered at ${r.not_offered_at.length} airbase${r.not_offered_at.length === 1 ? '' : 's'} with too short a runway. Open it for details.`
    : r.changes?.length ? 'In the game: OpenPlane adjusted it to fly. Open it for details.' : '';
  if (!text) return '';
  const bad = r.skipped || r.warnings?.length;
  return `<div class="small" style="color:var(${bad ? '--warn' : '--muted'})" role="note">${esc(text)}</div>`;
}

function planeCard(p, page, hasDraft = false) {
  const thumb = p.has_thumbnail ? `<img src="/api/planes/${p.id}/thumb?v=${encodeURIComponent(p.modified)}" alt="Top, side and front view of ${esc(p.name)}" loading="lazy">`
    : `${icon('plane', 'big')}`;
  const card = html(`<article class="card plane-card ${p.error ? 'broken' : ''}" aria-label="${esc(p.name)}">
      <div class="thumb">${thumb}</div>
      <div class="body">
        <div class="name" title="${esc(p.name)}">${esc(p.name)}</div>
        ${p.error ? `<div class="small" style="color:var(--bad)">${esc(p.error)}</div>` : `
        <div class="meta"><span class="num">${fmt(p.span_m, 1)} m span</span><span class="num">${fmt(p.length_m, 1)} m long</span><span title="${esc(donorLabel(p.donor))}">on ${esc(p.donor)}</span></div>
        <div class="credit" title="${esc(originText(p.origin, p.author))}">${esc(originText(p.origin, p.author))}</div>
        ${p.warning ? `<div class="small" style="color:var(--warn)" role="note">${esc(p.warning)}</div>` : ''}
        ${gameLine(p)}`}
        <div class="faint small">Changed ${esc(timeAgo(p.modified))}${hasDraft ? ' · <span class="pill note" title="You left this plane with changes that are not saved. Edit it to continue them.">Unsaved changes</span>' : ''}</div>
      </div>
      <div class="actions">
        ${p.error ? '<span style="margin-right:auto"></span>' : `<button type="button" class="btn small" data-a="edit">${icon('edit', 'sm')}Edit</button>`}
        ${p.error ? '' : `<button type="button" class="icon-btn" data-a="share" title="Share" aria-label="Share ${esc(p.name)}">${icon('share')}</button>
        <button type="button" class="icon-btn" data-a="dup" title="Duplicate" aria-label="Duplicate ${esc(p.name)}">${icon('copy')}</button>`}
        <button type="button" class="icon-btn" data-a="reveal" title="Show in folder" aria-label="Show ${esc(p.name)} in its folder">${icon('folder')}</button>
        <button type="button" class="icon-btn danger" data-a="delete" title="Delete" aria-label="Delete ${esc(p.name)}">${icon('trash')}</button>
      </div>
    </article>`);
  const on = (a, fn) => card.querySelector(`[data-a=${a}]`)?.addEventListener('click', fn);
  on('edit', () => go(`#/build/${p.id}`));
  on('share', () => shareDialog(p));
  on('dup', async () => {
    try {
      const r = await api.post(`/api/planes/${p.id}/duplicate`);
      toast(`Made a copy: "${r.plane.name}".`, { kind: 'good' });
      loadPlanes(page);
    } catch (e) { showError(e); }
  });
  on('reveal', () => api.post(`/api/planes/${p.id}/reveal`).catch(showError));
  on('delete', () => {
    if (card.querySelector('.confirm')) return;
    const box = html(`<div class="confirm" role="group" aria-label="Confirm delete">
        <span>Delete <strong>${esc(p.name)}</strong>? It moves to OpenPlane's "Deleted planes" folder and leaves the game.</span>
        <div class="btn-row"><button type="button" class="btn small danger solid" data-c="yes">Delete</button><button type="button" class="btn small" data-c="no">Keep it</button></div>
      </div>`);
    card.append(box);
    box.querySelector('[data-c=no]').focus();
    box.querySelector('[data-c=no]').onclick = () => { box.remove(); card.querySelector('[data-a=delete]').focus(); };
    box.querySelector('[data-c=yes]').onclick = async () => {
      try {
        const r = await api.del(`/api/planes/${p.id}`);
        card.remove();
        toast(`Deleted "${r.name}".`, {
          action: { label: 'Undo', run: async () => { try { await api.post('/api/planes/restore', { token: r.token }); loadPlanes(page); } catch (e) { showError(e); } } },
        });
        loadPlanes(page);
      } catch (e) { showError(e); }
    };
  });
  return card;
}
