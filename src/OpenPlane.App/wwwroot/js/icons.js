// Small stroke icons (24x24 viewBox), drawn for OpenPlane.
const P = {
  plane: '<path d="M12 2.5c.9 0 1.5 1.2 1.5 2.8V9l8 4.5v2l-8-2.2v4.3l2.6 2v1.7L12 20.4l-4.1.9v-1.7l2.6-2v-4.3l-8 2.2v-2L10.5 9V5.3c0-1.6.6-2.8 1.5-2.8z"/>',
  convert: '<path d="M4 7h13l-3-3M20 17H7l3 3"/>',
  build: '<path d="M14.7 6.3a4 4 0 0 0-5.4 5.2L3.5 17.3a1.8 1.8 0 0 0 2.6 2.6l5.8-5.8a4 4 0 0 0 5.2-5.4l-2.5 2.5-2.4-.6-.6-2.4 2.5-2.5z"/>',
  share: '<circle cx="18" cy="5" r="2.5"/><circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="19" r="2.5"/><path d="M8.2 10.8l7.6-4.4M8.2 13.2l7.6 4.4"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  import: '<path d="M12 3v12M7 10l5 5 5-5M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>',
  export: '<path d="M12 15V3M7 8l5-5 5 5M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16v4zM13.5 6.5l4 4"/>',
  copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  play: '<path d="M7 4.5v15l12-7.5z"/>',
  undo: '<path d="M9 14L4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>',
  redo: '<path d="M15 14l5-5-5-5"/><path d="M20 9H9.5a5.5 5.5 0 0 0 0 11H13"/>',
  save: '<path d="M5 3h11l3 3v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><path d="M7 3v5h8V3M7 21v-7h10v7"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  checkCircle: '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l3 3 5-6"/>',
  warn: '<path d="M12 3.5l9.5 16.5h-19z"/><path d="M12 10v4.5M12 17.4v.1"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  xCircle: '<circle cx="12" cy="12" r="9"/><path d="M9 9l6 6M15 9l-6 6"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.1"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  minus: '<path d="M5 12h14"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-4-4"/>',
  back: '<path d="M15 18l-6-6 6-6"/>',
  chevron: '<path d="M9 6l6 6-6 6"/>',
  mirror: '<path d="M12 3v18M8 7L3 12l5 5zM16 7l5 5-5 5z"/>',
  gear: '<circle cx="12" cy="17" r="3.5"/><path d="M12 13.5V4M8 6h8"/>',
  engine: '<rect x="3" y="8" width="13" height="8" rx="3"/><path d="M16 10l5-2v8l-5-2"/>',
  wing: '<path d="M2 14l9-6h3l8 6-9 1z"/>',
  body: '<path d="M2 12c0-2 3-4 8-4h6c3 0 6 2 6 4s-3 4-6 4h-6c-5 0-8-2-8-4z"/>',
  weapon: '<path d="M3 12h13l4-2v4l-4-2M7 12v3M11 12v3"/>',
  cockpit: '<path d="M4 15c0-4 3.5-8 8-8s8 4 8 8z"/><path d="M4 15h16"/>',
  frame: '<path d="M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5"/>',
  magic: '<path d="M5 19L17 7M14 4l1-2 1 2 2 1-2 1-1 2-1-2-2-1zM19 12l.7-1.3L21 10l-1.3-.7L19 8l-.7 1.3L17 10l1.3.7z"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  refresh: '<path d="M20 11a8 8 0 0 0-14.6-4.5L4 8M4 4v4h4M4 13a8 8 0 0 0 14.6 4.5L20 16M20 20v-4h-4"/>',
  spinner: '<path d="M12 3a9 9 0 1 0 9 9"/>',
  dash: '<path d="M7 12h10"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  helicopter: '<path d="M3 5h18M12 5v4M5 13a5 4 0 0 1 10 0v1H5zM15 13h6M9 17v3M5 20h9"/>',
  eyeOff: '<path d="M3 3l18 18M10.6 6.1A10 10 0 0 1 12 6c6.5 0 10 6 10 6a17 17 0 0 1-3.1 3.8M6.6 6.7C3.7 8.5 2 12 2 12s3.5 6 10 6a9.6 9.6 0 0 0 4.4-1.1M9.9 10a3 3 0 0 0 4.1 4.1"/>',
  lock: '<rect x="5" y="11" width="14" height="9" rx="1.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  unlock: '<rect x="5" y="11" width="14" height="9" rx="1.5"/><path d="M8 11V8a4 4 0 0 1 7.7-1.5"/>',
  cube: '<path d="M12 3l8 4.5v9L12 21l-8-4.5v-9z"/><path d="M4 7.5l8 4.5 8-4.5M12 12v9"/>',
  image: '<rect x="3" y="4" width="18" height="16" rx="1.5"/><circle cx="9" cy="10" r="2"/><path d="M21 16l-5-5-9 9"/>',
  pan: '<path d="M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3"/>',
  grid: '<rect x="4" y="4" width="16" height="16"/><path d="M4 12h16M12 4v16"/>',
  cursor: '<path d="M5 3l14 7-6 2-2 6z"/>',
  move: '<path d="M12 3v18M3 12h18"/><path d="M9 6l3-3 3 3M9 18l3 3 3-3M6 9l-3 3 3 3M18 9l3 3-3 3"/>',
  rotate: '<path d="M20 12a8 8 0 1 1-2.3-5.7"/><path d="M20 4v5h-5"/>',
  scale: '<rect x="3" y="11" width="10" height="10"/><path d="M13 11l8-8M15 3h6v6"/>',
  extrude: '<path d="M4 15l8 4 8-4M4 15l8-4 8 4"/><path d="M12 11V3M9 6l3-3 3 3"/>',
  inset: '<rect x="3" y="3" width="18" height="18"/><rect x="8" y="8" width="8" height="8"/><path d="M3 3l5 5M21 3l-5 5M3 21l5-5M21 21l-5-5"/>',
  loopcut: '<rect x="3" y="5" width="18" height="14"/><path d="M12 3v18" stroke-dasharray="2 2"/>',
  merge: '<path d="M5 5l7 7 7-7M12 12v9"/>',
  measure: '<path d="M3 17L17 3l4 4L7 21z"/><path d="M7 13l2 2M10 10l2 2M13 7l2 2"/>',
  quad: '<rect x="3" y="3" width="8" height="8"/><rect x="13" y="3" width="8" height="8"/><rect x="3" y="13" width="8" height="8"/><rect x="13" y="13" width="8" height="8"/>',
  xray: '<rect x="4" y="4" width="16" height="16"/><path d="M4 4l16 16M20 4L4 20" stroke-dasharray="2 2"/>',
  wire: '<path d="M12 3l8 4.5v9L12 21l-8-4.5v-9z"/><path d="M4 7.5l16 9M20 7.5l-16 9M12 3v18"/>',
  solid: '<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5a8.5 8.5 0 0 0 0 17z" fill="currentColor"/>',
  magnet: '<path d="M6 3v8a6 6 0 0 0 12 0V3M6 7h4M14 7h4"/>',
  prop: '<circle cx="12" cy="12" r="2.5"/><circle cx="12" cy="12" r="8.5" stroke-dasharray="3 2.5"/>',
  help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.3a2.6 2.6 0 0 1 5 1c0 1.8-2.5 2.2-2.5 3.8M12 17.4v.1"/>',
  select: '<rect x="4" y="4" width="16" height="16" stroke-dasharray="3 2.5"/><path d="M10 9l7 3.5-3 1-1.5 3z"/>',
  ring: '<ellipse cx="12" cy="12" rx="5" ry="8.5"/><path d="M3 12h3M18 12h3"/>',
  vert: '<rect x="4" y="4" width="16" height="16" stroke-opacity=".45"/><rect x="2.5" y="2.5" width="4" height="4" fill="currentColor"/>',
  edge: '<rect x="4" y="4" width="16" height="16" stroke-opacity=".45"/><path d="M4 4h16" stroke-width="3"/>',
  face: '<rect x="4" y="4" width="16" height="16"/><rect x="7" y="7" width="10" height="10" fill="currentColor" stroke="none" opacity=".55"/>',
  smooth: '<path d="M3 17c4-10 14-10 18 0"/><path d="M3 17l4-6 5-3 5 3 4 6" stroke-dasharray="2 2"/>',
  fix: '<path d="M14.7 6.3a4 4 0 0 0-5.4 5.2L3.5 17.3a1.8 1.8 0 0 0 2.6 2.6l5.8-5.8a4 4 0 0 0 5.2-5.4l-2.5 2.5-2.4-.6-.6-2.4 2.5-2.5z"/>',
};

export function icon(name, cls = '') {
  const body = P[name] || P.info;
  const fill = name === 'play' || name === 'plane' ? 'currentColor' : 'none';
  return `<span class="i ${cls}" aria-hidden="true"><svg viewBox="0 0 24 24" fill="${fill}" stroke="currentColor" stroke-width="${fill === 'none' ? 1.9 : 0}" stroke-linecap="round" stroke-linejoin="round">${body}</svg></span>`;
}

/** Fills every <span data-icon="name"> in a subtree. */
export function hydrateIcons(root = document) {
  root.querySelectorAll('[data-icon]').forEach(el => {
    const tmp = document.createElement('div');
    tmp.innerHTML = icon(el.dataset.icon, el.className.replace(/\bi\b/, '').trim());
    el.replaceWith(tmp.firstChild);
  });
}
