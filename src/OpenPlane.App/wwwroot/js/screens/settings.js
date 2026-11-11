// Settings: folders, installing the mod, Steam Deck help, appearance, about.
import { api, getStatus } from '../api.js';
import { html, esc, modal, showError, toast, pickFolder, copyText, openLink, confirmDialog } from '../ui.js';
import { icon } from '../icons.js';
import { applyTheme, refreshHeader } from '../app.js';

export async function render(main, args, query) {
  const page = html(`<div class="page">
    <div class="page-head"><div><h1>Settings</h1><p>Where OpenPlane finds things, and the mod that lets Nuclear Option load your planes.</p></div></div>
    <div class="settings-grid">
      <section class="card pad" id="paths-card" aria-labelledby="paths-h"><div class="section-title"><h2 id="paths-h">Game and folders</h2></div><div id="paths"></div></section>
      <section class="card pad" aria-labelledby="mod-h"><div class="section-title"><h2 id="mod-h">OpenPlane mod</h2></div><div id="mod"></div></section>
      <section class="card pad" id="multiplayer" aria-labelledby="mp-h"><div class="section-title"><h2 id="mp-h">Multiplayer: games you host</h2></div><div id="mp"></div></section>
      <section class="card pad" aria-labelledby="deck-h"><div class="section-title"><h2 id="deck-h">Steam Deck and Linux</h2></div><div id="deck"></div></section>
      <section class="card pad" aria-labelledby="you-h"><div class="section-title"><h2 id="you-h">You and appearance</h2></div><div id="you"></div></section>
      <section class="card pad" id="help" aria-labelledby="help-h"><div class="section-title"><h2 id="help-h">Help and problems</h2></div><div id="help-body"></div></section>
      <section class="card pad" aria-labelledby="about-h"><div class="section-title"><h2 id="about-h">About OpenPlane</h2></div><div id="about"></div></section>
    </div>
  </div>`);
  main.append(page);
  const ctx = { page };
  await Promise.all([drawPaths(ctx), drawMod(ctx), drawMultiplayer(ctx)]);
  drawDeck(ctx);
  await drawYou(ctx);
  drawHelp(ctx);
  drawAbout(ctx);
  if (query.get('focus') === 'game') page.querySelector('#game-dir')?.focus();
  if (query.get('focus') === 'mod') page.querySelector('#mod')?.scrollIntoView();
  if (query.get('section') === 'multiplayer') page.querySelector('#multiplayer')?.scrollIntoView({ block: 'start' });
  return { title: 'Settings' };
}

// ---------------------------------------------------------------- multiplayer (the mod's [Multiplayer] settings)

async function drawMultiplayer(ctx) {
  const host = ctx.page.querySelector('#mp');
  let r;
  try { r = await api.get('/api/host-rules'); } catch (e) { host.innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; return; }
  const el = html(`<div style="display:flex;flex-direction:column;gap:12px">
      <p class="muted">These apply to games this computer runs: single-player, a lobby you host, or a dedicated server running
         from this game folder. Planes outside them are hidden in every hangar for everyone in your game; other hosts use their
         own settings. Everyone still needs the same planes to share a lobby.</p>
      ${r.available ? '' : `<div class="notice warn">${icon('warn')}<div>${esc(r.note || 'Install the OpenPlane mod first.')}</div></div>`}
      <label class="check"><input type="checkbox" id="mp-allow" ${r.allow ? 'checked' : ''} ${r.available ? '' : 'disabled'}><span>Let players fly OpenPlane planes in games I host</span></label>
      <div class="mp-limits">
        <label class="field"><span class="label">Most thrust for its weight</span>
          <input type="number" id="mp-tw" min="0" max="10" step="0.05" value="${r.max_thrust_to_weight || ''}" placeholder="No limit" ${r.available ? '' : 'disabled'}>
          <span class="hint">Thrust over weight with full fuel and no weapons, as the builder shows it. Stock fighters are about 0.8 to 1.2. Empty = no limit.</span></label>
        <label class="field"><span class="label">Most weapon hardpoints</span>
          <input type="number" id="mp-hp" min="0" max="64" step="1" value="${r.max_hardpoints || ''}" placeholder="No limit" ${r.available ? '' : 'disabled'}>
          <span class="hint">Pylons a player can hang bombs and missiles on (guns don't count). Empty = no limit.</span></label>
      </div>
      <p class="faint small">A plane's cost is its stock plane's, paid from the player's budget like any other, so missions keep controlling cost as usual.</p>
      <div class="btn-row"><button type="button" class="btn primary" id="mp-save" disabled>Save</button><span class="muted small" id="mp-msg"></span></div>
    </div>`);
  host.replaceChildren(el);
  const save = el.querySelector('#mp-save');
  el.querySelectorAll('input').forEach(i => i.addEventListener('input', () => { save.disabled = !r.available; el.querySelector('#mp-msg').textContent = ''; }));
  el.querySelector('#mp-allow').addEventListener('change', () => { save.disabled = !r.available; });
  save.onclick = async () => {
    const num = (v, int) => { const n = int ? parseInt(v, 10) : parseFloat(v); return isFinite(n) && n > 0 ? n : 0; };
    try {
      save.disabled = true;
      r = await api.put('/api/host-rules', {
        allow: el.querySelector('#mp-allow').checked,
        max_thrust_to_weight: num(el.querySelector('#mp-tw').value, false),
        max_hardpoints: num(el.querySelector('#mp-hp').value, true),
      });
      el.querySelector('#mp-msg').textContent = r.note || 'Saved.';
    } catch (e) { save.disabled = false; showError(e); }
  };
}

// ---------------------------------------------------------------- paths

async function drawPaths(ctx) {
  const host = ctx.page.querySelector('#paths');
  let view;
  try { view = await api.get('/api/settings'); } catch (e) { host.innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; return; }
  const s = view.settings, eff = view.effective, ov = view.overridden;
  const src = r => r.source === 'command line' ? 'Set on the command line for this session' : r.source === 'settings' ? 'Your choice' : r.source === 'detected' ? 'Found automatically' : r.source === 'default' ? "Nuclear Option's user folder (default)" : 'Not found';
  const el = html(`<div style="display:flex;flex-direction:column;gap:16px">
      <div class="path-row">
        <label class="field"><span class="label">Nuclear Option folder</span>
          <input type="text" id="game-dir" value="${esc(s.game_dir || '')}" placeholder="${esc(eff.game.path || 'Not found: choose the folder that holds NuclearOption.exe')}" ${ov.game ? 'disabled' : ''}>
          <span class="path-source">${esc(src(eff.game))}${eff.game.path ? `: ${esc(eff.game.path)}` : ''}</span></label>
        <button type="button" class="btn" data-browse="game-dir" ${ov.game ? 'disabled' : ''}>${icon('folder')}Browse…</button>
        <button type="button" class="btn" id="detect" ${ov.game ? 'disabled' : ''}>${icon('search')}Find automatically</button>
      </div>
      <div class="path-row">
        <label class="field"><span class="label">Planes folder (the mod loads every plane in it)</span>
          <input type="text" id="planes-dir" value="${esc(s.planes_dir || '')}" placeholder="${esc(eff.planes.path || '')}" ${ov.planes ? 'disabled' : ''}>
          <span class="path-source">${esc(src(eff.planes))}${eff.planes.path ? `: ${esc(eff.planes.path)}` : ''}. Leave empty for the default.
            The mod always loads Nuclear Option's own planes folder; for a folder you choose, OpenPlane adds it to the mod's settings (<code>BepInEx/config/com.meta.openplane.cfg</code>) when you save or install.</span></label>
        <button type="button" class="btn" data-browse="planes-dir" ${ov.planes ? 'disabled' : ''}>${icon('folder')}Browse…</button>
        <button type="button" class="btn ghost" id="open-planes">${icon('folder')}Open</button>
      </div>
      <div class="field"><span class="label">Flyout craft folders</span>
        <div id="flyout-dirs" style="display:flex;flex-direction:column;gap:6px"></div>
        <div class="btn-row"><button type="button" class="btn small" id="add-flyout" ${ov.flyout ? 'disabled' : ''}>${icon('plus', 'sm')}Add a folder…</button>
        ${ov.flyout ? '<span class="path-source">Set on the command line for this session.</span>' : ''}</div>
      </div>
      <div class="btn-row"><button type="button" class="btn primary" id="save-paths" disabled>Save changes</button><span class="muted small" id="paths-msg"></span></div>
      <p class="faint small">Settings file: <code>${esc(view.settings_file)}</code></p>
    </div>`);
  host.replaceChildren(el);
  const dirty = () => { el.querySelector('#save-paths').disabled = false; };
  el.querySelectorAll('input').forEach(i => i.addEventListener('input', dirty));

  const flyHost = el.querySelector('#flyout-dirs');
  let flyoutDirs = [...s.flyout_dirs];
  const hidden = [...(s.hidden_flyout_dirs || [])];
  const drawFly = () => {
    flyHost.replaceChildren(...eff.flyout.filter(f => f.source !== 'imported').map(f => {
      const row = html(`<div class="path-row" style="align-items:center"><span class="dot ${f.exists ? 'good' : ''}"></span><code style="flex:1">${esc(f.path)}</code>
          <span class="path-source">${esc(f.source === 'settings' ? 'added by you' : f.source)}${f.exists ? '' : ' · not found'}</span>
          ${f.source === 'command line' ? '' : `<button type="button" class="btn small ghost" aria-label="Remove ${esc(f.path)}">${icon('x', 'sm')}Remove</button>`}</div>`);
      row.querySelector('button')?.addEventListener('click', () => {
        if (f.source === 'settings') flyoutDirs = flyoutDirs.filter(p => p !== f.path); else hidden.push(f.path);
        eff.flyout = eff.flyout.filter(x => x !== f);
        drawFly(); dirty();
      });
      return row;
    }));
    if (!flyHost.children.length) flyHost.append(html(`<span class="muted small">No Flyout folders yet. Flyout's own folder is added automatically when Flyout is installed.</span>`));
  };
  drawFly();

  el.querySelectorAll('[data-browse]').forEach(b => b.onclick = async () => {
    const input = el.querySelector('#' + b.dataset.browse);
    const p = await pickFolder(b.dataset.browse === 'game-dir' ? 'Choose the Nuclear Option folder' : 'Choose the planes folder', input.value || input.placeholder);
    if (p) { input.value = p; dirty(); }
  });
  el.querySelector('#detect').onclick = async () => {
    try {
      const r = await api.post('/api/settings/detect');
      if (r.game_dir) { el.querySelector('#game-dir').value = r.game_dir; dirty(); }
      toast(r.message, { kind: r.game_dir ? 'good' : 'info' });
    } catch (e) { showError(e); }
  };
  el.querySelector('#open-planes').onclick = () => api.post('/api/reveal-path', { path: eff.planes.path }).catch(showError);
  el.querySelector('#add-flyout').onclick = async () => {
    const p = await pickFolder('Choose a folder with Flyout craft');
    if (!p) return;
    flyoutDirs.push(p);
    eff.flyout.push({ path: p, source: 'settings', exists: true });
    drawFly(); dirty();
  };
  el.querySelector('#save-paths').onclick = async () => {
    try {
      const cur = (await api.get('/api/settings')).settings;
      const saved = await api.put('/api/settings', {
        ...cur,
        game_dir: ov.game ? cur.game_dir : el.querySelector('#game-dir').value.trim() || null,
        planes_dir: ov.planes ? cur.planes_dir : el.querySelector('#planes-dir').value.trim() || null,
        flyout_dirs: flyoutDirs, hidden_flyout_dirs: hidden,
      });
      toast('Settings saved.', { kind: 'good' });
      if (saved.mod_config_note) toast(saved.mod_config_note, { kind: 'info', timeout: 12000 });
      await drawPaths(ctx);
      await drawMod(ctx);
      refreshHeader(true);
    } catch (e) { showError(e); }
  };
}

// ---------------------------------------------------------------- mod

async function drawMod(ctx) {
  const host = ctx.page.querySelector('#mod');
  let st;
  try { st = await getStatus(true); } catch (e) { host.innerHTML = `<div class="notice bad">${icon('warn')}<div>${esc(e.message)}</div></div>`; return; }
  const m = st.mod;
  const row = (ok, title, text) => `<div class="mod-row"><span class="dot ${ok === null ? '' : ok ? 'good' : 'warn'}"></span><div class="text"><strong>${title}</strong><span>${text}</span></div></div>`;
  const installed = m.state === 'ready' || m.state === 'needs-update';
  const el = html(`<div style="display:flex;flex-direction:column;gap:14px">
      ${!m.game_found ? `<div class="notice warn">${icon('warn')}<div>${esc(m.summary)}</div></div>` : ''}
      ${m.state === 'no-bundle' ? `<div class="notice bad">${icon('warn')}<div>This copy of OpenPlane doesn't include the mod files, so it can't install the mod. Download OpenPlane from its release page (the release includes them), or build the mod from source as the README explains.</div></div>` : ''}
      ${m.nomm_detected ? `<div class="notice note">${icon('info')}<div><strong>NOMM manages this game's mods.</strong> OpenPlane won't install BepInEx or a second Blueprinter over it. NOMM will list OpenPlane as a mod it didn't install; that's fine.${m.disabled_in_nomm ? ' OpenPlane is turned off in NOMM right now; turn it on there.' : ''}</div></div>` : ''}
      <div class="mod-rows">
        ${row(m.game_found ? m.bepinex_installed : null, 'BepInEx 5', m.bepinex_installed ? `Installed${m.bepinex_version ? ` (${esc(m.bepinex_version)})` : ''}. It loads mods into the game.`
          : m.bepinex_incomplete ? `Incomplete: ${esc((m.bepinex_missing || []).map(f => f.split(/[\\/]/).pop()).join(', '))} missing, so the game won't load mods. Installing puts the missing files back.`
          : 'Not installed. OpenPlane downloads it from its official GitHub release when you install.')}
        ${row(m.game_found ? m.blueprinter_installed && !m.blueprinter_too_old : null, 'Blueprinter by nikkorap', m.blueprinter_installed ? `${m.blueprinter_installed_by_others ? 'Installed (by you or NOMM)' : 'Installed by OpenPlane'}${m.blueprinter_version ? `, version ${esc(m.blueprinter_version)}` : ''}.${m.blueprinter_too_old ? ' OpenPlane needs 2.1.1 or newer: update it.' : ''}`
          : m.blueprinter_disabled_in_nomm ? 'Turned off in NOMM. Turn it back on there; OpenPlane planes need it.' : 'Not installed. It comes with OpenPlane; it lets the game load new planes.')}
        ${row(m.game_found ? m.mod_installed && !m.update_available : null, 'OpenPlane mod', m.mod_installed ? (m.update_available ? 'Installed, and a newer version is ready.' : `Installed (version ${esc(m.mod_version || '?')}).`) : 'Not installed.')}
      </div>
      <div class="btn-row">
        <button type="button" class="btn primary" id="install" ${m.game_found && m.bundle_available && !m.game_running ? '' : 'disabled'}>${icon('import')}${installed ? (m.update_available ? 'Update OpenPlane' : 'Reinstall OpenPlane') : 'Install OpenPlane into Nuclear Option'}</button>
        <button type="button" class="btn danger" id="remove" ${m.game_found && (m.mod_installed || m.blueprinter_installed) ? '' : 'disabled'}>${icon('trash')}Remove OpenPlane</button>
        <button type="button" class="btn ghost" id="mod-refresh">${icon('refresh')}Check again</button>
      </div>
      ${m.game_running ? `<div class="notice warn">${icon('warn')}<div>Nuclear Option is running. Close it before installing or removing mods.</div></div>` : ''}
      <p class="muted small">While Blueprinter is installed, Nuclear Option only shows multiplayer lobbies that use the same mods. Remove OpenPlane here to play unmodded multiplayer again.</p>
      <div id="job-host"></div>
    </div>`);
  host.replaceChildren(el);
  el.querySelector('#mod-refresh').onclick = () => drawMod(ctx);
  el.querySelector('#install').onclick = async () => { await installModFlow(); drawMod(ctx); refreshHeader(true); };
  el.querySelector('#remove').onclick = async () => {
    const opts = await modal(close => {
      const d = html(`<div style="display:contents">
          <h2>Remove OpenPlane from Nuclear Option?</h2>
          <p class="muted">OpenPlane deletes only its own two folders in <code>BepInEx/plugins</code>. Your planes stay in the planes folder, and BepInEx stays installed.</p>
          ${m.blueprinter_installed_by_others ? '' : `<label class="check"><input type="checkbox" id="rm-bp" checked>Also remove Blueprinter (keep it if other plane mods need it)</label>`}
          <div class="btn-row"><button type="button" class="btn" data-a="no">Cancel</button><button type="button" class="btn danger solid" data-a="yes">Remove</button></div>
        </div>`);
      d.querySelector('[data-a=no]').onclick = () => close(null);
      d.querySelector('[data-a=yes]').onclick = () => close({ remove_blueprinter: d.querySelector('#rm-bp')?.checked ?? false });
      return d;
    }, { label: 'Remove OpenPlane' });
    if (!opts) return;
    try {
      const job = await api.post('/api/mod/remove', opts);
      const final = await followJob(job, el.querySelector('#job-host'));
      await drawMod(ctx);
      await followJob(final, ctx.page.querySelector('#job-host'));
    } catch (e) { showError(e); await drawMod(ctx); }
    refreshHeader(true);
  };
}

/** Explains what installing does, then runs it with live progress. Resolves when finished or cancelled. */
export async function installModFlow() {
  const st = await getStatus(true).catch(() => null);
  if (!st) return;
  const m = st.mod;
  if (!m.game_found) { toast(m.summary, { kind: 'bad', timeout: 8000 }); return; }
  if (!m.bundle_available) { toast("This copy of OpenPlane doesn't include the mod files. Download OpenPlane from its release page.", { kind: 'bad', timeout: 9000 }); return; }
  const steps = [];
  if (m.disabled_in_nomm) { toast('OpenPlane is turned off in NOMM. Turn it back on in NOMM instead of installing a second copy.', { kind: 'bad', timeout: 10000 }); return; }
  if (m.bepinex_incomplete) {
    steps.push(`Repair <strong>BepInEx</strong>: put back the missing ${esc((m.bepinex_missing || []).map(f => f.split(/[\\/]/).pop()).join(', '))} from the official BepInEx 5.4.23.4 release (files already there are kept).`);
  } else if (!m.bepinex_installed) {
    steps.push(m.game_exe_found
      ? 'Download <strong>BepInEx 5.4.23.4</strong> (the mod loader, about 625 KB) from its official GitHub release, check it, and unpack it into the game folder.'
      : "BepInEx isn't installed, but <code>NuclearOption.exe</code> isn't in this folder, so OpenPlane won't download it here. The other files are still copied.");
  }
  if (!m.blueprinter_installed) steps.push('Copy <strong>Blueprinter 2.1.1</strong> by nikkorap (MIT licence) into <code>BepInEx/plugins/Blueprinter</code>.');
  steps.push(`Copy the <strong>OpenPlane mod</strong> into <code>BepInEx/plugins/OpenPlane</code>.`);
  steps.push('Make sure the planes folder exists.');
  await modal(close => {
    const d = html(`<div style="display:contents">
        <h2>Install OpenPlane into Nuclear Option</h2>
        <p class="muted">Game folder: <code>${esc(m.game_dir)}</code></p>
        <p>OpenPlane will:</p>
        <ol class="steps">${steps.map(s => `<li>${s}</li>`).join('')}</ol>
        ${m.nomm_detected ? `<div class="notice note">${icon('info')}<div>NOMM manages this game's mods. OpenPlane won't touch BepInEx or NOMM's mods.</div></div>` : ''}
        <p class="muted small" id="lobby-note">While Blueprinter is installed, the game only shows multiplayer lobbies that use the same mods. You can remove OpenPlane in Settings at any time.</p>
        <div id="job"></div>
        <div class="btn-row"><button type="button" class="btn" data-a="close">Cancel</button><button type="button" class="btn primary" data-a="go">${icon('import')}Install</button></div>
      </div>`);
    const go = d.querySelector('[data-a=go]'), cancel = d.querySelector('[data-a=close]');
    cancel.onclick = () => close();
    go.onclick = async () => {
      go.disabled = true; cancel.disabled = true;
      d.querySelector('#lobby-note').hidden = true;   // the result lists it again with the next steps
      try {
        const job = await api.post('/api/mod/install');
        const final = await followJob(job, d.querySelector('#job'));
        go.hidden = true;
        cancel.disabled = false;
        cancel.textContent = 'Close';
        cancel.classList.add('primary');
        cancel.focus();
        if (final.state === 'done') toast(final.message, { kind: final.partial ? 'info' : 'good', timeout: 8000 });
      } catch (e) {
        showError(e);
        go.disabled = false; cancel.disabled = false;
      }
    };
    return d;
  }, { label: 'Install OpenPlane', wide: true });
}

/** Polls a job and draws its steps; resolves with the final snapshot. */
export async function followJob(job, host) {
  const draw = j => {
    host.replaceChildren(html(`<div class="job" aria-live="polite">
        <ol>${j.steps.map(s => `<li class="${s.state}">${icon(s.state === 'done' ? 'checkCircle' : s.state === 'failed' ? 'xCircle' : s.state === 'skipped' ? 'minus' : 'spinner')}
            <span>${esc(s.text)}${s.detail ? `<span class="detail">${esc(s.detail)}</span>` : ''}</span></li>`).join('')}</ol>
        ${j.progress !== null && j.progress !== undefined && j.state === 'running' ? `<div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(j.progress * 100)}"><div style="width:${Math.round(j.progress * 100)}%"></div></div>` : ''}
        ${j.state === 'failed' ? `<div class="notice bad">${icon('warn')}<div>${esc(j.error)}</div></div>` : ''}
        ${j.state === 'done' ? `<div class="notice ${j.partial ? 'warn' : 'good'}">${icon(j.partial ? 'warn' : 'checkCircle')}<div>${esc(j.message)}</div></div>` : ''}
        ${j.next_steps?.length && j.state !== 'running' ? `<ul class="about-list">${j.next_steps.map(n => `<li>${esc(n)}</li>`).join('')}</ul>` : ''}
      </div>`));
  };
  let j = job;
  draw(j);
  while (j.state === 'running') {
    await new Promise(r => setTimeout(r, 300));
    j = await api.get(`/api/jobs/${job.id}`);
    draw(j);
  }
  return j;
}

// ---------------------------------------------------------------- Steam Deck

async function drawDeck(ctx) {
  const st = await getStatus().catch(() => null);
  const linux = st?.platform === 'linux';
  const host = ctx.page.querySelector('#deck');
  const el = html(`<div style="display:flex;flex-direction:column;gap:12px">
      <p class="muted">${linux ? 'You are on Linux or a Steam Deck.' : 'Only needed when you play on a Steam Deck or Linux.'}
        Nuclear Option runs through Proton there, and BepInEx needs one launch option so Proton loads it. Do this once:</p>
      <ol class="steps">
        <li>In Steam, right-click Nuclear Option and choose <strong>Properties</strong>.</li>
        <li>Paste this into <strong>Launch Options</strong>:</li>
      </ol>
      <div class="copy-box"><code id="lo">${esc(st?.mod?.launch_option || 'WINEDLLOVERRIDES="winhttp=n,b" %command%')}</code><button type="button" class="btn" id="copy">${icon('copy')}Copy</button></div>
      <p class="muted small">Under Proton the game's planes folder is inside its Proton folder:
        <code>~/.local/share/Steam/steamapps/compatdata/2168680/pfx/drive_c/users/steamuser/AppData/LocalLow/Shockfront/NuclearOption/OpenPlane/Aircraft</code>.
        The OpenPlane app is made for Windows; running it on Linux is untested, so you may need to copy the mod and your planes there yourself.</p>
    </div>`);
  host.replaceChildren(el);
  el.querySelector('#copy').onclick = async () => {
    const ok = await copyText(el.querySelector('#lo').textContent);
    toast(ok ? 'Launch option copied. Paste it in Steam.' : "Couldn't copy. Select the text and copy it yourself.", { kind: ok ? 'good' : 'bad' });
  };
}

// ---------------------------------------------------------------- you and appearance

async function drawYou(ctx) {
  const st = await getStatus(true).catch(() => null);
  const s = st?.settings || {};
  const host = ctx.page.querySelector('#you');
  const el = html(`<div style="display:flex;flex-direction:column;gap:14px">
      <label class="field" style="max-width:420px"><span class="label">Your name</span>
        <input type="text" id="your-name" value="${esc(s.your_name || '')}" placeholder="As players know you">
        <span class="hint">Used when you say "I did" for who built a plane, and as the author of planes you build.</span></label>
      <fieldset class="field" style="border:0;padding:0;margin:0"><legend class="label" style="margin-bottom:6px">Test fly starts</legend>
        <div class="btn-row">
          ${[['air', 'In the air, over an airbase'], ['hangar', 'In a hangar, ready to taxi']].map(([t, label]) => `<label class="check"><input type="radio" name="tfstart" value="${t}" ${(s.test_fly_start || 'air') === t ? 'checked' : ''}>${label}</label>`).join('')}
        </div>
        <span class="hint">Test fly opens Nuclear Option straight into Free Flight on Heartland with your plane. A plane no airbase there offers (runway too short) starts in the air.</span>
      </fieldset>
      <fieldset class="field" style="border:0;padding:0;margin:0"><legend class="label" style="margin-bottom:6px">Theme</legend>
        <div class="btn-row">
          ${[['dark', 'Dark'], ['light', 'Bone (light)'], ['system', 'Follow the system']].map(([t, label]) => `<label class="check"><input type="radio" name="theme" value="${t}" ${(s.theme || 'dark') === t ? 'checked' : ''}>${label}</label>`).join('')}
        </div>
      </fieldset>
      <label class="check"><input type="checkbox" id="show-gs" ${s.getting_started_hidden ? '' : 'checked'}><span>Show "Getting started" on My planes (until every step is done)</span></label>
      <div class="btn-row"><button type="button" class="btn primary" id="save-you">Save</button></div>
      <div id="assoc"></div>
    </div>`);
  host.replaceChildren(el);
  drawAssociation(el.querySelector('#assoc'));
  el.querySelectorAll('input[name=theme]').forEach(r => r.onchange = () => applyTheme(r.value));
  el.querySelector('#save-you').onclick = async () => {
    try {
      const cur = (await api.get('/api/settings')).settings;
      const theme = el.querySelector('input[name=theme]:checked').value;
      const tfs = el.querySelector('input[name=tfstart]:checked')?.value || 'air';
      await api.put('/api/settings', { ...cur, your_name: el.querySelector('#your-name').value.trim() || null, theme, test_fly_start: tfs, getting_started_hidden: !el.querySelector('#show-gs').checked });
      applyTheme(theme);
      toast('Saved.', { kind: 'good' });
    } catch (e) { showError(e); }
  };
}

/** Windows: "Open .oplane files with OpenPlane" (double-click a shared plane to import it). Only for the installed app. */
async function drawAssociation(host) {
  let a;
  try { a = await api.get('/api/file-association'); } catch { return; }
  if (!a.supported) return;
  const draw = () => {
    host.replaceChildren(html(`<div class="field" style="border-top:1px solid var(--line);padding-top:14px">
        <span class="label">Double-clicking a .oplane file</span>
        <label class="check"><input type="checkbox" id="assoc-on" ${a.on ? 'checked' : ''} ${a.exe ? '' : 'disabled'}><span>Opens it in OpenPlane (and imports it)</span></label>
        <span class="hint">${a.exe ? (a.other_app && !a.on ? 'Another program opens .oplane files now; ticking this makes OpenPlane open them instead. ' : '') + 'Set up for your Windows account only; untick to undo it.'
          : 'Available when OpenPlane runs from OpenPlane.exe.'}</span>
      </div>`));
    host.querySelector('#assoc-on').onchange = async e => {
      try { a = await api.post('/api/file-association', { on: e.target.checked }); toast(a.on ? 'Double-clicking a .oplane file now opens it in OpenPlane.' : 'OpenPlane no longer opens .oplane files on double-click.', { kind: 'good' }); }
      catch (err) { showError(err); }
      draw();
    };
  };
  draw();
}

// ---------------------------------------------------------------- help and problems

const TROUBLE = [
  ['My plane isn\'t in the game', 'Nuclear Option loads planes when it starts: restart it after saving. Each plane is offered only at airbases that offer the stock plane it is built on and have a long enough runway (My planes shows where it isn\'t offered). Test fly skips all that and puts you in the plane.'],
  ['The game shows no OpenPlane planes at all', 'Check the mod line above says "Installed", and that Nuclear Option was started after installing. On Steam Deck and Linux, add the launch option in "Steam Deck and Linux". Copy a problem report below: its mod log lines say what went wrong.'],
  ['I can\'t see my friends\' lobby', 'Everyone needs exactly the same OpenPlane planes (and the same mods). On Share, "Playing together" sends all your planes as one pack; your friends import it and restart.'],
  ['Windows says it protected my PC', 'OpenPlane isn\'t code signed yet. Choose More info, then Run anyway. You can check your download against SHA256SUMS.txt from the release page.'],
  ['The window stays blank', 'OpenPlane draws its window with Microsoft Edge WebView2. Install the WebView2 Runtime from Microsoft, or run OpenPlane.exe --serve and open the address it prints in your browser.'],
];

function drawHelp(ctx) {
  const host = ctx.page.querySelector('#help-body');
  const el = html(`<div style="display:flex;flex-direction:column;gap:12px">
      <div class="faq">${TROUBLE.map(([q, a]) => `<details><summary>${esc(q)}</summary><p class="muted">${esc(a)}</p></details>`).join('')}</div>
      <p class="muted">Something else wrong? Copy a problem report and paste it into a new issue. It lists versions, what OpenPlane found, what the
         game did with each plane and the mod's log lines; your Windows user name and folders are replaced, so it's safe to post.</p>
      <div class="btn-row">
        <button type="button" class="btn primary" id="copy-report">${icon('copy')}Copy a problem report</button>
        <button type="button" class="btn" id="open-log">${icon('folder')}Show the game's mod log</button>
        <button type="button" class="btn" id="report-issue">${icon('link')}Report a problem on GitHub</button>
      </div>
    </div>`);
  host.replaceChildren(el);
  el.querySelector('#copy-report').onclick = async () => {
    try {
      const r = await api.get('/api/problem-report');
      if (await copyText(r.text)) toast('Problem report copied. Paste it into your report.', { kind: 'good' });
      else await modal(close => {
        const d = html(`<div style="display:contents"><h2>Problem report</h2><p class="muted">Copy this text into your report.</p>
            <textarea readonly rows="16" style="width:100%;font-family:var(--mono);font-size:12px">${esc(r.text)}</textarea>
            <div class="btn-row"><button type="button" class="btn primary" data-a="close">Close</button></div></div>`);
        d.querySelector('[data-a=close]').onclick = () => close();
        return d;
      }, { wide: true, label: 'Problem report' });
    } catch (e) { showError(e); }
  };
  el.querySelector('#open-log').onclick = async () => {
    const st = await getStatus(true).catch(() => null);
    const game = st?.paths?.game?.path;
    if (!game) { toast('Nuclear Option wasn\'t found, so there is no mod log yet.', { kind: 'info' }); return; }
    const sep = game.includes('/') && !game.includes('\\') ? '/' : '\\';
    api.post('/api/reveal-path', { path: `${game}${sep}BepInEx${sep}LogOutput.log` }).catch(e => showError(e));
  };
  el.querySelector('#report-issue').onclick = () => openLink('https://github.com/metasprojects/OpenPlane/issues');
}

// ---------------------------------------------------------------- about

async function drawAbout(ctx) {
  const st = await getStatus().catch(() => null);
  const host = ctx.page.querySelector('#about');
  const el = html(`<div style="display:flex;flex-direction:column;gap:12px">
      <p><strong>OpenPlane ${esc(st?.version || '')}</strong>: convert Flyout planes, build your own, and share them, for Nuclear Option.</p>
      <p class="muted">Free and open source under the MIT licence. Made by Meta. Built with AI assistance.</p>
      <label class="check"><input type="checkbox" id="check-updates" ${st?.settings?.check_for_updates === false ? '' : 'checked'}><span>Check for a newer OpenPlane when it starts <span class="faint small">(asks GitHub; nothing about you is sent)</span></span></label>
      <ul class="about-list">
        <li><button type="button" class="linklike" data-link="https://github.com/nikkorap/NOBlueprinter-Releases">Blueprinter</button> by nikkorap (MIT): loads new planes into Nuclear Option.</li>
        <li><button type="button" class="linklike" data-link="https://threejs.org/">three.js</button> (MIT): the 3D view in the builder.</li>
        <li><button type="button" class="linklike" data-link="https://github.com/BepInEx/BepInEx">BepInEx</button> (LGPL-2.1): the mod loader, downloaded from its official release when you install.</li>
        <li><button type="button" class="linklike" data-link="https://github.com/tryphotino/photino.NET">Photino</button> (Apache-2.0): the app window, shown by Microsoft's WebView2 (its loader is under the WebView2 SDK licence).</li>
        <li>Newtonsoft.Json by James Newton-King (MIT), and the .NET and ASP.NET Core runtimes (MIT) the app runs on.</li>
      </ul>
      <p class="muted small">The licence texts are in the <code>licenses</code> folder next to OpenPlane.exe.</p>
      <p class="muted small">OpenPlane isn't affiliated with or endorsed by Shockfront Studios (Nuclear Option) or Stonext Games (Flyout).
        Nuclear Option is a trademark of its owners. OpenPlane ships no game files. Converted Flyout planes belong to the people who built them.</p>
      <p class="faint small">Data folder: <code>${esc(st?.paths?.data_dir || '')}</code></p>
    </div>`);
  host.replaceChildren(el);
  el.querySelectorAll('[data-link]').forEach(b => b.onclick = () => openLink(b.dataset.link));
  el.querySelector('#check-updates').onchange = async e => {
    try { const cur = (await api.get('/api/settings')).settings; await api.put('/api/settings', { ...cur, check_for_updates: e.target.checked }); toast(e.target.checked ? 'OpenPlane will check for updates when it starts.' : "OpenPlane won't check for updates.", { kind: 'good' }); }
    catch (err) { showError(err); }
  };
}
