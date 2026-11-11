// A turntable camera like Blender's: orbit around a target, pan in the screen plane, zoom, perspective or orthographic,
// and the numbered views. World space is the three.js scene; the plane's own x is world -x (see viewport.js).
import * as THREE from 'three';

const DEG = Math.PI / 180;
const FOV = 40, COCKPIT_FOV = 70;

/** Named views: yaw/pitch in degrees (camera offset = (cos p sin y, sin p, cos p cos y) in world space). */
export const VIEWS = {
  front: { yaw: 0, pitch: 0, label: 'Front' },
  back: { yaw: 180, pitch: 0, label: 'Back' },
  right: { yaw: -90, pitch: 0, label: 'Right' },
  left: { yaw: 90, pitch: 0, label: 'Left' },
  top: { yaw: 180, pitch: 90, label: 'Top' },
  bottom: { yaw: 180, pitch: -90, label: 'Bottom' },
  user: { yaw: -43, pitch: 24, label: 'User' },
};

export class CameraRig {
  constructor() {
    this.persp = new THREE.PerspectiveCamera(FOV, 1, 0.05, 5000);
    this.ortho = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.01, 10000);
    this.target = new THREE.Vector3();
    this.yaw = VIEWS.user.yaw;
    this.pitch = VIEWS.user.pitch;
    this.dist = 20;
    this.orthographic = false;
    this.autoOrtho = false;   // switched to ortho by a numbered view; orbiting switches back to perspective
    this.view = 'user';       // name of an aligned view, or 'user'
    this.aspect = 1;
    this.sceneSize = 20;
    // Cockpit view: the camera sits at the pilot's eye (world space) and turns about it; null otherwise.
    this.cockpit = false;
    this.eye = new THREE.Vector3();
    this.beforeCockpit = null;
    this.update();
  }

  /** Puts the camera at the pilot's eye (a world point), looking forward along the plane. */
  enterCockpit(eyeWorld) {
    if (!this.cockpit) {
      this.beforeCockpit = { target: this.target.clone(), dist: this.dist, yaw: this.yaw, pitch: this.pitch, orthographic: this.orthographic, autoOrtho: this.autoOrtho, view: this.view };
    }
    this.cockpit = true;
    this.eye.copy(eyeWorld);
    this.yaw = 180; this.pitch = 0;          // offset (0, 0, -1): the camera looks along +z, the nose
    this.dist = 0.05;
    this.orthographic = false; this.autoOrtho = false;
    this.view = 'cockpit';
    this.persp.fov = COCKPIT_FOV;
    this.update();
  }

  /** Back to the view from before the cockpit view (any other view button does this). */
  leaveCockpit() {
    if (!this.cockpit) return;
    this.cockpit = false;
    this.persp.fov = FOV;
    const b = this.beforeCockpit;
    this.beforeCockpit = null;
    if (b) { this.target.copy(b.target); this.dist = b.dist; this.yaw = b.yaw; this.pitch = b.pitch; this.orthographic = b.orthographic; this.autoOrtho = b.autoOrtho; this.view = b.view; }
    else this.view = 'user';
    this.update();
  }

  get camera() { return this.orthographic ? this.ortho : this.persp; }

  /** Unit vector from the target to the camera (world). */
  offsetDir() {
    const y = this.yaw * DEG, p = this.pitch * DEG;
    return new THREE.Vector3(Math.cos(p) * Math.sin(y), Math.sin(p), Math.cos(p) * Math.cos(y));
  }

  basis() {
    const back = this.offsetDir();
    const forward = back.clone().negate();
    const y = this.yaw * DEG;
    const right = new THREE.Vector3(Math.cos(y), 0, -Math.sin(y));
    const up = new THREE.Vector3().crossVectors(right, forward).normalize();
    return { forward, right, up, back };
  }

  setAspect(a) { this.aspect = Math.max(0.05, a); this.update(); }

  update() {
    const { right, up, back } = this.basis();
    // In the cockpit the eye stays put and the view turns about it.
    if (this.cockpit) this.target.copy(this.eye).addScaledVector(back, -this.dist);
    const m = new THREE.Matrix4().makeBasis(right, up, back);
    const half = Math.tan(this.persp.fov * DEG / 2);
    this.persp.aspect = this.aspect;
    this.persp.position.copy(this.target).addScaledVector(back, this.dist);
    this.persp.quaternion.setFromRotationMatrix(m);
    this.persp.near = Math.max(0.01, this.dist / 800);
    this.persp.far = Math.max(this.dist * 60, this.sceneSize * 20);
    this.persp.updateProjectionMatrix();
    this.persp.updateMatrixWorld();
    const h = this.dist * half;
    const back2 = Math.max(this.dist, this.sceneSize) * 3 + 50;
    this.ortho.left = -h * this.aspect; this.ortho.right = h * this.aspect; this.ortho.top = h; this.ortho.bottom = -h;
    this.ortho.position.copy(this.target).addScaledVector(back, back2);
    this.ortho.quaternion.setFromRotationMatrix(m);
    this.ortho.near = 0.01;
    this.ortho.far = back2 * 2 + this.sceneSize * 4;
    this.ortho.updateProjectionMatrix();
    this.ortho.updateMatrixWorld();
  }

  /** Metres per screen pixel at the target, for a viewport `heightPx` tall. */
  unitsPerPixel(heightPx) { return (2 * this.dist * Math.tan(this.persp.fov * DEG / 2)) / Math.max(1, heightPx); }

  orbit(dxPx, dyPx) {
    this.yaw -= dxPx * 0.4;
    this.pitch = Math.max(-90, Math.min(90, this.pitch + dyPx * 0.4));
    if (!this.cockpit) this.view = 'user';
    if (this.autoOrtho) { this.orthographic = false; this.autoOrtho = false; }
    this.update();
  }

  pan(dxPx, dyPx, heightPx) {
    const { right, up } = this.basis();
    // In the cockpit, panning moves the head a little (3 mm per pixel).
    const k = this.cockpit ? 0.003 : this.unitsPerPixel(heightPx);
    (this.cockpit ? this.eye : this.target).addScaledVector(right, -dxPx * k).addScaledVector(up, dyPx * k);
    this.update();
  }

  /** Zoom by a factor (< 1 moves in). With a world point, keeps that point under the cursor (zoom to mouse). */
  zoom(factor, toward = null) {
    if (this.cockpit) {
      // In the cockpit, zooming narrows or widens the field of view.
      this.persp.fov = Math.max(20, Math.min(100, this.persp.fov * factor));
      this.update();
      return;
    }
    const before = this.dist;
    this.dist = Math.max(0.05, Math.min(5000, this.dist * factor));
    if (toward) this.target.lerp(toward, 1 - this.dist / before);
    this.update();
  }

  setView(name) {
    this.leaveCockpit();
    const v = VIEWS[name] || VIEWS.user;
    this.yaw = v.yaw; this.pitch = v.pitch;
    this.view = name in VIEWS && name !== 'user' ? name : 'user';
    if (this.view !== 'user') { this.orthographic = true; this.autoOrtho = true; }
    this.update();
  }

  toggleOrtho() {
    if (this.cockpit) { this.leaveCockpit(); return; }
    this.orthographic = !this.orthographic;
    this.autoOrtho = false;
    this.update();
  }

  /** Looks at a sphere so it fills the view. */
  frameSphere(center, radius) {
    if (this.cockpit) { this.leaveCockpit(); if (this.view === 'cockpit') this.view = 'user'; }
    this.target.copy(center);
    const half = this.persp.fov * DEG / 2;
    const fitH = radius / Math.sin(half);
    const fitW = radius / Math.sin(Math.atan(Math.tan(half) * this.aspect));
    this.dist = Math.max(0.5, Math.max(fitH, fitW) * 1.08);
    this.update();
  }

  label() {
    if (this.cockpit) return 'Cockpit view';
    const v = VIEWS[this.view];
    return `${v && this.view !== 'user' ? v.label : 'User'} ${this.orthographic ? 'Orthographic' : 'Perspective'}`;
  }

  copyFrom(o) {
    this.target.copy(o.target); this.yaw = o.yaw; this.pitch = o.pitch; this.dist = o.dist;
    this.orthographic = o.orthographic; this.autoOrtho = o.autoOrtho; this.view = o.view;
    this.cockpit = o.cockpit; this.eye.copy(o.eye); this.beforeCockpit = o.beforeCockpit; this.persp.fov = o.persp.fov;
    this.update();
  }
}
