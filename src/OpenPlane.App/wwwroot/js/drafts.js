// Unsaved builder work: one draft per plane (keyed by the plane's id) or per new plane ("new-..."), kept on the server.
import { api } from './api.js';
import { html, esc, showError, confirmDialog, timeAgo } from './ui.js';
import { icon } from './icons.js';

/** A fresh draft id for a plane that hasn't been saved yet. */
export function newDraftId() {
  const r = crypto.getRandomValues(new Uint32Array(2));
  return `new-${Date.now().toString(36)}-${r[0].toString(36)}${r[1].toString(36)}`;
}

export async function listDrafts() {
  try { return (await api.get('/api/drafts')).drafts || []; } catch { return []; }
}

/**
 * Fills `host` with one line per unsaved draft ("Continue it" / "Discard"). Returns the drafts shown.
 * `go` navigates; `onChange` runs after a draft is discarded.
 */
export async function renderDrafts(host, go, onChange) {
  const drafts = await listDrafts();
  host.replaceChildren();
  if (!drafts.length) return drafts;
  const box = html(`<div class="notice note" style="flex-direction:column;align-items:stretch;gap:8px">
      <div style="display:flex;gap:8px;align-items:center">${icon('info')}<strong>${drafts.length === 1 ? 'You have unsaved work' : `You have ${drafts.length} unsaved planes`}</strong></div>
    </div>`);
  for (const d of drafts) {
    const row = html(`<div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
        <div style="flex:1;min-width:200px"><strong>${esc(d.name || 'Untitled plane')}</strong>
          <span class="muted small">${d.plane_name ? `changes to "${esc(d.plane_name)}"` : 'a new plane, not saved yet'} · ${esc(timeAgo(d.saved_at))}</span></div>
        <button type="button" class="btn small primary" data-a="go">Continue it</button>
        <button type="button" class="btn small ghost" data-a="drop">Discard</button>
      </div>`);
    row.querySelector('[data-a=go]').onclick = () => go(`#/build/draft/${encodeURIComponent(d.id)}`);
    row.querySelector('[data-a=drop]').onclick = async () => {
      const what = d.plane_name ? `Your unsaved changes to "${esc(d.plane_name)}" will be lost; the saved plane stays as it is.` : `"${esc(d.name || 'Untitled plane')}" was never saved, so it will be lost.`;
      if (!(await confirmDialog({ title: 'Discard the unsaved work?', body: what, confirm: 'Discard', danger: true }))) return;
      try { await api.del(`/api/drafts/${encodeURIComponent(d.id)}`); } catch (e) { showError(e); return; }
      row.remove();
      if (!box.querySelector('[data-a=go]')) box.remove();
      onChange?.();
    };
    box.append(row);
  }
  host.append(box);
  return drafts;
}
