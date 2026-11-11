// Convert: pick Flyout craft, say who built them, convert, then read what carried over.
import { api, getStatus } from '../api.js';
import { html, esc, fmt, fmtKg, showError, toast, pickFolder, confirmDialog, promptDialog, timeAgo, donorLabel } from '../ui.js';
import { icon } from '../icons.js';
import { go, chooseFilesToImport } from '../app.js';

export async function render(main, args, query) {
  const state = { craft: [], folders: [], selected: new Set((query.get('select') || '').split(',').filter(Boolean)), filter: '' };
  const page = html(`<div class="page fill">
    <div class="page-head">
      <div>
        <h1>Convert from Flyout</h1>
        <p>Pick the planes you want in Nuclear Option. OpenPlane keeps their exact wings and fuselage, swaps Flyout's engines,
           guns and weapons for Nuclear Option's own, and tells you exactly what changed.</p>
      </div>
    </div>
    <div class="toolbar">
      <label class="search"><span class="sr-only">Search craft</span>${icon('search')}<input type="search" id="q" placeholder="Search by name"></label>
      <button type="button" class="btn" id="add-folder">${icon('folder')}Add a folder…</button>
      <button type="button" class="btn" id="import-zip">${icon('import')}Import a zip…</button>
      <button type="button" class="btn ghost" id="select-all">Select all</button>
      <button type="button" class="btn ghost" id="refresh" title="Look for craft again">${icon('refresh')}Refresh</button>
    </div>
    <details class="card pad" id="folders"><summary class="muted small" style="cursor:pointer">Folders OpenPlane looks in</summary><div id="folder-list" style="margin-top:10px"></div></details>
    <div id="craft"><div class="loading-page"><span class="spinner"></span>Looking for Flyout craft…</div></div>
    <div class="sticky-bar" id="bar">
      <div class="builder-name">
        <label for="builder" class="small" style="font-weight:600">Who built ${'this plane'}?</label>
        <input type="text" id="builder" placeholder="The builder's name" autocomplete="off">
        <button type="button" class="btn small" id="i-did">I did</button>
      </div>
      <label class="check small" id="replace-wrap" hidden title="Only planes converted from craft of the same name are replaced; planes you built never are. The old ones move to OpenPlane's Deleted planes folder, so you can undo it."><input type="checkbox" id="replace">Replace the planes I converted from these before (the old ones go to Deleted planes)</label>
      <span class="spacer"></span>
      <span class="muted small" id="sel-count"></span>
      <button type="button" class="btn primary big" id="go" disabled>${icon('convert')}Convert</button>
    </div>
  </div>`);
  main.append(page);

  const $ = s => page.querySelector(s);
  const status = await getStatus().catch(() => null);
  const yourName = status?.settings?.your_name || '';

  async function load() {
    try {
      const data = await api.get('/api/flyout');
      state.craft = data.craft;
      state.folders = data.folders;
      draw();
    } catch (e) { $('#craft').innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; }
  }

  function drawFolders() {
    const list = $('#folder-list');
    list.innerHTML = '';
    for (const f of state.folders) {
      const row = html(`<div class="path-row" style="align-items:center;margin-bottom:6px">
          <span class="dot ${f.exists ? 'good' : ''}"></span>
          <code style="flex:1">${esc(f.path)}</code>
          <span class="path-source">${esc(f.source === 'imported' ? 'imported zips' : f.source)}${f.exists ? '' : ' · not found'}</span>
          ${f.source === 'settings' || f.source === 'detected' ? `<button type="button" class="btn small ghost" data-rm>${icon('x', 'sm')}Remove</button>` : ''}
        </div>`);
      row.querySelector('[data-rm]')?.addEventListener('click', async () => {
        try { const r = await api.post('/api/flyout/folders/remove', { path: f.path }); state.folders = r.folders; load(); }
        catch (e) { showError(e); }
      });
      list.append(row);
    }
  }

  function visible() {
    const q = state.filter.toLowerCase();
    return state.craft.filter(c => !q || c.name.toLowerCase().includes(q));
  }

  function draw() {
    drawFolders();
    const host = $('#craft');
    // Forget selections that no longer exist.
    for (const id of [...state.selected]) if (!state.craft.some(c => c.id === id && !c.helicopter)) state.selected.delete(id);
    if (!state.craft.length) {
      const found = state.folders.some(f => f.exists && f.source !== 'imported');
      host.replaceChildren(html(`<div class="card empty">
          ${icon('search', 'big')}
          <h3>No Flyout craft found</h3>
          <p>${found ? 'The folders OpenPlane looks in have no craft yet.' : "Flyout's craft folder wasn't found on this computer."}
             Add the folder that holds your craft, or import a zipped craft folder someone shared.</p>
          <div class="btn-row"><button type="button" class="btn primary" data-a="add">${icon('folder')}Add a folder…</button><button type="button" class="btn" data-a="zip">${icon('import')}Import a zip…</button></div>
        </div>`));
      host.querySelector('[data-a=add]').onclick = addFolder;
      host.querySelector('[data-a=zip]').onclick = chooseFilesToImport;
      updateBar();
      return;
    }
    const list = visible();
    const grid = html(`<div class="craft-grid" role="list"></div>`);
    if (!list.length) grid.append(html(`<p class="muted">No craft match "${esc(state.filter)}".</p>`));
    for (const c of list) {
      const on = state.selected.has(c.id);
      const card = html(`<button type="button" class="card craft-card ${c.helicopter ? 'disabled' : ''}" role="listitem" aria-pressed="${on}" ${c.helicopter ? 'aria-disabled="true"' : ''}>
          <span class="tick">${icon('check', 'sm')}</span>
          <span class="thumb"><img src="/api/flyout/${c.id}/thumb" alt="" loading="lazy" onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'loading',textContent:'No preview'}))"></span>
          <span class="body">
            <strong>${esc(c.name)}</strong>
            <span class="muted small">Flyout ${esc(c.version || 'save')} · changed ${esc(timeAgo(c.modified))}</span>
            <span class="tags">
              ${c.helicopter ? `<span class="pill warn">${icon('helicopter', 'sm')}Helicopter: not supported yet</span>` : ''}
              ${c.uses_mod_parts ? '<span class="pill note" title="Parts from Flyout mods are left out">Uses Flyout mod parts</span>' : ''}
              ${c.converted_plane_id ? '<span class="pill accent">Converted before</span>' : ''}
              ${c.source === 'imported' ? '<span class="pill">Imported</span>' : ''}
            </span>
          </span>
        </button>`);
      card.onclick = () => {
        if (c.helicopter) {
          toast("Helicopters aren't supported yet: OpenPlane builds fixed-wing planes only for now.", { timeout: 7000 });
          return;
        }
        if (state.selected.has(c.id)) state.selected.delete(c.id); else state.selected.add(c.id);
        card.setAttribute('aria-pressed', state.selected.has(c.id));
        updateBar();
      };
      grid.append(card);
    }
    host.replaceChildren(grid);
    updateBar();
  }

  function updateBar() {
    const n = state.selected.size;
    $('#sel-count').textContent = n ? `${n} selected` : 'Select one or more craft';
    $('#go').disabled = n === 0;
    $('#go').lastChild.textContent = n > 1 ? `Convert ${n} planes` : 'Convert';
    const again = state.craft.some(c => state.selected.has(c.id) && c.converted_plane_id);
    $('#replace-wrap').hidden = !again;
    page.querySelector('label[for=builder]').textContent = n > 1 ? 'Who built these planes?' : 'Who built this plane?';
  }

  async function addFolder() {
    const path = await pickFolder('Choose a folder with Flyout craft');
    if (!path) return;
    try {
      const r = await api.post('/api/flyout/folders', { path });
      toast(r.found ? `Found ${r.found} craft in that folder.` : 'No Flyout craft (folders with data.txt) were found in that folder yet.', { kind: r.found ? 'good' : 'info' });
      load();
    } catch (e) { showError(e); }
  }

  $('#q').addEventListener('input', e => { state.filter = e.target.value; draw(); });
  $('#add-folder').onclick = addFolder;
  $('#import-zip').onclick = chooseFilesToImport;
  $('#refresh').onclick = load;
  $('#select-all').onclick = () => {
    const ids = visible().filter(c => !c.helicopter).map(c => c.id);
    const all = ids.every(id => state.selected.has(id));
    ids.forEach(id => all ? state.selected.delete(id) : state.selected.add(id));
    draw();
  };
  $('#i-did').onclick = async () => {
    let name = yourName || (await getStatus(true).catch(() => null))?.settings?.your_name;
    if (!name) {
      name = await promptDialog({ title: "What's your name?", label: 'Your name (as players know you)', confirm: 'Save', body: 'OpenPlane credits you as the builder. It remembers this for next time (change it in Settings).' });
      if (!name) return;
      try {
        const s = (await api.get('/api/settings')).settings;
        await api.put('/api/settings', { ...s, your_name: name });
      } catch (e) { showError(e); }
    }
    $('#builder').value = name;
  };
  $('#go').onclick = async () => {
    const ids = [...state.selected];
    if (!ids.length) return;
    let builder = $('#builder').value.trim();
    if (!builder) {
      const ok = await confirmDialog({
        title: 'Convert without a builder name?',
        body: "Flyout files don't say who built a plane. Without a name, the plane will say \"builder unknown\". Please credit the builder when you share it.",
        confirm: 'Convert anyway', cancel: 'Add a name',
      });
      if (!ok) { $('#builder').focus(); return; }
    }
    // Only when the box is shown (some selected craft were converted before) and ticked.
    const replace = !$('#replace-wrap').hidden && $('#replace').checked;
    const btn = $('#go');
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner"></span>Converting ${ids.length > 1 ? `${ids.length} planes` : ''}…`;
    try {
      const r = await api.post('/api/flyout/convert', { ids, builder: builder || null, replace });
      showResults(main, r.results);
    } catch (e) {
      showError(e);
      btn.disabled = false;
      btn.innerHTML = `${icon('convert')}Convert`;
    }
  };

  window.addEventListener('openplane-imported', load);
  await load();
  return { title: 'Convert', destroy() { window.removeEventListener('openplane-imported', load); } };
}

// ---------------------------------------------------------------- results

const GROUPS = [
  { level: 'info', cls: 'good', title: 'Carried over', icon: 'checkCircle' },
  { level: 'approximated', cls: 'approx', title: 'Approximated', icon: 'info' },
  { level: 'dropped', cls: 'dropped', title: 'Left out', icon: 'minus' },
  { level: 'problem', cls: 'problem', title: 'Problems', icon: 'warn' },
];

export function reportGroups(report) {
  const wrap = html(`<div class="report-groups"></div>`);
  for (const g of GROUPS) {
    const items = report.filter(i => i.level === g.level);
    if (!items.length) continue;
    wrap.append(html(`<div class="report-group ${g.cls}">
        <h4>${icon(g.icon, 'sm')}${g.title} (${items.length})</h4>
        <ul>${items.map(i => `<li>${esc(i.message)}</li>`).join('')}</ul>
      </div>`));
  }
  return wrap;
}

export function stabilityPill(flight) {
  const sm = flight.static_margin_percent;
  const cls = flight.wing_area_m2 <= 0 || sm < -10 ? 'bad' : sm < 3 || sm > 25 ? 'warn' : 'good';
  return `<span class="pill ${cls}">${esc(flight.stability || '–')}${flight.wing_area_m2 > 0 ? ` · ${fmt(sm, 0)}%` : ''}</span>`;
}

export function flightGauges(flight, stats) {
  const g = (k, v, unit) => `<div class="gauge"><div class="k">${k}</div><div class="v">${v}${unit ? `<small>${unit}</small>` : ''}</div></div>`;
  return `<div class="readouts">
    ${g('Gross weight', fmtKg(flight.gross_mass_kg), '')}
    ${g('Span', fmt(stats?.span_m ?? flight.span_m, 1), 'm')}
    ${g('Length', fmt(stats?.length_m, 1), 'm')}
    ${g('Wing area', fmt(flight.wing_area_m2, 1), 'm²')}
    ${g('Thrust/weight', fmt(flight.thrust_to_weight, 2), '')}
    ${g('Stall speed', fmt(flight.stall_speed_knots, 0), 'kt')}
  </div>`;
}

function showResults(main, results) {
  const ok = results.filter(r => r.ok);
  const page = html(`<div class="page">
    <div class="page-head">
      <div>
        <h1>${ok.length ? `Converted ${ok.length} plane${ok.length === 1 ? '' : 's'}` : 'Nothing was converted'}</h1>
        <p>${ok.length ? "They're saved in your planes folder. Here's what carried over and what changed, plane by plane." : 'See the reasons below.'}</p>
      </div>
      <div class="btn-row">
        <button type="button" class="btn" id="more">${icon('convert')}Convert more</button>
        <button type="button" class="btn primary" id="done">Done</button>
      </div>
    </div>
    <div id="results" style="display:flex;flex-direction:column;gap:16px"></div>
  </div>`);
  main.replaceChildren(page);
  page.querySelector('#done').onclick = () => go('#/');
  page.querySelector('#more').onclick = () => go('#/convert');
  const host = page.querySelector('#results');
  for (const r of results) {
    if (!r.ok) {
      host.append(html(`<div class="card pad notice ${r.helicopter ? 'warn' : 'bad'}">${icon(r.helicopter ? 'helicopter' : 'warn')}
          <div><strong>${esc(r.craft_name || 'Craft')}</strong><div>${esc(r.error)}</div></div></div>`));
      continue;
    }
    const problems = r.report.filter(i => i.level === 'problem').length + r.flight.checks.filter(c => c.level === 'problem').length;
    const card = html(`<section class="card pad result-card" aria-label="${esc(r.plane.name)}">
        <div class="result-head">
          <div class="thumb"><img src="/api/planes/${r.plane.id}/thumb?v=${encodeURIComponent(r.plane.modified)}" alt="Top, side and front view of ${esc(r.plane.name)}"></div>
          <div style="flex:1;min-width:260px;display:flex;flex-direction:column;gap:8px">
            <h2>${esc(r.plane.name)}</h2>
            <div class="muted small">Saved as <code>${esc(r.plane.file_name)}</code> · built on ${esc(donorLabel(r.donor))}</div>
            ${r.replaced ? `<div class="muted small" data-replaced>It replaced the copy you converted before, "${esc(r.replaced.name)}", which moved to Deleted planes. <button type="button" class="btn small ghost" data-undo>Bring it back</button></div>` : ''}
            <div class="btn-row">${stabilityPill(r.flight)}${problems ? `<span class="pill bad">${problems} problem${problems === 1 ? '' : 's'} to fix</span>` : '<span class="pill good">Ready to fly</span>'}</div>
            ${flightGauges(r.flight, r.stats)}
          </div>
          <div class="btn-row"><button type="button" class="btn primary" data-open>${icon('build')}Open in builder</button></div>
        </div>
        <div></div>
        <details ${problems ? 'open' : ''}><summary style="cursor:pointer;font-weight:600">Flight check (${r.flight.checks.length})</summary>
          <ul class="checks" style="margin-top:8px">${r.flight.checks.map(c => `<li class="${c.level}">${icon(c.level === 'good' ? 'checkCircle' : c.level === 'note' ? 'info' : c.level === 'warning' ? 'warn' : 'xCircle')}<span>${esc(c.message)}</span></li>`).join('')}</ul>
        </details>
      </section>`);
    card.children[1].replaceWith(reportGroups(r.report));
    card.querySelector('[data-open]').onclick = () => go(`#/build/${r.plane.id}`);
    card.querySelector('[data-undo]')?.addEventListener('click', async () => {
      try {
        // Undo the replacement: the new conversion goes to Deleted planes first (it has the same plane id), then the old one comes back.
        await api.del(`/api/planes/${r.plane.id}`);
        const back = await api.post('/api/planes/restore', { token: r.replaced.token });
        card.querySelector('[data-replaced]').textContent = `The earlier copy is back in your planes as "${back.plane.file_name}"; this new conversion moved to Deleted planes.` + (back.plane.warning ? ' ' + back.plane.warning : '');
        card.querySelector('[data-open]')?.setAttribute('disabled', '');
      } catch (e) { showError(e); }
    });
    host.append(card);
  }
  main.scrollTop = 0;
}
