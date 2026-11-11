// The builder's 3D view: drawing, cameras (single or quad view), navigation, picking and overlays. What the mouse does
// beyond navigation (selecting, transforming) is decided by the builder through the callbacks.
//
// Plane parts live under `planeRoot`, which mirrors x so the plane's own (Unity, left-handed) coordinates can be used
// directly: x right, y up, z forward. three.js flips face winding for mirrored transforms by itself. u2w / w2u convert.
import * as THREE from 'three';
import { TransformControls } from 'three/addons/TransformControls.js';
import { CameraRig, VIEWS } from './camera.js';
import { icon } from '../icons.js';
import {
  buildWing, loft, meshCenter, isSelfMirrored, fuselageTransform, shapeMesh, shapeTransform, quatMul, CENTRE_TOL,
} from './geometry.js';
import { materialById, materialAt, glassOf, paintOf, shapeMaterial, eyeOf } from './parts.js';

export const u2w = p => new THREE.Vector3(-p[0], p[1], p[2]);
export const w2u = v => [-v.x, v.y, v.z];
const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

const PART_COLORS = { engine: 0x3b3f46, prop: 0x2a2d32, gear: 0x34373c, wheel: 0x141416, hardpoint: 0x7ccf7a, gun: 0xe6b45b, glass: 0x5B9BE6 };
const QUAD_VIEWS = ['top', 'front', 'right'];
const clamp01 = v => Math.max(0, Math.min(1, Number.isFinite(+v) ? +v : 0));
// Instruments are built facing local +z with 12 o'clock at +y, then turned so the face looks along the rotation's +y and
// 12 o'clock is its +z, as the mod does (DesignVisuals.AddInstruments: q * LookRotation(up, forward), a half turn about
// (0, 1, 1)/sqrt 2). Flyout's own gauges follow the same convention.
const FACE_UP = [0, Math.SQRT1_2, Math.SQRT1_2, 0];

/** The palette material a converted body's slot is drawn with (as DesignMaterials.ForSlot and DesignGenerator.ApplyLook). */
function slotMaterial(spec, src, slot, chosen) {
  const table = Array.isArray(src.materials) ? src.materials : null;
  let base, isPaint;
  if (table && slot < table.length) { const e = materialAt(spec, table[slot]); base = e || paintOf(spec); isPaint = !e || !e.kind || e.kind === 'paint'; }
  else if (slot === 0) { base = paintOf(spec); isPaint = true; }
  else { base = glassOf(spec); isPaint = false; }
  return chosen && isPaint ? chosen : base;
}

export class Viewport {
  constructor(container, cb) {
    this.container = container;
    this.cb = cb;
    this.items = new Map();        // key "kind:id" -> { object, sig, kind }
    this.meshGeoCache = new Map(); // source mesh index -> BufferGeometry
    this.selfMirrored = new Map();
    this.sourceMeshes = [];
    this.selected = new Set();
    this.active = null;
    this.hidden = new Set();
    this.locked = new Set();
    this.editKey = null;
    this.spec = null;
    this.needsRender = true;
    this.extent = 10;
    this.shading = { wire: false, xray: false };
    this.quad = false;
    this.labels = [];              // HTML labels placed every frame: { el, p (plane), quad? }

    const r = this.renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    r.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    r.domElement.tabIndex = 0;
    r.domElement.setAttribute('aria-label', '3D view of the plane. Middle-drag or Alt+drag to orbit, Shift+middle-drag or right-drag to pan, wheel to zoom. Click a part to select it.');
    r.domElement.setAttribute('role', 'application');
    container.prepend(r.domElement);
    this.canvas = r.domElement;

    this.scene = new THREE.Scene();
    this.rig = new CameraRig();
    this.quadRigs = QUAD_VIEWS.map(v => { const q = new CameraRig(); q.setView(v); return q; });
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x4a4d55, 1.5));
    const sun = new THREE.DirectionalLight(0xffffff, 1.8); sun.position.set(-8, 14, 10); this.scene.add(sun);
    const fill = new THREE.DirectionalLight(0xffffff, 0.55); fill.position.set(10, -4, -12); this.scene.add(fill);

    this.planeRoot = new THREE.Group();
    this.planeRoot.scale.set(-1, 1, 1);
    this.scene.add(this.planeRoot);
    this.markers = new THREE.Group(); this.planeRoot.add(this.markers);
    this.overlay = new THREE.Group(); this.planeRoot.add(this.overlay);          // edit-mode cage, loop-cut preview, measure box
    this.refGroup = new THREE.Group(); this.scene.add(this.refGroup);            // reference images (world space)
    this.ground = new THREE.Group(); this.scene.add(this.ground);
    this.groundY = 0;

    this.gizmo = new TransformControls(this.rig.camera, r.domElement);
    this.gizmo.setSize(0.8);
    this.gizmoProxy = new THREE.Object3D();
    this.scene.add(this.gizmoProxy, this.gizmo);
    this.gizmo.enabled = false;
    this.gizmo.visible = false;
    this.gizmo.addEventListener('change', () => this.requestRender());
    this.gizmo.addEventListener('dragging-changed', e => {
      if (e.value) {
        this.gizmoStart = { p: this.gizmoProxy.position.clone(), q: this.gizmoProxy.quaternion.clone(), s: this.gizmoProxy.scale.clone() };
        this.cb.onGizmoStart?.(this.gizmo.mode);
      } else this.cb.onGizmoEnd?.();
    });
    this.gizmo.addEventListener('objectChange', () => {
      if (!this.gizmoStart) return;
      const st = this.gizmoStart;
      if (this.gizmo.mode === 'translate') this.cb.onGizmoChange?.({ type: 'translate', d: w2u(this.gizmoProxy.position.clone().sub(st.p)) });
      else if (this.gizmo.mode === 'rotate') {
        const dq = this.gizmoProxy.quaternion.clone().multiply(st.q.clone().invert());
        this.cb.onGizmoChange?.({ type: 'rotate', q: [dq.x, -dq.y, -dq.z, dq.w] }); // world -> plane (x mirrored)
      } else {
        const s = this.gizmoProxy.scale;
        this.cb.onGizmoChange?.({ type: 'scale', s: [s.x / st.s.x, s.y / st.s.y, s.z / st.s.z] });
      }
    });

    this.buildNavGizmo();
    this.setupPointer();
    this.applyTheme();
    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    this.onTheme = () => this.applyTheme();
    window.addEventListener('openplane-theme', this.onTheme);
    this.resize();
    this.loop = () => {
      this.raf = requestAnimationFrame(this.loop);
      if (!this.needsRender) return;
      this.needsRender = false;
      this.render();
    };
    this.loop();
  }

  requestRender() { this.needsRender = true; }

  dispose() {
    cancelAnimationFrame(this.raf);
    this.resizeObserver.disconnect();
    window.removeEventListener('openplane-theme', this.onTheme);
    this.gizmo.dispose();
    this.scene.traverse(o => { o.geometry?.dispose?.(); if (o.material) [].concat(o.material).forEach(m => { m.map?.dispose(); m.dispose(); }); });
    for (const t of this.refTextures?.values() || []) t.dispose();
    this.renderer.dispose();
    this.canvas.remove();
  }

  // ---------------------------------------------------------------- theme, ground, sizing

  applyTheme() {
    const c = n => new THREE.Color(css(n) || '#000');
    this.colors = {
      bg: c('--viewport-bg'), grid: c('--line'), major: c('--line-strong'), red: c('--red'), text: c('--text'),
      muted: c('--muted'), blue: c('--blue'), ok: c('--ok'), warn: c('--warn'), panel: c('--panel'),
      redCss: css('--red'), textCss: css('--text'), mutedCss: css('--muted'),
    };
    this.scene.background = this.colors.bg;
    this.rebuildGround(this.extent * 3, this.groundY);
    this.refreshHighlights();
    this.navDirty = true;
    this.cb.onThemeChange?.();
    this.requestRender();
  }

  rebuildGround(size, groundY) {
    clearAndDispose(this.ground);
    const s = Math.max(20, Math.ceil(size / 10) * 10);
    const minor = new THREE.GridHelper(s, s, this.colors.grid, this.colors.grid);
    minor.material.transparent = true; minor.material.opacity = 0.8;
    const major = new THREE.GridHelper(s, s / 5, this.colors.major, this.colors.major);
    major.position.y = 0.002;
    // The centre line along the nose axis (z) in blue, the wing axis (x) in red, like Blender's axis lines.
    const axis = (a, b, color) => new THREE.Line(new THREE.BufferGeometry().setFromPoints([a, b]), new THREE.LineBasicMaterial({ color }));
    const zl = axis(new THREE.Vector3(0, 0.004, -s / 2), new THREE.Vector3(0, 0.004, s / 2), this.colors.blue);
    const xl = axis(new THREE.Vector3(-s / 2, 0.004, 0), new THREE.Vector3(s / 2, 0.004, 0), this.colors.red);
    xl.material.transparent = zl.material.transparent = true; xl.material.opacity = zl.material.opacity = 0.7;
    this.ground.add(minor, major, zl, xl);
    const a = Math.max(0.6, s * 0.02);
    const shape = new THREE.Shape();
    shape.moveTo(0, a * 1.4); shape.lineTo(a, 0); shape.lineTo(a * 0.35, 0); shape.lineTo(a * 0.35, -a); shape.lineTo(-a * 0.35, -a); shape.lineTo(-a * 0.35, 0); shape.lineTo(-a, 0); shape.closePath();
    const arrow = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshBasicMaterial({ color: this.colors.blue, side: THREE.DoubleSide, transparent: true, opacity: 0.8 }));
    arrow.rotation.x = -Math.PI / 2;
    arrow.position.set(0, 0.006, s / 2 - a * 2.2);
    this.ground.add(arrow);
    this.ground.position.y = groundY;
    this.groundY = groundY;
    this.requestRender();
  }

  resize() {
    const w = Math.max(1, this.container.clientWidth), h = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(w, h, false);
    this.size = { w, h };
    this.updateAspects();
    this.requestRender();
  }

  updateAspects() {
    const { w, h } = this.size || { w: 1, h: 1 };
    if (this.quad) { for (const q of [...this.quadRigs, this.rig]) q.setAspect((w / 2) / (h / 2)); }
    else this.rig.setAspect(w / h);
  }

  /** Viewport rectangles in CSS pixels: [{ x, y, w, h, rig }] (y from the top). */
  viewports() {
    const { w, h } = this.size || { w: 1, h: 1 };
    if (!this.quad) return [{ x: 0, y: 0, w, h, rig: this.rig, main: true }];
    const hw = Math.floor(w / 2), hh = Math.floor(h / 2);
    return [
      { x: 0, y: 0, w: hw, h: hh, rig: this.quadRigs[0] },
      { x: hw, y: 0, w: w - hw, h: hh, rig: this.quadRigs[1] },
      { x: 0, y: hh, w: hw, h: h - hh, rig: this.quadRigs[2] },
      { x: hw, y: hh, w: w - hw, h: h - hh, rig: this.rig, main: true },
    ];
  }

  viewportAt(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    const x = clientX - rect.left, y = clientY - rect.top;
    return this.viewports().find(v => x >= v.x && x < v.x + v.w && y >= v.y && y < v.y + v.h) || this.viewports()[0];
  }

  /** The rig the user last worked in (for header view buttons and the transform's view axis). */
  get activeRig() { return this.focusRig && (this.quad || this.focusRig === this.rig) ? this.focusRig : this.rig; }

  setQuad(on) {
    if (on) this.rig.leaveCockpit();
    this.quad = !!on;
    if (this.quad) for (const q of this.quadRigs) { q.target.copy(this.rig.target); q.dist = this.rig.dist; q.sceneSize = this.rig.sceneSize; q.update(); }
    this.focusRig = this.rig;
    this.updateAspects();
    this.cb.onViewChange?.();
    this.requestRender();
  }

  // ---------------------------------------------------------------- rendering

  render() {
    const r = this.renderer;
    const { h: H } = this.size || { h: 1 };
    r.setScissorTest(true);
    // From the pilot's eye the seat marker and the balance markers would be in the way.
    const seat = this.items.get('cockpit:cockpit')?.object;
    const seatShown = seat?.visible, markersShown = this.markers.visible;
    for (const v of this.viewports()) {
      const cam = v.rig.camera;
      r.setViewport(v.x, H - v.y - v.h, v.w, v.h);
      r.setScissor(v.x, H - v.y - v.h, v.w, v.h);
      this.updateReferenceVisibility(v.rig);
      if (seat) seat.visible = seatShown && !v.rig.cockpit;
      this.markers.visible = markersShown && !v.rig.cockpit;
      this.gizmo.camera = this.gizmoRig ? this.gizmoRig.camera : this.activeRig.camera;
      this.gizmo.visible = this.gizmo.enabled;
      r.render(this.scene, cam);
    }
    if (seat) seat.visible = seatShown;
    this.markers.visible = markersShown;
    r.setScissorTest(false);
    this.placeLabels();
    if (this.navDirty || this.navRigView !== this.activeRig.yaw + ':' + this.activeRig.pitch) this.drawNav();
  }

  /** Projects a plane-space point to CSS pixels in a viewport. */
  project(p, vp = this.viewports().find(v => v.main)) {
    const v = u2w(p).project(vp.rig.camera);
    return { x: vp.x + (v.x + 1) / 2 * vp.w, y: vp.y + (1 - v.y) / 2 * vp.h, z: v.z, visible: v.z > -1 && v.z < 1 };
  }

  placeLabels() {
    const vp = this.viewports().find(v => v.main);
    for (const l of this.labels) {
      const s = this.project(l.p, vp);
      l.el.style.display = s.visible ? '' : 'none';
      l.el.style.left = `${s.x}px`; l.el.style.top = `${s.y}px`;
    }
  }

  // ---------------------------------------------------------------- the plane

  setSourceMeshes(meshes) {
    this.sourceMeshes = meshes || [];
    for (const g of this.meshGeoCache.values()) g.dispose();
    this.meshGeoCache.clear();
    this.selfMirrored.clear();
    for (const [, it] of this.items) { this.planeRoot.remove(it.object); this.disposeObject(it.object); }
    this.items.clear();
  }

  /** Rebuilds only the parts whose data changed. */
  setPlane(spec) {
    this.spec = spec;
    const paint = spec.paint?.base_color || [0.6, 0.62, 0.64, 1];
    // Every part's colour depends on the plane paint and the palette, so a change to either redraws them.
    const looks = (spec.materials || []).map(m => [m?.id, m?.kind, m?.color, m?.metallic, m?.smoothness]);   // not the names
    const paintKey = paint.join(',') + '|' + (spec.paint?.smoothness ?? 0.3) + '|' + (spec.paint?.metallic ?? 0) + '|' + JSON.stringify(looks) + '|' + this.shading.wire + this.shading.xray;
    const wanted = new Map();
    const add = (kind, item) => wanted.set(`${kind}:${item.id}`, { kind, item, sig: paintKey + signature(item) });
    (spec.fuselages || []).forEach(f => add('fuselage', f));
    (spec.shapes || []).forEach(s => add('shape', s));
    (spec.wings || []).forEach(w => add('wing', w));
    (spec.engines || []).forEach(e => add('engine', e));
    (spec.gear || []).forEach(g => add('gear', g));
    (spec.hardpoints || []).forEach(h => add('hardpoint', h));
    (spec.guns || []).forEach(g => add('gun', g));
    (spec.instruments || []).forEach(n => add('instrument', n));
    (spec.lights || []).forEach(l => add('light', l));
    if (spec.cockpit) add('cockpit', { id: 'cockpit', ...spec.cockpit });

    for (const [key, it] of this.items) {
      if (!wanted.has(key)) { this.planeRoot.remove(it.object); this.disposeObject(it.object); this.items.delete(key); }
    }
    for (const [key, w] of wanted) {
      const cur = this.items.get(key);
      if (cur && cur.sig === w.sig) continue;
      if (cur) { this.planeRoot.remove(cur.object); this.disposeObject(cur.object); }
      const obj = this.buildItem(w.kind, w.item, spec);
      obj.userData = { kind: w.kind, id: w.item.id, key };
      this.planeRoot.add(obj);
      this.items.set(key, { object: obj, sig: w.sig, kind: w.kind });
    }
    this.applyVisibility();
    this.refreshHighlights();
    this.updateExtent();
    this.setReferences(spec.references || []);
    this.requestRender();
  }

  disposeObject(obj) {
    obj.traverse(o => {
      if (o.geometry && !o.userData.sharedGeometry) o.geometry.dispose();
      if (o.material) [].concat(o.material).forEach(m => m.dispose());
    });
  }

  updateExtent() {
    const box = new THREE.Box3();
    for (const [key, it] of this.items) if (!this.hidden.has(key)) box.expandByObject(it.object);
    if (box.isEmpty()) { this.bounds = null; return; }
    this.bounds = box;
    const size = box.getSize(new THREE.Vector3());
    const ext = Math.max(size.x, size.y, size.z, 2);
    for (const q of [this.rig, ...this.quadRigs]) q.sceneSize = ext;
    if (Math.abs(ext - this.extent) / this.extent > 0.25) { this.extent = ext; this.rebuildGround(ext * 3, this.groundY); }
  }

  /**
   * A three.js material for a palette entry ({ kind, color [r,g,b,a] 0..1, metallic, smoothness }): paint is solid, glass is
   * see-through (its alpha, at least 0.2), a light colour glows. `shade` lightens (+) or darkens (-) it a little.
   */
  surfaceMaterial(def, shade = 0) {
    const c = def?.color || [0.6, 0.62, 0.64, 1];
    // Colours are stored as the game (and the colour picker) sees them: sRGB.
    const color = new THREE.Color().setRGB(clamp01(c[0]), clamp01(c[1]), clamp01(c[2]), THREE.SRGBColorSpace);
    if (shade) color.offsetHSL(0, 0, shade);
    const m = new THREE.MeshStandardMaterial({ color, roughness: 1 - 0.7 * clamp01(def?.smoothness ?? 0.3), metalness: Math.min(0.8, clamp01(def?.metallic ?? 0)) });
    if (def?.kind === 'glass') { m.transparent = true; m.opacity = Math.max(0.2, Math.min(1, c[3] ?? 0.25)); m.depthWrite = false; }
    if (def?.kind === 'light') { m.emissive.copy(color); m.emissiveIntensity = 1; }
    m.polygonOffset = true; m.polygonOffsetFactor = 1; m.polygonOffsetUnits = 1;
    return this.shade(m);
  }

  shade(m) {
    m.wireframe = this.shading.wire;
    if (this.shading.xray) { m.transparent = true; m.opacity = Math.min(m.opacity ?? 1, 0.32); m.depthWrite = false; }
    m.userData.baseOpacity = m.opacity;
    // Its own glow, which the selection highlight adds to (kept as an array so material.clone() copies it).
    if (m.emissive) m.userData.baseEmissive = m.emissive.toArray();
    return m;
  }

  buildItem(kind, item, spec) {
    const g = new THREE.Group();
    const paint = paintOf(spec);
    // One three.js material per palette entry (and shade) in this part.
    const mats = new Map();
    const look = (def, shade = 0) => {
      const k = `${JSON.stringify(def)}|${shade}`;
      if (!mats.has(k)) mats.set(k, this.surfaceMaterial(def, shade));
      return mats.get(k);
    };
    const std = (o) => this.shade(new THREE.MeshStandardMaterial(o));
    const twin = (obj, pos, mirror, tol = CENTRE_TOL) => {
      g.add(obj);
      if (mirror && Math.abs(pos?.[0] || 0) > tol) {
        const m = new THREE.Group();
        m.scale.x = -1;
        const c = obj.clone();
        c.traverse(o => { if (o.isMesh) { o.userData.sharedGeometry = true; o.material = Array.isArray(o.material) ? o.material.map(x => x.clone()) : o.material.clone(); } });
        m.add(c);
        g.add(m);
        return c;
      }
      return null;
    };
    switch (kind) {
      case 'fuselage': {
        if (item.kind === 'mesh') {
          const src = this.sourceMeshes[item.mesh_index];
          if (!src) return g;
          let geo = this.meshGeoCache.get(item.mesh_index);
          if (!geo) {
            geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.Float32BufferAttribute(src.vertices, 3));
            // Triangles sorted by material slot, one draw group per slot, so each slot can have its own colour.
            const tris = src.triangles || [], nt = Math.floor(tris.length / 3);
            const tm = Array.isArray(src.triangle_materials) && src.triangle_materials.length >= nt ? src.triangle_materials : null;
            const slotOf = t => Math.max(0, Math.min(1023, tm[t] | 0));
            let slots = 1;
            if (tm) for (let t = 0; t < nt; t++) slots = Math.max(slots, slotOf(t) + 1);
            const index = new Uint32Array(nt * 3);
            if (tm && slots > 1) {
              const counts = new Array(slots).fill(0);
              for (let t = 0; t < nt; t++) counts[slotOf(t)]++;
              const start = [];
              let at = 0;
              for (let s = 0; s < slots; s++) { start[s] = at; if (counts[s]) geo.addGroup(at * 3, counts[s] * 3, s); at += counts[s]; }
              const next = [...start];
              for (let t = 0; t < nt; t++) { const s = slotOf(t), o = next[s]++ * 3; index[o] = tris[t * 3]; index[o + 1] = tris[t * 3 + 1]; index[o + 2] = tris[t * 3 + 2]; }
            } else {
              index.set(tris.slice(0, nt * 3));
              geo.addGroup(0, nt * 3, 0);
            }
            geo.setIndex(new THREE.BufferAttribute(index, 1));
            geo.userData.slots = slots;
            geo.userData.hasSlots = !!tm;
            geo.computeVertexNormals();
            geo.computeBoundingSphere();
            this.meshGeoCache.set(item.mesh_index, geo);
          }
          // A chosen material replaces the paint slots; glass stays glass (a body without slots takes it all over).
          const chosen = materialById(spec, item.material_id);
          const list = Array.from({ length: geo.userData.slots }, (_, s) => look(chosen && !geo.userData.hasSlots ? chosen : slotMaterial(spec, src, s, chosen)));
          const mesh = new THREE.Mesh(geo, list);
          mesh.userData.sharedGeometry = true;
          mesh.matrixAutoUpdate = false;
          mesh.matrix.fromArray(fuselageTransform(item, src).matrix);
          if (!this.selfMirrored.has(item.mesh_index)) this.selfMirrored.set(item.mesh_index, isSelfMirrored(src.vertices));
          const sym = this.selfMirrored.get(item.mesh_index);
          const moved = Math.abs((item.position || [0])[0]) > 1e-3 || (item.rotation_deg || []).some(v => Math.abs(+v || 0) > 1e-6);
          twin(mesh, sym ? [moved ? 1 : 0, 0, 0] : [1, 0, 0], item.mirror, sym ? 1e-3 : 0);
        } else {
          const { positions, indices } = loft(item);
          if (!indices.length) return g;
          const geo = new THREE.BufferGeometry();
          geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
          geo.setIndex(new THREE.BufferAttribute(indices, 1));
          geo.computeVertexNormals();
          const pos = item.position || [0, 0, 0];
          twin(new THREE.Mesh(geo, look(materialById(spec, item.material_id) || paint)), pos, item.mirror);
        }
        break;
      }
      case 'shape': {
        const m = shapeMesh(item);
        if (!m.indices.length) return g;
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(m.positions, 3));
        geo.setIndex(new THREE.BufferAttribute(m.indices, 1));
        geo.computeVertexNormals();
        const mesh = new THREE.Mesh(geo, look(shapeMaterial(spec, item)));
        mesh.matrixAutoUpdate = false;
        const t = shapeTransform(item);
        mesh.matrix.fromArray(t.matrix);
        let twinNeeded = !!item.mirror;
        if (twinNeeded) {
          const v = []; for (let i = 0; i < m.positions.length; i += 3) v.push(...t.toPlane([m.positions[i], m.positions[i + 1], m.positions[i + 2]]));
          twinNeeded = !isSelfMirrored(v);
        }
        twin(mesh, [twinNeeded ? 1 : 0, 0, 0], twinNeeded, 0.5);
        break;
      }
      case 'wing': {
        // Panels in the wing's material (or, kept from a converted plane, each panel's own); a surface in its own
        // material or else its panel's. Moving surfaces are drawn a shade darker so their hinge lines show.
        const panels = buildWing(item);
        const wingDef = materialById(spec, item.material_id);
        const fixed = item.kind === 'panels';
        const slabs = new Map();   // "material json|shade" -> { def, shade, verts }
        const into = (def, shade) => {
          const k = `${JSON.stringify(def)}|${shade}`;
          if (!slabs.has(k)) slabs.set(k, { def, shade, verts: [] });
          return slabs.get(k).verts;
        };
        for (const p of panels) {
          const pDef = wingDef || (fixed ? materialAt(spec, p.material) : null) || paint;
          slab(into(pDef, 0.04), p.root_leading, p.root_trailing, p.tip_trailing, p.tip_leading, p.up, 0.085);
          for (const s of p.surfaces || []) {
            const sDef = (fixed ? materialAt(spec, s.material) : materialById(spec, s.material_id)) || pDef;
            slab(into(sDef, s.role === 'fixed' ? 0.04 : -0.07), s.root_leading, s.root_trailing, s.tip_trailing, s.tip_leading, p.up, 0.05);
          }
        }
        for (const { def, shade, verts } of slabs.values()) if (verts.length) g.add(slabMesh(verts, look(def, shade)));
        break;
      }
      case 'engine': {
        const pos = item.position || [0, 0, 0];
        const e = new THREE.Group();
        const prop = item.variant ? item.variant === 'piston' || item.variant === 'turboprop' || item.variant === 'electric' : item.type === 'prop';
        if (prop) {
          const d = Math.max(0.3, +item.prop_diameter_m || 2);
          const disc = new THREE.Mesh(new THREE.CylinderGeometry(d / 2, d / 2, 0.04, 40), std({ color: PART_COLORS.prop, transparent: true, opacity: 0.35, roughness: 0.5 }));
          disc.rotation.x = Math.PI / 2;
          const spinner = new THREE.Mesh(new THREE.ConeGeometry(d * 0.09, d * 0.22, 24), std({ color: PART_COLORS.engine, roughness: 0.4, metalness: 0.4 }));
          spinner.rotation.x = Math.PI / 2;
          spinner.position.z = d * 0.1;
          const blades = new THREE.Mesh(new THREE.BoxGeometry(d * 0.96, d * 0.07, 0.03), std({ color: PART_COLORS.prop, roughness: 0.6 }));
          e.add(disc, spinner, blades);
        } else {
          const d = Math.max(0.2, +item.diameter_m || 0.9);
          const L = Math.max(1.2, d * 3);
          const can = new THREE.Mesh(new THREE.CylinderGeometry(d / 2, d * 0.42, L, 32, 1, true), std({ color: PART_COLORS.engine, roughness: 0.45, metalness: 0.5, side: THREE.DoubleSide }));
          can.rotation.x = Math.PI / 2;
          const face = new THREE.Mesh(new THREE.CircleGeometry(d / 2, 32), std({ color: 0x111113, roughness: 0.8 }));
          face.position.z = L / 2;
          const ab = item.sizing === 'designed' ? !!item.afterburner : (+item.afterburner_kn || 0) > (+item.thrust_kn || 0);
          const nozzle = new THREE.Mesh(new THREE.CircleGeometry(d * 0.42, 32), std({ color: ab ? 0xc0582a : 0x2a2d31, roughness: 0.6 }));
          nozzle.position.z = -L / 2;
          nozzle.rotation.y = Math.PI;
          e.add(can, face, nozzle);
        }
        e.position.set(pos[0], pos[1], pos[2]);
        twin(e, pos, item.mirror);
        break;
      }
      case 'gear': {
        const pos = item.position || [0, 0, 0];
        const strut = Math.max(0.05, +item.strut_length || 0.5), r = Math.max(0.05, +item.wheel_radius || 0.3);
        const gg = new THREE.Group();
        const s = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, strut, 12), std({ color: PART_COLORS.gear, roughness: 0.4, metalness: 0.6 }));
        s.position.y = -strut / 2;
        const wheel = new THREE.Mesh(new THREE.CylinderGeometry(r, r, r * 0.7, 28), std({ color: PART_COLORS.wheel, roughness: 0.9 }));
        wheel.rotation.z = Math.PI / 2;
        wheel.position.y = -strut;
        gg.add(s, wheel);
        gg.position.set(pos[0], pos[1], pos[2]);
        twin(gg, pos, item.mirror);
        break;
      }
      case 'hardpoint': {
        const pos = item.position || [0, 0, 0];
        const h = new THREE.Group();
        const pylon = new THREE.Mesh(new THREE.BoxGeometry(0.08, 0.14, 0.7), std({ color: PART_COLORS.hardpoint, roughness: 0.6 }));
        const store = new THREE.Mesh(new THREE.CapsuleGeometry(0.1, 1.5, 4, 12), std({ color: PART_COLORS.hardpoint, transparent: true, opacity: item.internal ? 0.25 : 0.45, roughness: 0.6 }));
        store.rotation.x = Math.PI / 2;
        store.position.y = -0.2;
        h.add(pylon, store);
        const q = item.rotation || [0, 0, 0, 1];
        h.quaternion.set(q[0], q[1], q[2], q[3]);
        h.position.set(pos[0], pos[1], pos[2]);
        twin(h, pos, item.mirror);
        break;
      }
      case 'gun': {
        const pos = item.position || [0, 0, 0];
        const dir = new THREE.Vector3(...(item.direction || [0, 0, 1])).normalize();
        const cal = Math.max(5, +item.caliber_mm || 20) / 1000;
        const len = 1.2 + cal * 20;
        const barrel = new THREE.Mesh(new THREE.CylinderGeometry(cal * 1.6, cal * 1.6, len, 12), std({ color: PART_COLORS.gun, roughness: 0.4, metalness: 0.6 }));
        barrel.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir);
        barrel.position.set(pos[0], pos[1], pos[2]).add(dir.clone().multiplyScalar(len / 2));
        twin(barrel, pos, item.mirror);
        break;
      }
      case 'cockpit': {
        const pos = item.position || [0, 0, 0];
        const c = new THREE.Mesh(new THREE.SphereGeometry(1, 32, 16, 0, Math.PI * 2, 0, Math.PI / 2),
          std({ color: PART_COLORS.glass, transparent: true, opacity: 0.55, roughness: 0.1, metalness: 0.3 }));
        c.scale.set(0.42, 0.42, 1.0);
        c.position.set(pos[0], pos[1], pos[2]);
        g.add(c);
        // The pilot's eye: a small ball with a cone showing the way it looks (forward), joined to the seat by a line.
        const eye = eyeOf(item);
        const ball = new THREE.Mesh(new THREE.SphereGeometry(0.06, 16, 12), std({ color: 0xf2f2ee, roughness: 0.4 }));
        ball.position.set(eye[0], eye[1], eye[2]);
        const aim = new THREE.Mesh(new THREE.ConeGeometry(0.045, 0.16, 16), std({ color: this.colors.blue, roughness: 0.5, transparent: true, opacity: 0.8 }));
        aim.rotation.x = Math.PI / 2;     // the cone's tip (+y) points forward (+z)
        aim.position.set(eye[0], eye[1], eye[2] + 0.15);
        const stalk = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(...pos), new THREE.Vector3(...eye)]),
          new THREE.LineBasicMaterial({ color: this.colors.muted, transparent: true, opacity: 0.7 }));
        g.add(ball, aim, stalk);
        break;
      }
      case 'instrument': twin(this.buildInstrument(item, std), item.position, item.mirror); break;
      case 'light': {
        const lamp = this.buildLight(item, item.color, std);
        g.add(lamp);
        // The twin on the other side; a navigation light's twin swaps red and green (as the game does).
        if (item.mirror && Math.abs((item.position || [0])[0]) > CENTRE_TOL) {
          const c = [...(item.color || [1, 1, 1, 1])];
          if (item.kind === 'nav') [c[0], c[1]] = [c[1], c[0]];
          const m = new THREE.Group();
          m.scale.x = -1;
          m.add(this.buildLight(item, c, std));
          g.add(m);
        }
        break;
      }
    }
    return g;
  }

  /** An instrument as the game builds it: in a frame facing local +z (12 o'clock up), turned into place. */
  buildInstrument(item, std) {
    const root = new THREE.Group();
    const p = item.position || [0, 0, 0];
    root.position.set(+p[0] || 0, +p[1] || 0, +p[2] || 0);
    const q = quatMul(item.rotation && item.rotation.length >= 4 ? item.rotation.map(Number) : [0, 0, 0, 1], FACE_UP);
    root.quaternion.set(q[0], q[1], q[2], q[3]).normalize();
    const size = Math.max(0.01, +item.size || 0.08), r = size / 2;
    const dark = () => std({ color: 0x141518, roughness: 0.6, metalness: 0.2 });
    const glow = (color, k = 0.6) => { const m = std({ color, roughness: 0.5 }); m.emissive.set(color).multiplyScalar(k); return this.shade(m); };
    const disc = (radius, z, mat) => {
      const d = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, 0.004, 32), mat);
      d.rotation.x = Math.PI / 2;      // the cylinder's axis along z
      d.position.z = z;
      return d;
    };
    const box = (w, h, d, mat, z = 0) => { const b = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat); b.position.z = z; return b; };
    switch (item.kind) {
      case 'attitude': {
        root.add(disc(r * 1.12, -0.004, std({ color: 0xc9ccd1, roughness: 0.35, metalness: 0.6 })));
        const half = (start, color) => { const h = new THREE.Mesh(new THREE.CircleGeometry(r, 32, start, Math.PI), glow(color, 0.35)); h.material.side = THREE.DoubleSide; h.position.z = 0.0005; return h; };
        root.add(half(0, 0x2f6fd0), half(Math.PI, 0x7a4b22));
        root.add(box(r * 0.9, r * 0.06, 0.002, glow(0xf2f2d9, 0.8), 0.002));
        break;
      }
      case 'screen':
        root.add(box(size * 1.1, size * 0.85, 0.01, dark(), -0.006), box(size, size * 0.75, 0.002, glow(0x0d3a17, 0.5)));
        break;
      case 'hud': {
        const m = glow(0x33ff59, 0.5);
        m.transparent = true; m.opacity = Math.min(m.opacity, 0.3); m.depthWrite = false; m.side = THREE.DoubleSide;
        root.add(box(size, size * 0.8, 0.002, m));
        break;
      }
      case 'mirror':
        root.add(box(size, size * 0.4, 0.006, std({ color: 0xd9e0e6, roughness: 0.08, metalness: 1 })));
        break;
      case 'lever': {
        const stem = new THREE.Mesh(new THREE.CylinderGeometry(0.005, 0.005, size, 10), dark());
        stem.rotation.x = Math.PI / 2;
        stem.position.z = size / 2;
        const knob = new THREE.Mesh(new THREE.SphereGeometry(0.016, 14, 10), std({ color: 0xe8e8dc, roughness: 0.5 }));
        knob.position.z = size;
        root.add(stem, knob);
        break;
      }
      default: {
        // Gauge or compass: a dark face in a light rim, the needle at its starting angle (clockwise from 12 o'clock).
        root.add(disc(r * 1.12, -0.004, std({ color: 0xc9ccd1, roughness: 0.35, metalness: 0.6 })), disc(r, -0.0015, dark()));
        const pivot = new THREE.Group();
        pivot.position.z = 0.001;
        // The game turns needles by +angle about the face normal (clockwise as the pilot sees it).
        pivot.rotation.z = (item.kind === 'compass' ? 0 : +item.min_deg || 0) * Math.PI / 180;
        const needle = box(r * 0.08, r * 0.85, 0.002, glow(0xf4f4ea, 0.8));
        needle.position.y = r * 0.4;
        pivot.add(needle);
        root.add(pivot);
      }
    }
    return root;
  }

  /** A lamp: a glowing ball `size` across with a soft halo (a strip is a thin glowing bar `size` long). */
  buildLight(item, color, std) {
    const c = color || [1, 1, 1, 1];
    const col = new THREE.Color().setRGB(clamp01(c[0]), clamp01(c[1]), clamp01(c[2]), THREE.SRGBColorSpace);
    const size = Math.max(0.01, +item.size || 0.1);
    const lit = () => { const m = std({ color: col, roughness: 0.4 }); m.emissive.copy(col); return this.shade(m); };
    const root = new THREE.Group();
    const p = item.position || [0, 0, 0];
    root.position.set(+p[0] || 0, +p[1] || 0, +p[2] || 0);
    const q = item.rotation && item.rotation.length >= 4 ? item.rotation.map(Number) : [0, 0, 0, 1];
    root.quaternion.set(q[0], q[1], q[2], q[3]).normalize();
    if (item.kind === 'strip') root.add(new THREE.Mesh(new THREE.BoxGeometry(size, 0.015, 0.04), lit()));
    else root.add(new THREE.Mesh(new THREE.SphereGeometry(size / 2, 18, 12), lit()));
    const halo = this.shade(new THREE.MeshStandardMaterial({ color: col, emissive: col, transparent: true, opacity: 0.18, depthWrite: false }));
    const hs = item.kind === 'strip' ? new THREE.Mesh(new THREE.BoxGeometry(size + 0.08, 0.09, 0.12), halo) : new THREE.Mesh(new THREE.SphereGeometry(size * 1.3, 18, 12), halo);
    root.add(hs);
    return root;
  }

  // ---------------------------------------------------------------- selection, visibility, shading

  setSelection(keys, active) {
    this.selected = new Set(keys || []);
    this.active = active || null;
    this.refreshHighlights();
    this.requestRender();
  }

  setHiddenLocked(hidden, locked) {
    this.hidden = new Set(hidden || []);
    this.locked = new Set(locked || []);
    this.applyVisibility();
    this.requestRender();
  }

  setEditKey(key) { this.editKey = key; this.refreshHighlights(); this.requestRender(); }

  applyVisibility() { for (const [key, it] of this.items) it.object.visible = !this.hidden.has(key); }

  refreshHighlights() {
    if (!this.colors) return;
    for (const [key, it] of this.items) {
      const editing = key === this.editKey;
      const level = editing ? 0 : key === this.active ? 0.55 : this.selected.has(key) ? 0.32 : 0;
      it.object.traverse(o => {
        if (!o.isMesh) return;
        for (const m of [].concat(o.material || [])) {
          if (!m?.emissive) continue;
          // A part's own glow (light colours, lamps, screens) stays, dimmed under the highlight.
          const base = Array.isArray(m.userData.baseEmissive) ? new THREE.Color().fromArray(m.userData.baseEmissive) : new THREE.Color(0, 0, 0);
          m.emissive.copy(level ? base.multiplyScalar(0.4).add(this.colors.red.clone().multiplyScalar(level)) : base);
        }
      });
    }
  }

  setShading({ wire, xray }) {
    this.shading = { wire: !!wire, xray: !!xray };
    // Rebuild everything so every material picks it up.
    for (const [, it] of this.items) { this.planeRoot.remove(it.object); this.disposeObject(it.object); }
    this.items.clear();
    if (this.spec) this.setPlane(this.spec);
    for (const m of this.overlayMaterials || []) m.depthTest = !this.shading.xray;
    this.requestRender();
  }

  // ---------------------------------------------------------------- analysis markers

  setAnalysis(a) {
    clearAndDispose(this.markers, new Set([checker]));
    if (!a?.flight) { this.requestRender(); return; }
    const size = Math.max(0.2, this.extent * 0.022);
    const cg = a.flight.center_of_mass;
    const cgMark = new THREE.Mesh(new THREE.SphereGeometry(size, 24, 16), new THREE.MeshBasicMaterial({ map: checkerTexture(), depthTest: false, transparent: true }));
    cgMark.position.set(cg[0], cg[1], cg[2]);
    cgMark.renderOrder = 10;
    this.markers.add(cgMark);
    if (a.flight.wing_area_m2 > 0) {
      const np = new THREE.Mesh(new THREE.ConeGeometry(size * 0.8, size * 1.8, 4), new THREE.MeshBasicMaterial({ color: this.colors.blue, depthTest: false, transparent: true }));
      np.rotation.x = Math.PI;
      np.position.set(0, cg[1] + size * 2.4, a.flight.neutral_point_z);
      np.renderOrder = 10;
      this.markers.add(np);
    }
    const ground = a.ground_y ?? (this.bounds ? this.bounds.min.y - 0.3 : 0);
    if (Math.abs(ground - this.groundY) > 1e-3) this.rebuildGround(this.extent * 3, ground);
    this.requestRender();
  }

  setMarkersVisible(on) { this.markers.visible = on; this.requestRender(); }

  // ---------------------------------------------------------------- edit-mode overlay

  /**
   * Draws the edit cage. o = { verts: [{ p, sel, act, hide }], edges: [{ a: p, b: p, sel }], faces: [{ c, sel }],
   * lines: [{ pts: [p...], closed, sel }] } in plane space.
   */
  setEditOverlay(o) {
    this.clearGroup(this.overlay, 'edit');
    if (!o) { this.requestRender(); return; }
    const g = new THREE.Group(); g.name = 'edit';
    const c = this.colors;
    // Like Blender: a dark wire for the cage, the signal colour for what's selected, white for the active element.
    const WIRE = [0.043, 0.043, 0.047], RED = [c.red.r, c.red.g, c.red.b], WHITE = [1, 1, 1];
    this.overlayMaterials = [];
    const onTop = !!o.onTop;
    const mat = (M, opts) => { const m = new M({ depthTest: !this.shading.xray && !onTop, transparent: true, ...opts }); this.overlayMaterials.push(m); return m; };
    const edges = o.edges || [];
    if (edges.length) {
      // Typed arrays filled in place: an edit cage can have tens of thousands of edges and is redrawn on every move.
      const pos = new Float32Array(edges.length * 6), col = new Float32Array(edges.length * 6);
      let i = 0;
      for (const e of edges) {
        const k = e.sel ? RED : WIRE;
        pos[i] = e.a[0]; pos[i + 1] = e.a[1]; pos[i + 2] = e.a[2]; pos[i + 3] = e.b[0]; pos[i + 4] = e.b[1]; pos[i + 5] = e.b[2];
        col[i] = col[i + 3] = k[0]; col[i + 1] = col[i + 4] = k[1]; col[i + 2] = col[i + 5] = k[2];
        i += 6;
      }
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
      const ls = new THREE.LineSegments(geo, mat(THREE.LineBasicMaterial, { vertexColors: true, opacity: 0.9 }));
      ls.renderOrder = 20;
      g.add(ls);
    }
    for (const l of o.lines || []) {
      const pts = l.pts.map(p => new THREE.Vector3(p[0], p[1], p[2]));
      if (l.closed && pts.length) pts.push(pts[0].clone());
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat(THREE.LineBasicMaterial, { color: l.sel ? c.red : new THREE.Color(...WIRE), opacity: l.sel ? 1 : 0.8 }));
      line.renderOrder = 20;
      g.add(line);
    }
    const points = (list, size, colorOf) => {
      const shown = (list || []).filter(v => !v.hide);
      if (!shown.length) return;
      const pos = new Float32Array(shown.length * 3), col = new Float32Array(shown.length * 3);
      shown.forEach((v, j) => { const k = colorOf(v); pos[j * 3] = v.p[0]; pos[j * 3 + 1] = v.p[1]; pos[j * 3 + 2] = v.p[2]; col[j * 3] = k[0]; col[j * 3 + 1] = k[1]; col[j * 3 + 2] = k[2]; });
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
      const pts = new THREE.Points(geo, mat(THREE.PointsMaterial, { size, sizeAttenuation: false, vertexColors: true }));
      pts.renderOrder = 21;
      g.add(pts);
    };
    points(o.verts, 7, v => (v.act ? WHITE : v.sel ? RED : WIRE));
    points((o.faces || []).map(f => ({ p: f.c, sel: f.sel })), 6, v => (v.sel ? RED : WIRE));
    this.overlay.add(g);
    this.requestRender();
  }

  /** A thin polyline preview (loop cut, measuring), plane space. */
  setPreviewLine(pts, closed = false, color = null) {
    this.clearGroup(this.overlay, 'preview');
    if (pts?.length) {
      const v = pts.map(p => new THREE.Vector3(p[0], p[1], p[2]));
      if (closed) v.push(v[0].clone());
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(v), new THREE.LineBasicMaterial({ color: color || this.colors.warn, depthTest: false, transparent: true }));
      line.renderOrder = 30; line.name = 'preview';
      this.overlay.add(line);
    }
    this.requestRender();
  }

  /** The proportional-editing radius: a circle facing the camera around a plane-space centre. */
  setPropCircle(center, radius) {
    this.clearGroup(this.scene, 'prop');
    if (center && radius > 0) {
      const pts = [];
      for (let k = 0; k <= 64; k++) { const t = k / 64 * Math.PI * 2; pts.push(new THREE.Vector3(Math.cos(t) * radius, Math.sin(t) * radius, 0)); }
      const ring = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), new THREE.LineBasicMaterial({ color: this.colors.text, depthTest: false, transparent: true, opacity: 0.8 }));
      ring.name = 'prop'; ring.renderOrder = 31;
      ring.position.copy(u2w(center));
      ring.quaternion.copy(this.activeRig.camera.quaternion);
      this.scene.add(ring);
    }
    this.requestRender();
  }

  /** The measurement overlay: a box around the selection with its length, span and height. */
  setMeasure(box) {
    this.clearGroup(this.overlay, 'measure');
    for (const l of this.labels.filter(l => l.measure)) l.el.remove();
    this.labels = this.labels.filter(l => !l.measure);
    if (box) {
      const [a, b] = box;
      const geo = new THREE.EdgesGeometry(new THREE.BoxGeometry(b[0] - a[0] || 1e-3, b[1] - a[1] || 1e-3, b[2] - a[2] || 1e-3));
      const lines = new THREE.LineSegments(geo, new THREE.LineDashedMaterial({ color: this.colors.warn, dashSize: 0.2, gapSize: 0.12, depthTest: false, transparent: true }));
      lines.computeLineDistances();
      lines.position.set((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2);
      lines.name = 'measure'; lines.renderOrder = 25;
      this.overlay.add(lines);
      const lab = (text, p) => {
        const el = document.createElement('div');
        el.className = 'measure-label';
        el.textContent = text;
        this.container.append(el);
        this.labels.push({ el, p, measure: true });
      };
      const f = v => `${(Math.round(v * 100) / 100).toFixed(2)} m`;
      lab(`span ${f(b[0] - a[0])}`, [(a[0] + b[0]) / 2, a[1], b[2]]);
      lab(`length ${f(b[2] - a[2])}`, [b[0], a[1], (a[2] + b[2]) / 2]);
      lab(`height ${f(b[1] - a[1])}`, [b[0], (a[1] + b[1]) / 2, b[2]]);
    }
    this.requestRender();
  }

  clearGroup(parent, name) {
    for (const o of [...parent.children]) if (o.name === name) { parent.remove(o); this.disposeObject(o); }
  }

  // ---------------------------------------------------------------- reference images

  setReferences(refs) {
    this.refTextures ||= new Map();
    const sig = JSON.stringify(refs) + '|' + (this.bounds ? [this.bounds.min.toArray(), this.bounds.max.toArray()].join() : '');
    if (sig === this.refSig) return;
    this.refSig = sig;
    clearAndDispose(this.refGroup, new Set(this.refTextures.values()));
    // Pictures no reference uses any more are freed (their GPU copy too).
    const usedImages = new Set(refs.map(r => r.image));
    for (const [img, tex] of this.refTextures) if (!usedImages.has(img)) { tex.dispose(); this.refTextures.delete(img); }
    const b = this.bounds || new THREE.Box3(new THREE.Vector3(-5, -2, -6), new THREE.Vector3(5, 2, 6));
    const margin = Math.max(0.5, this.extent * 0.15);
    for (const r of refs) {
      if (!r.visible || !r.image) continue;
      let tex = this.refTextures.get(r.image);
      if (!tex) {
        tex = new THREE.TextureLoader().load(`/api/references/${encodeURIComponent(r.image)}`, t => {
          t.userData.aspect = t.image.height / t.image.width;
          this.refSig = null; this.setReferences(this.spec?.references || []); this.requestRender();
        });
        tex.colorSpace = THREE.SRGBColorSpace;
        this.refTextures.set(r.image, tex);
      }
      const aspect = tex.userData.aspect || 0.5;
      const w = Math.max(0.1, +r.width_m || 10), h = w * aspect;
      const mat = new THREE.MeshBasicMaterial({ map: tex, transparent: true, opacity: Math.max(0.05, Math.min(1, +r.opacity || 0.5)), side: THREE.DoubleSide, depthWrite: false });
      const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, h), mat);
      const c = r.center || [0, 0];
      // Plane-space basis (u = image right, v = image up, n = towards the camera), then into world space.
      // World bounds: plane x is world -x, so the plane's extent across x is max(|min.x|, |max.x|).
      const halfX = Math.max(Math.abs(b.min.x), Math.abs(b.max.x));
      let u, v, n, at;
      if (r.view === 'side') { u = [0, 0, 1]; v = [0, 1, 0]; n = [1, 0, 0]; at = [-halfX - margin, c[1], c[0]]; }
      else if (r.view === 'front') { u = [-1, 0, 0]; v = [0, 1, 0]; n = [0, 0, 1]; at = [c[0], c[1], b.min.z - margin]; }
      else { u = [1, 0, 0]; v = [0, 0, 1]; n = [0, 1, 0]; at = [c[0], b.min.y - margin, c[1]]; }
      mesh.quaternion.setFromRotationMatrix(new THREE.Matrix4().makeBasis(u2w(u), u2w(v), u2w(n)));
      mesh.rotateZ((+r.rotation_deg || 0) * Math.PI / 180);
      if (r.flip) mesh.scale.x = -1;
      mesh.position.copy(u2w(at));
      mesh.renderOrder = -5;
      mesh.userData = { refId: r.id, view: r.view || 'top', locked: !!r.locked };
      this.refGroup.add(mesh);
    }
    this.requestRender();
  }

  /** References show in views that look straight along their axis (like Blender's background images). */
  updateReferenceVisibility(rig) {
    const d = rig.offsetDir();
    const dirOf = { top: new THREE.Vector3(0, 1, 0), side: new THREE.Vector3(-1, 0, 0), front: new THREE.Vector3(0, 0, 1) };
    for (const m of this.refGroup.children) m.visible = d.dot(dirOf[m.userData.view] || dirOf.top) > 0.965;
  }

  /** The reference image (id) under the cursor in an aligned view, and the plane point hit. */
  pickReference(clientX, clientY) {
    const vp = this.viewportAt(clientX, clientY);
    this.updateReferenceVisibility(vp.rig);
    const ray = this.raycaster(clientX, clientY, vp);
    const hits = ray.intersectObjects(this.refGroup.children.filter(m => m.visible), false);
    return hits.length ? { id: hits[0].object.userData.refId, locked: hits[0].object.userData.locked, point: w2u(hits[0].point) } : null;
  }

  // ---------------------------------------------------------------- picking

  raycaster(clientX, clientY, vp = this.viewportAt(clientX, clientY)) {
    const rect = this.canvas.getBoundingClientRect();
    const x = clientX - rect.left - vp.x, y = clientY - rect.top - vp.y;
    const ndc = new THREE.Vector2((x / vp.w) * 2 - 1, -(y / vp.h) * 2 + 1);
    const ray = new THREE.Raycaster();
    ray.params.Line.threshold = 0.05;
    ray.setFromCamera(ndc, vp.rig.camera);
    return ray;
  }

  /** The part under the cursor: its key, or null. Hidden and locked parts are skipped. */
  pickObject(clientX, clientY) {
    const ray = this.raycaster(clientX, clientY);
    const fromSeat = this.viewportAt(clientX, clientY).rig.cockpit;   // the seat marker isn't drawn from the pilot's eye
    const objs = [...this.items.entries()].filter(([k]) => !this.hidden.has(k) && !this.locked.has(k) && !(fromSeat && k === 'cockpit:cockpit')).map(([, it]) => it.object);
    const hits = ray.intersectObjects(objs, true);
    for (const h of hits) {
      let o = h.object;
      while (o && !o.userData?.key) o = o.parent;
      if (o) return { key: o.userData.key, point: w2u(h.point), distance: h.distance };
    }
    return null;
  }

  /** Distance from the camera to the first visible surface along the ray through a point (for occlusion tests). */
  surfaceDistance(clientX, clientY, onlyKey = null) {
    const ray = this.raycaster(clientX, clientY);
    const objs = onlyKey ? [this.items.get(onlyKey)?.object].filter(Boolean) : [...this.items.values()].map(it => it.object);
    const hits = ray.intersectObjects(objs, true);
    return hits.length ? hits[0].distance : Infinity;
  }

  /** Camera position (plane space) of the viewport under a point, for occlusion tests. */
  cameraPlanePos(clientX, clientY) { return w2u(this.viewportAt(clientX, clientY).rig.camera.position); }

  /** Where a ray under the cursor meets the plane through `point` with `normal` (both plane space), or null. */
  planeHit(clientX, clientY, point, normal, vp = null) {
    const ray = this.raycaster(clientX, clientY, vp || this.viewportAt(clientX, clientY));
    const n = u2w(normal).normalize();
    const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(n, u2w(point));
    const hit = ray.ray.intersectPlane(plane, new THREE.Vector3());
    return hit ? w2u(hit) : null;
  }

  /** The camera's looking direction in plane space for a viewport (default: the active one). */
  viewDir(rig = this.activeRig) { const f = rig.basis().forward; return [-f.x, f.y, f.z]; }

  /** Keys of parts whose bounding-box centre falls in a client-space rectangle. */
  objectsInRect(r, vp) {
    const out = [];
    for (const [key, it] of this.items) {
      if (this.hidden.has(key) || this.locked.has(key)) continue;
      const box = new THREE.Box3().expandByObject(it.object);
      if (box.isEmpty()) continue;
      const c = w2u(box.getCenter(new THREE.Vector3()));
      const s = this.projectIn(c, vp);
      if (s.visible && s.cx >= r.x0 && s.cx <= r.x1 && s.cy >= r.y0 && s.cy <= r.y1) out.push(key);
    }
    return out;
  }

  /** Projection to client coordinates for a viewport. */
  projectIn(p, vp) {
    const rect = this.canvas.getBoundingClientRect();
    const s = this.project(p, vp);
    return { cx: rect.left + s.x, cy: rect.top + s.y, visible: s.visible, z: s.z };
  }

  /** The world box (plane space [min, max]) of parts, or of everything visible. */
  boxOf(keys = null) {
    const box = new THREE.Box3();
    for (const [key, it] of this.items) if ((!keys || keys.includes(key)) && !this.hidden.has(key)) box.expandByObject(it.object);
    if (box.isEmpty()) return null;
    const a = w2u(box.min), b = w2u(box.max);
    return [[Math.min(a[0], b[0]), a[1], a[2]], [Math.max(a[0], b[0]), b[1], b[2]]];
  }

  // ---------------------------------------------------------------- camera commands

  setView(name) { this.activeRig.setView(name); this.cb.onViewChange?.(); this.requestRender(); }

  /** Cockpit view: the camera at the pilot's eye (plane space), looking forward. Any other view leaves it. */
  setCockpitView(eye) {
    this.activeRig.enterCockpit(u2w(eye));
    this.navDirty = true;
    this.cb.onViewChange?.();
    this.requestRender();
  }
  toggleOrtho() { this.activeRig.toggleOrtho(); this.cb.onViewChange?.(); this.requestRender(); }

  /** Frames a plane-space box (or everything when null) in the active view; all quad views follow. */
  frameBox(box) {
    let center, radius;
    if (box) {
      const b = new THREE.Box3(u2w(box[0]), u2w(box[0])).expandByPoint(u2w(box[1]));
      const s = b.getBoundingSphere(new THREE.Sphere());
      center = s.center; radius = Math.max(0.3, s.radius);
    } else {
      const b = this.bounds || new THREE.Box3(new THREE.Vector3(-5, -2, -6), new THREE.Vector3(5, 2, 6));
      const s = b.getBoundingSphere(new THREE.Sphere());
      center = s.center; radius = s.radius;
    }
    const rigs = this.quad ? [this.rig, ...this.quadRigs] : [this.activeRig];
    for (const r of rigs) r.frameSphere(center, radius * 0.92);
    this.cb.onViewChange?.();
    this.requestRender();
  }

  // ---------------------------------------------------------------- gizmo

  /** Shows the move/rotate/scale gizmo at a plane point (mode null hides it). */
  setGizmo(mode, at) {
    if (!mode || !at) {
      this.gizmo.enabled = false; this.gizmo.visible = false;
      if (this.gizmo.object) this.gizmo.detach();
      this.requestRender();
      return;
    }
    if (this.gizmo.dragging) return;
    this.gizmoProxy.position.copy(u2w(at));
    this.gizmoProxy.quaternion.identity();
    this.gizmoProxy.scale.set(1, 1, 1);
    this.gizmo.setMode(mode);
    if (this.gizmo.object !== this.gizmoProxy) this.gizmo.attach(this.gizmoProxy);
    this.gizmo.enabled = true; this.gizmo.visible = true;
    this.requestRender();
  }

  setGizmoSnap(on) {
    this.gizmo.setTranslationSnap(on ? 0.1 : null);
    this.gizmo.setRotationSnap(on ? THREE.MathUtils.degToRad(5) : null);
    this.gizmo.setScaleSnap(on ? 0.1 : null);
  }

  // ---------------------------------------------------------------- navigation gizmo (top right)

  buildNavGizmo() {
    const wrap = document.createElement('div');
    wrap.className = 'vp-overlay vp-nav';
    wrap.innerHTML = `<canvas width="192" height="192" aria-label="View gizmo: click an axis for that view, drag to orbit" role="button" tabindex="-1"></canvas>
      <button type="button" class="navbtn" data-nav="pan" title="Drag to pan (Shift+middle mouse, or right-drag)" aria-label="Pan the view">${icon('pan')}</button>
      <button type="button" class="navbtn" data-nav="zoom" title="Drag to zoom (mouse wheel)" aria-label="Zoom the view">${icon('search')}</button>
      <button type="button" class="navbtn" data-nav="ortho" title="Perspective / orthographic (Numpad 5)" aria-label="Switch perspective and orthographic">${icon('grid')}</button>`;
    this.container.append(wrap);
    this.navWrap = wrap;
    this.navCanvas = wrap.querySelector('canvas');
    const drag = (el, fn) => el.addEventListener('pointerdown', e => {
      e.preventDefault(); e.stopPropagation();
      el.setPointerCapture(e.pointerId);
      let lx = e.clientX, ly = e.clientY, moved = 0;
      const mv = ev => { const dx = ev.clientX - lx, dy = ev.clientY - ly; lx = ev.clientX; ly = ev.clientY; moved += Math.abs(dx) + Math.abs(dy); fn(dx, dy); this.cb.onViewChange?.(); this.requestRender(); };
      const up = ev => { el.removeEventListener('pointermove', mv); el.removeEventListener('pointerup', up); el.releasePointerCapture?.(ev.pointerId); if (moved < 3) el.dispatchEvent(new CustomEvent('navclick', { detail: ev })); };
      el.addEventListener('pointermove', mv); el.addEventListener('pointerup', up);
    });
    drag(this.navCanvas, (dx, dy) => this.activeRig.orbit(dx, dy));
    this.navCanvas.addEventListener('navclick', e => {
      const ev = e.detail, rect = this.navCanvas.getBoundingClientRect();
      const x = (ev.clientX - rect.left) * 2, y = (ev.clientY - rect.top) * 2;
      const hit = (this.navHits || []).find(h => Math.hypot(h.x - x, h.y - y) < 22);
      if (hit) this.setView(hit.view);
    });
    drag(wrap.querySelector('[data-nav=pan]'), (dx, dy) => this.activeRig.pan(dx, dy, this.size?.h || 600));
    drag(wrap.querySelector('[data-nav=zoom]'), (dx, dy) => this.activeRig.zoom(Math.exp(dy * 0.01)));
    wrap.querySelector('[data-nav=ortho]').addEventListener('click', () => this.toggleOrtho());
  }

  drawNav() {
    const ctx = this.navCanvas.getContext('2d');
    const rig = this.activeRig;
    this.navRigView = rig.yaw + ':' + rig.pitch;
    this.navDirty = false;
    const { right, up, back } = rig.basis();
    ctx.clearRect(0, 0, 192, 192);
    ctx.fillStyle = 'rgba(128,128,128,0.10)';
    ctx.beginPath(); ctx.arc(96, 96, 92, 0, Math.PI * 2); ctx.fill();
    const c = this.colors;
    const axes = [
      { d: [-1, 0, 0], col: c.redCss || '#FF4538', label: 'X', view: 'right', nview: 'left' },   // plane +x = world -x
      { d: [0, 1, 0], col: '#7ccf7a', label: 'Y', view: 'top', nview: 'bottom' },
      { d: [0, 0, 1], col: '#5B9BE6', label: 'Z', view: 'front', nview: 'back' },
    ];
    const items = [];
    for (const a of axes) {
      const v = new THREE.Vector3(...a.d);
      const sx = v.dot(right), sy = v.dot(up), depth = v.dot(back);
      items.push({ x: 96 + sx * 62, y: 96 - sy * 62, depth, a, pos: true });
      items.push({ x: 96 - sx * 62, y: 96 + sy * 62, depth: -depth, a, pos: false });
    }
    items.sort((p, q) => p.depth - q.depth);
    this.navHits = [];
    for (const it of items) {
      if (it.pos) { ctx.strokeStyle = it.a.col; ctx.lineWidth = 4; ctx.beginPath(); ctx.moveTo(96, 96); ctx.lineTo(it.x, it.y); ctx.stroke(); }
      ctx.beginPath(); ctx.arc(it.x, it.y, it.pos ? 17 : 13, 0, Math.PI * 2);
      if (it.pos) { ctx.fillStyle = it.a.col; ctx.fill(); }
      else { ctx.fillStyle = 'rgba(0,0,0,0.35)'; ctx.fill(); ctx.strokeStyle = it.a.col; ctx.lineWidth = 3; ctx.stroke(); }
      if (it.pos) { ctx.fillStyle = '#000'; ctx.font = 'bold 20px Bahnschrift, Segoe UI, sans-serif'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(it.a.label, it.x, it.y + 1); }
      this.navHits.push({ x: it.x, y: it.y, view: it.pos ? it.a.view : it.a.nview });
    }
  }

  // ---------------------------------------------------------------- pointer: navigation here, the rest via callbacks

  setupPointer() {
    const el = this.canvas;
    let nav = null;        // { mode: 'orbit' | 'pan', x, y, button, rig, moved }
    let rightDown = null;
    el.addEventListener('contextmenu', e => e.preventDefault());

    el.addEventListener('pointerdown', e => {
      el.focus({ preventScroll: true });
      const vp = this.viewportAt(e.clientX, e.clientY);
      this.focusRig = vp.rig;
      if (this.gizmo.enabled && this.gizmo.axis) return;          // the gizmo takes it
      if (this.cb.onPointerDown?.(e, vp)) { e.preventDefault(); return; }
      const orbit = e.button === 1 && !e.shiftKey && !e.ctrlKey || e.button === 0 && e.altKey && !e.shiftKey;
      const pan = e.button === 1 && e.shiftKey || e.button === 0 && e.altKey && e.shiftKey;
      const zoom = e.button === 1 && e.ctrlKey;
      if (orbit || pan || zoom) {
        e.preventDefault();
        el.setPointerCapture(e.pointerId);
        nav = { mode: zoom ? 'zoom' : pan ? 'pan' : 'orbit', x: e.clientX, y: e.clientY, rig: vp.rig, vp };
        return;
      }
      if (e.button === 2) {
        el.setPointerCapture(e.pointerId);
        rightDown = { x: e.clientX, y: e.clientY, lx: e.clientX, ly: e.clientY, moved: 0, rig: vp.rig, vp };
        return;
      }
      if (e.button === 0) { el.setPointerCapture(e.pointerId); this.cb.onLeftDown?.(e, vp); }
    });

    el.addEventListener('pointermove', e => {
      if (nav) {
        const dx = e.clientX - nav.x, dy = e.clientY - nav.y;
        nav.x = e.clientX; nav.y = e.clientY;
        if (nav.mode === 'orbit') nav.rig.orbit(dx, dy);
        else if (nav.mode === 'pan') nav.rig.pan(dx, dy, nav.vp.h);
        else nav.rig.zoom(Math.exp(dy * 0.01));
        this.cb.onViewChange?.();
        this.requestRender();
        return;
      }
      if (rightDown) {
        const dx = e.clientX - rightDown.lx, dy = e.clientY - rightDown.ly;
        rightDown.lx = e.clientX; rightDown.ly = e.clientY;
        rightDown.moved += Math.abs(dx) + Math.abs(dy);
        if (rightDown.moved > 4) { rightDown.rig.pan(dx, dy, rightDown.vp.h); this.cb.onViewChange?.(); this.requestRender(); }
        return;
      }
      if (this.quad && !this.gizmo.dragging) {
        const vp = this.viewportAt(e.clientX, e.clientY);
        if (this.gizmoRig !== vp.rig) { this.gizmoRig = vp.rig; this.requestRender(); }
      }
      this.cb.onPointerMove?.(e);
    });

    const end = e => {
      if (nav) { nav = null; el.releasePointerCapture?.(e.pointerId); return; }
      if (rightDown) {
        const r = rightDown; rightDown = null;
        el.releasePointerCapture?.(e.pointerId);
        if (r.moved <= 4) this.cb.onRightClick?.(e);
        return;
      }
      this.cb.onPointerUp?.(e);
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);

    el.addEventListener('wheel', e => {
      e.preventDefault();
      if (this.cb.onWheel?.(e)) return;
      const vp = this.viewportAt(e.clientX, e.clientY);
      this.focusRig = vp.rig;
      const factor = Math.exp(Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY), 120) * 0.0016);
      vp.rig.zoom(factor);
      this.cb.onViewChange?.();
      this.requestRender();
    }, { passive: false });
  }
}

// ---------------------------------------------------------------- helpers

// A part's signature for 'did it change?': its JSON, with a shape's (possibly huge) vertex and face lists stood in for by
// the identity of the arrays. Edits always put new arrays there, so a new array means new geometry.
const arrayIds = new WeakMap();
let nextArrayId = 1;
function signature(item) {
  if (!Array.isArray(item.vertices)) return JSON.stringify(item);
  const id = a => { if (!Array.isArray(a)) return 0; let n = arrayIds.get(a); if (!n) { n = nextArrayId++; arrayIds.set(a, n); } return n; };
  return JSON.stringify(item, (k, val) => (k === 'vertices' || k === 'faces' ? `#${id(val)}` : val));
}

/** A thin slab (box) between four corners, `t` thick as a fraction of the local chord, along `up`. */
function slab(out, rl, rt, tt, tl, up, t) {
  const rc = Math.hypot(rt[0] - rl[0], rt[1] - rl[1], rt[2] - rl[2]);
  const tc = Math.hypot(tt[0] - tl[0], tt[1] - tl[1], tt[2] - tl[2]);
  const hr = Math.min(0.35, rc * t) / 2, ht = Math.min(0.35, tc * t) / 2;
  const o = (p, h) => [p[0] + up[0] * h, p[1] + up[1] * h, p[2] + up[2] * h];
  const top = [o(rl, hr), o(rt, hr), o(tt, ht), o(tl, ht)];
  const bot = [o(rl, -hr), o(rt, -hr), o(tt, -ht), o(tl, -ht)];
  const quad = (a, b, c, d) => out.push(a, b, c, a, c, d);
  quad(top[0], top[1], top[2], top[3]);
  quad(bot[3], bot[2], bot[1], bot[0]);
  quad(top[0], top[3], bot[3], bot[0]);
  quad(top[1], bot[1], bot[2], top[2]);
  quad(top[0], bot[0], bot[1], top[1]);
  quad(top[3], top[2], bot[2], bot[3]);
}

function slabMesh(points, material) {
  const arr = new Float32Array(points.length * 3);
  points.forEach((p, i) => { arr[i * 3] = p[0]; arr[i * 3 + 1] = p[1]; arr[i * 3 + 2] = p[2]; });
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(arr, 3));
  geo.computeVertexNormals();
  material.side = THREE.DoubleSide;
  material.flatShading = true;
  return new THREE.Mesh(geo, material);
}

let checker = null;
function checkerTexture() {
  if (checker) return checker;
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  g.fillStyle = '#ffffff'; g.fillRect(0, 0, 64, 64);
  g.fillStyle = '#111111'; g.fillRect(0, 0, 32, 32); g.fillRect(32, 32, 32, 32);
  checker = new THREE.CanvasTexture(c);
  checker.colorSpace = THREE.SRGBColorSpace;
  return checker;
}

export { VIEWS, CENTRE_TOL, meshCenter };

/** Removes every child of a group and frees what they hold on the GPU: geometries, materials and textures (except shared ones). */
function clearAndDispose(group, keepTextures = new Set()) {
  for (const child of [...group.children]) {
    child.traverse(o => {
      o.geometry?.dispose?.();
      for (const m of [].concat(o.material || [])) {
        if (m.map && !keepTextures.has(m.map)) m.map.dispose();
        m.dispose();
      }
    });
  }
  group.clear();
}
