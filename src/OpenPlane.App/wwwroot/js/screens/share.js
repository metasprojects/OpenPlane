// Share: export a plane (or several as a plane pack), import planes others shared, playing together, and the Workshop.
import { api, getStatus } from '../api.js';
import { html, esc, fmt, modal, showError, pickFolder, originText, timeAgo } from '../ui.js';
import { icon } from '../icons.js';
import { chooseFilesToImport, go } from '../app.js';

export async function render(main) {
  const page = html(`<div class="page">
    <div class="page-head">
      <div>
        <h1>Share</h1>
        <p>A plane is one <code>.oplane</code> file. Send it on Discord or anywhere; the other player imports it with OpenPlane and it appears in their game.
           Several planes go together as a plane pack (one <code>.zip</code>).</p>
      </div>
    </div>
    <div class="two-col">
      <section class="card pad">
        <div class="section-title"><h2>Your planes</h2>
          <div class="btn-row tight pack-bar" id="pack-bar">
            <span class="muted small" id="pack-count"></span>
            <button type="button" class="btn small" id="pack-clear" hidden>Clear</button>
            <button type="button" class="btn small primary" id="pack-save" hidden>${icon('export', 'sm')}Share as a pack…</button>
          </div>
        </div>
        <div id="list" class="share-list"><div class="loading-page"><span class="spinner"></span></div></div>
      </section>
      <div style="display:flex;flex-direction:column;gap:18px">
        <section class="card pad">
          <div class="section-title"><h2>Import a plane</h2></div>
          <div class="drop-zone">
            ${icon('import', 'big')}
            <strong>Drop .oplane files or plane packs here</strong>
            <span class="small">or a .zip of a Flyout craft folder. Dropping anywhere in OpenPlane works too.</span>
            <button type="button" class="btn primary" id="choose">Choose files…</button>
          </div>
        </section>
        <section class="card pad" id="together">
          <div class="section-title"><h2>Playing together</h2></div>
          <div id="together-body" class="muted"><span class="spinner"></span></div>
        </section>
        <section class="card pad">
          <div class="section-title"><h2>Steam Workshop</h2></div>
          <div style="display:flex;flex-direction:column;gap:10px" class="muted">
            <p>Every OpenPlane plane has its own Workshop tag, so <strong>liveries and missions</strong> made for it upload with
               Nuclear Option's own Workshop uploader, the same way as for stock planes. Tag them "Mod Required". Players who
               subscribe need the plane too.</p>
            <p>Uploading the <strong>plane files themselves</strong> to the Workshop will come once Nuclear Option's developers approve it.
               Until then, share the <code>.oplane</code> file or a plane pack directly.</p>
            <p>Please credit the original builder of any converted plane when you share it.</p>
          </div>
        </section>
      </div>
    </div>
  </div>`);
  main.append(page);
  page.querySelector('#choose').onclick = chooseFilesToImport;

  let planes = [];
  const picked = new Set();
  const bar = page.querySelector('#pack-bar');
  // The bar keeps its height whether or not planes are ticked, so ticking one doesn't move the list under the pointer.
  const syncBar = () => {
    const any = picked.size > 0;
    page.querySelector('#pack-count').textContent = any ? `${picked.size} selected` : (planes.length > 1 ? 'Tick planes to share several as one pack' : '');
    page.querySelector('#pack-clear').hidden = !any;
    page.querySelector('#pack-save').hidden = !any;
    bar.classList.toggle('active', any);
  };
  page.querySelector('#pack-clear').onclick = () => { picked.clear(); page.querySelectorAll('.share-row input[type=checkbox]').forEach(c => { c.checked = false; }); syncBar(); };
  page.querySelector('#pack-save').onclick = () => packDialog(planes.filter(p => picked.has(p.id)), { all: false });

  const load = async () => {
    const host = page.querySelector('#list');
    try {
      const data = await api.get('/api/planes');
      planes = data.planes.filter(p => !p.error);
      for (const id of [...picked]) if (!planes.some(p => p.id === id)) picked.delete(id);
      syncBar();
      if (!planes.length) {
        host.replaceChildren(html(`<div class="empty">${icon('plane', 'big')}<p>No planes to share yet.</p>
            <div class="btn-row"><button type="button" class="btn" data-go="#/convert">Convert from Flyout</button><button type="button" class="btn" data-go="#/build">Build a plane</button></div></div>`));
        host.querySelectorAll('[data-go]').forEach(b => b.onclick = () => go(b.dataset.go));
        renderTogether(page.querySelector('#together-body'), planes);
        return;
      }
      host.replaceChildren(...planes.map(p => {
        const row = html(`<div class="card share-row">
            <label class="pick" title="Select for a plane pack"><input type="checkbox" aria-label="Select ${esc(p.name)} for a plane pack" ${picked.has(p.id) ? 'checked' : ''}></label>
            <div class="thumb">${p.has_thumbnail ? `<img src="/api/planes/${p.id}/thumb?v=${encodeURIComponent(p.modified)}" alt="" loading="lazy">` : ''}</div>
            <div class="text"><strong>${esc(p.name)}</strong><span>${esc(originText(p.origin, p.author))} · <span class="num">${fmt(p.span_m, 1)} m</span></span></div>
            <button type="button" class="btn" aria-label="Share ${esc(p.name)}">${icon('export')}Share…</button>
          </div>`);
        row.querySelector('button').onclick = () => shareDialog(p);
        row.querySelector('input').onchange = e => { if (e.target.checked) picked.add(p.id); else picked.delete(p.id); syncBar(); };
        return row;
      }));
    } catch (e) { host.innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; }
    renderTogether(page.querySelector('#together-body'), planes);
  };
  window.addEventListener('openplane-imported', load);
  await load();
  return { title: 'Share', destroy() { window.removeEventListener('openplane-imported', load); } };
}

/** "Playing together": what lobby matching needs, the plane set the game loaded last, and one button to send it all. */
async function renderTogether(body, planes) {
  let g = null;
  try { g = await api.get('/api/game-report'); } catch { /* shown as "not started yet" */ }
  const rep = g?.found ? g.report : null;
  const loaded = rep ? (rep.planes || []).filter(p => !p.skipped).length : 0;
  const since = rep?.loaded_at ? timeAgo(rep.loaded_at) : '';
  const changed = rep && planes.some(p => new Date(p.modified) > new Date(rep.loaded_at));
  const blocked = rep ? (rep.planes || []).filter(p => p.host_blocked) : [];
  body.replaceChildren(html(`<div style="display:flex;flex-direction:column;gap:10px">
      <p>Nuclear Option sends planes over the network by number, so everyone in a game needs <strong>exactly the same OpenPlane
         planes</strong>. The game only shows you lobbies whose players have the same set as you.</p>
      ${rep && rep.signature && rep.signature !== 'none'
        ? `<div class="kv"><span>Your plane set</span><strong class="num">${esc(rep.signature)}</strong><span class="faint small">${loaded} plane${loaded === 1 ? '' : 's'}, when Nuclear Option started ${esc(since)}</span></div>
           ${changed ? `<div class="notice warn">${icon('warn')}<div>You changed planes since then. Restart Nuclear Option so your set (and lobby list) is up to date.</div></div>` : ''}`
        : `<p class="faint small">Start Nuclear Option once with the OpenPlane mod and your plane set shows here.</p>`}
      <p><strong>To play with friends:</strong> send them all your planes as one pack. They drop it on OpenPlane (older versions
         of the same planes are updated) and restart the game. Planes they have that you don't also change the set, so
         remove extras or send yours back.</p>
      <div class="btn-row"><button type="button" class="btn primary" id="pack-all" ${planes.length ? '' : 'disabled'}>${icon('export')}Share all ${planes.length} as a pack…</button>
        <button type="button" class="btn" id="host-rules">${icon('settings')}Host settings</button></div>
      ${blocked.length ? `<p class="faint small">${blocked.length} of your planes can't be flown in games you host (your host settings).</p>` : ''}
    </div>`));
  body.querySelector('#pack-all').onclick = () => packDialog(planes, { all: true });
  body.querySelector('#host-rules').onclick = () => go('#/settings?section=multiplayer');
}

/** Saves several planes as one plane pack: permission check for converted planes, then Downloads or a folder. */
async function packDialog(list, { all }) {
  if (!list.length) return;
  const converted = list.filter(p => p.origin?.source === 'flyout');
  if (converted.length) {
    const ok = await modal(close => {
      const el = html(`<div style="display:contents">
          <h2>Did you build ${converted.length === 1 ? 'this plane' : 'these planes'}?</h2>
          <p class="muted">${converted.length === 1
            ? `"${esc(converted[0].name)}" was converted from a Flyout craft${converted[0].origin?.builder ? ` built by <strong>${esc(converted[0].origin.builder)}</strong>` : ''}.
               Share it only if you built it yourself, or its builder said it's fine. Their credit stays inside the file.`
            : `${converted.length} of these planes were converted from Flyout craft. Share them only if you built them yourself, or their
               builders said it's fine. Each builder's credit stays inside the file.`}</p>
          <div class="btn-row">
            <button type="button" class="btn" data-a="no">Cancel</button>
            <button type="button" class="btn primary" data-a="yes">I built ${converted.length === 1 ? 'it' : 'them'}, or I have permission</button>
          </div>
        </div>`);
      el.querySelector('[data-a=no]').onclick = () => close(false);
      el.querySelector('[data-a=yes]').onclick = () => close(true);
      return el;
    }, { label: 'Did you build these planes?' });
    if (!ok) return;
  }
  const st = await getStatus().catch(() => null);
  await modal(close => {
    const el = html(`<div style="display:contents">
        <h2>Share ${list.length} plane${list.length === 1 ? '' : 's'} as a pack</h2>
        <p class="muted">OpenPlane saves one <code>.zip</code> with ${list.length === 1 ? 'this plane' : 'these planes'}. Friends drop it on OpenPlane
           (or import it on the Share screen) and restart Nuclear Option.</p>
        <label class="field"><span>Pack name</span><input type="text" id="pack-name" maxlength="80" value="${esc(all ? 'My OpenPlane planes' : 'OpenPlane planes')}"></label>
        <ul class="pack-names">${list.slice(0, 12).map(p => `<li>${esc(p.name)}</li>`).join('')}${list.length > 12 ? `<li class="faint">and ${list.length - 12} more</li>` : ''}</ul>
        <div id="result"></div>
        <div class="btn-row">
          <button type="button" class="btn" data-a="close">Close</button>
          <button type="button" class="btn" data-a="folder">${icon('folder')}Choose a folder…</button>
          <button type="button" class="btn primary" data-a="downloads">${icon('export')}Save to Downloads</button>
        </div>
      </div>`);
    const result = el.querySelector('#result');
    const name = () => el.querySelector('#pack-name').value.trim() || 'OpenPlane planes';
    const payload = extra => ({ ...(all ? { all: true } : { ids: list.map(p => p.id) }), name: name(), ...extra });
    const done = r => {
      result.replaceChildren(html(`<div class="notice good">${icon('checkCircle')}<div>Saved ${r.count} plane${r.count === 1 ? '' : 's'} to <code>${esc(r.path)}</code>
          <div class="btn-row" style="margin-top:8px"><button type="button" class="btn small" data-show>${icon('folder', 'sm')}Show in folder</button></div></div></div>`));
      result.querySelector('[data-show]').onclick = () => api.post('/api/reveal-path', { path: r.path }).catch(showError);
      el.querySelector('[data-a=downloads]').classList.remove('primary');
      el.querySelector('[data-a=close]').classList.add('primary');
      el.querySelector('[data-a=close]').focus();
    };
    el.querySelector('[data-a=close]').onclick = () => close();
    el.querySelector('[data-a=downloads]').onclick = async () => {
      try { done(await api.post('/api/planes/export-pack', payload({ to: 'downloads' }))); } catch (e) { showError(e); }
    };
    el.querySelector('[data-a=folder]').onclick = async () => {
      const folder = await pickFolder('Choose where to save the plane pack', st?.paths?.downloads_dir);
      if (!folder) return;
      try { done(await api.post('/api/planes/export-pack', payload({ to: 'folder', folder }))); } catch (e) { showError(e); }
    };
    return el;
  }, { label: 'Share as a plane pack' });
}

/** The share flow for one plane: permission check for converted planes, then save to Downloads or a folder. */
export async function shareDialog(plane) {
  const converted = plane.origin?.source === 'flyout';
  if (converted) {
    const ok = await modal(close => {
      const el = html(`<div style="display:contents">
          <h2>Did you build this plane?</h2>
          <p class="muted">"${esc(plane.name)}" was converted from a Flyout craft${plane.origin.builder ? ` built by <strong>${esc(plane.origin.builder)}</strong>` : ''}.
             Share it only if you built it yourself, or the builder said it's fine. Their credit stays inside the file.</p>
          <div class="btn-row">
            <button type="button" class="btn" data-a="no">Cancel</button>
            <button type="button" class="btn primary" data-a="yes">I built it, or I have the builder's permission</button>
          </div>
        </div>`);
      el.querySelector('[data-a=no]').onclick = () => close(false);
      el.querySelector('[data-a=yes]').onclick = () => close(true);
      return el;
    }, { label: 'Did you build this plane?' });
    if (!ok) return;
  }
  const st = await getStatus().catch(() => null);
  await modal(close => {
    const el = html(`<div style="display:contents">
        <h2>Share "${esc(plane.name)}"</h2>
        <p class="muted">OpenPlane saves a copy of <code>${esc(plane.file_name)}</code>. Send that file to anyone with OpenPlane;
           they import it (or drop it on OpenPlane) and the plane appears in their game.${plane.origin?.builder ? ` It keeps the credit to ${esc(plane.origin.builder)}.` : ''}</p>
        <label class="check" id="refs-row" hidden><input type="checkbox" id="inc-refs"><span>Include reference images <span class="faint small">(blueprints you traced; leave them out if they aren't yours to share)</span></span></label>
        <div id="result"></div>
        <div class="btn-row">
          <button type="button" class="btn" data-a="close">Close</button>
          <button type="button" class="btn" data-a="folder">${icon('folder')}Choose a folder…</button>
          <button type="button" class="btn primary" data-a="downloads">${icon('export')}Save to Downloads</button>
        </div>
      </div>`);
    const result = el.querySelector('#result');
    const incRefs = () => el.querySelector('#inc-refs').checked;
    api.get(`/api/planes/${plane.id}`).then(p => { if ((p.spec?.references || []).length) el.querySelector('#refs-row').hidden = false; }).catch(() => {});
    const done = r => {
      result.replaceChildren(html(`<div class="notice good">${icon('checkCircle')}<div>Saved to <code>${esc(r.path)}</code>
          <div class="btn-row" style="margin-top:8px"><button type="button" class="btn small" data-show>${icon('folder', 'sm')}Show in folder</button></div></div></div>`));
      result.querySelector('[data-show]').onclick = () => api.post('/api/reveal-path', { path: r.path }).catch(showError);
      el.querySelector('[data-a=downloads]').classList.remove('primary');
      el.querySelector('[data-a=close]').classList.add('primary');
      el.querySelector('[data-a=close]').focus();
    };
    el.querySelector('[data-a=close]').onclick = () => close();
    el.querySelector('[data-a=downloads]').onclick = async () => {
      try { done(await api.post(`/api/planes/${plane.id}/export`, { to: 'downloads', include_references: incRefs() })); }
      catch (e) { showError(e); }
    };
    el.querySelector('[data-a=folder]').onclick = async () => {
      const folder = await pickFolder('Choose where to save the plane', st?.paths?.downloads_dir);
      if (!folder) return;
      try { done(await api.post(`/api/planes/${plane.id}/export`, { to: 'folder', folder, include_references: incRefs() })); }
      catch (e) { showError(e); }
    };
    return el;
  }, { label: `Share ${plane.name}` });
}
