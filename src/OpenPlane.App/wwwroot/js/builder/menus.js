// Pop-up menus (right-click and the Add menu), the shortcut cheat sheet and the first-run tips.
import { esc, modal, html } from '../ui.js';
import { icon } from '../icons.js';

let open = null;

export function closeMenu() { if (open) { open.remove(); open = null; document.removeEventListener('pointerdown', onAway, true); document.removeEventListener('keydown', onMenuKey, true); } }
export const menuOpen = () => !!open;

function onAway(e) { if (open && !open.contains(e.target)) closeMenu(); }
function onMenuKey(e) {
  if (!open) return;
  const items = [...open.querySelectorAll('button:not(:disabled)')];
  const i = items.indexOf(document.activeElement);
  if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMenu(); }
  else if (e.key === 'ArrowDown') { e.preventDefault(); e.stopPropagation(); items[(i + 1) % items.length]?.focus(); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); e.stopPropagation(); items[(i - 1 + items.length) % items.length]?.focus(); }
  else if (e.key === 'ArrowRight' && document.activeElement?.dataset.sub) { e.preventDefault(); document.activeElement.click(); }
  else if (e.key === 'ArrowLeft' && open.dataset.parent) { e.preventDefault(); e.stopPropagation(); }
}

/**
 * Shows a menu at client (x, y). items: [{ label, icon?, keys?, run?, disabled?, sub?: items }, 'sep'].
 * Keyboard: arrows, Enter, Esc. A submenu replaces the menu in place (with a Back row), which also suits touch.
 */
export function showMenu(x, y, title, items, parent = null) {
  closeMenu();
  const el = html(`<div class="ctx-menu" role="menu" aria-label="${esc(title || 'Menu')}"></div>`);
  if (title) el.append(html(`<div class="ctx-title">${esc(title)}</div>`));
  if (parent) {
    const back = html(`<button type="button" role="menuitem">${icon('back')}<span>${esc(parent.title || 'Back')}</span></button>`);
    back.onclick = () => showMenu(x, y, parent.title, parent.items, parent.parent);
    el.append(back, html('<div class="ctx-sep"></div>'));
  }
  for (const it of items) {
    if (!it) continue;
    if (it === 'sep') { el.append(html('<div class="ctx-sep"></div>')); continue; }
    const b = html(`<button type="button" role="menuitem" ${it.disabled ? 'disabled' : ''} ${it.sub ? 'data-sub="1"' : ''} title="${esc(it.title || '')}">${it.icon ? icon(it.icon) : '<span class="i"></span>'}<span>${esc(it.label)}</span>${it.keys ? `<span class="k">${esc(it.keys)}</span>` : ''}${it.sub ? '<span class="k">▸</span>' : ''}</button>`);
    b.onclick = () => {
      if (it.sub) { showMenu(x, y, it.label, it.sub, { title, items, parent }); return; }
      closeMenu();
      it.run?.();
    };
    el.append(b);
  }
  document.body.append(el);
  const r = el.getBoundingClientRect();
  el.style.left = `${Math.max(4, Math.min(x, innerWidth - r.width - 4))}px`;
  el.style.top = `${Math.max(4, Math.min(y, innerHeight - r.height - 4))}px`;
  open = el;
  setTimeout(() => { document.addEventListener('pointerdown', onAway, true); document.addEventListener('keydown', onMenuKey, true); }, 0);
  el.querySelector('button:not(:disabled)')?.focus();
}

/** The "?" overlay: every command with its shortcut, grouped. commands: [{ group, label, keys }]. */
export function cheatSheet(commands) {
  const groups = new Map();
  for (const c of commands) {
    if (!c.keys || !c.group) continue;
    if (!groups.has(c.group)) groups.set(c.group, []);
    groups.get(c.group).push(c);
  }
  const kb = keys => keys.split(' / ').map(k => k.split('+').map(p => `<kbd>${esc(p)}</kbd>`).join('+')).join(' or ');
  return modal(close => {
    const el = html(`<div style="display:contents"><h2 tabindex="-1" autofocus style="outline:none">Keyboard and mouse</h2>
      <p class="muted small">Everything here also has a button: the tool shelf on the left, the header above the view, and the right-click menu. Hover any button to see its shortcut. The Steam Deck has no number pad, so every view has a header button too.</p>
      <div class="cheat-grid">
        <div><h3>Mouse</h3><dl>
          <dt>Select</dt><dd><kbd>Left click</kbd></dd>
          <dt>Add to selection</dt><dd><kbd>Shift</kbd>+<kbd>click</kbd></dd>
          <dt>Box select</dt><dd>drag on empty space</dd>
          <dt>Orbit</dt><dd><kbd>Middle drag</kbd> or <kbd>Alt</kbd>+<kbd>drag</kbd></dd>
          <dt>Pan</dt><dd><kbd>Shift</kbd>+<kbd>middle</kbd> or <kbd>right drag</kbd></dd>
          <dt>Zoom</dt><dd><kbd>Wheel</kbd></dd>
          <dt>Menu of actions</dt><dd><kbd>Right click</kbd></dd>
          <dt>Orbit, pan, zoom by mouse only</dt><dd>gizmo at top right</dd>
        </dl></div>
        <div><h3>Paint, cockpit and instruments</h3><dl>
          <dt>Plane paint, glass and light colours</dt><dd>click empty space, then Properties</dd>
          <dt>A part's colour</dt><dd>its Properties: Material</dd>
          <dt>Add an instrument or a light</dt><dd><kbd>Shift</kbd>+<kbd>A</kbd></dd>
          <dt>Look from the pilot's eye</dt><dd><kbd>Numpad 0</kbd> or Cockpit</dd>
          <dt>In cockpit view: look around</dt><dd><kbd>Middle drag</kbd></dd>
          <dt>In cockpit view: zoom</dt><dd><kbd>Wheel</kbd></dd>
          <dt>Leave cockpit view</dt><dd>Top, Front, Side or 3D</dd>
        </dl></div>
        ${[...groups].map(([g, list]) => `<div><h3>${esc(g)}</h3><dl>${list.map(c => `<dt>${esc(c.label)}</dt><dd>${kb(c.keys)}</dd>`).join('')}</dl></div>`).join('')}
      </div>
      <div class="btn-row"><button type="button" class="btn primary">Close</button></div></div>`);
    el.querySelector('.btn.primary').onclick = () => close();
    return el;
  }, { wide: true, label: 'Keyboard and mouse' }).then(() => {});
}

const TIPS_KEY = 'openplane-builder-tips-v2';

/** A small first-run card in the view; returns its element or null when it was dismissed before. */
export function firstRunTips(host, onShortcuts) {
  let seen = false;
  try { seen = localStorage.getItem(TIPS_KEY) === '1'; } catch { }
  if (seen) return null;
  const el = html(`<div class="tips" role="note">
      <h3>Building like a pro, the easy way</h3>
      <ul>
        <li><strong>Click</strong> a part to select it, <strong>Shift+click</strong> to add more, drag on empty space to box-select.</li>
        <li>Use the tools on the left (Move, Rotate, Scale) or press <kbd>G</kbd> <kbd>R</kbd> <kbd>S</kbd> and move the mouse. Type a number for an exact value.</li>
        <li>Press <kbd>Tab</kbd> (or the Edit button) to reshape a fuselage ring by ring, a shape vertex by vertex, or a wing by its corners.</li>
        <li><strong>Right-click</strong> anywhere for the actions that fit. Your plane's flight numbers update as you go.</li>
      </ul>
      <div class="btn-row"><button type="button" class="btn small" data-a="keys">${icon('help', 'sm')}All shortcuts</button><span class="spacer"></span><button type="button" class="btn primary small" data-a="ok">Got it</button></div>
    </div>`);
  const dismiss = () => { try { localStorage.setItem(TIPS_KEY, '1'); } catch { } el.remove(); };
  el.querySelector('[data-a=ok]').onclick = dismiss;
  el.querySelector('[data-a=keys]').onclick = () => { dismiss(); onShortcuts(); };
  host.append(el);
  return el;
}
