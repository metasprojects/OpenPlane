// Modal transforms like Blender's G / R / S: the mouse drives the change, X / Y / Z lock an axis (Shift locks the other
// two), typing a number sets an exact value, Ctrl snaps, Enter or a left click confirms, Esc or a right click cancels.
// Also the one-number drags (extrude and inset amounts, sweep, dihedral, twist, surface slides).
import { vec, angleAxis } from './geometry.js';
import { u2w, w2u } from './viewport.js';

const AXES = { x: [1, 0, 0], y: [0, 1, 0], z: [0, 0, 1] };
const LABEL = { translate: 'Move', rotate: 'Rotate', scale: 'Scale', slide: 'Slide', param: 'Adjust' };

/**
 * ctx: { vp, type, pivot (plane), x, y (pointer start, client px), apply(op) -> note?, restore(), done(confirmed),
 *        readout (element), label?, snapStep?, slide?: { dir (plane), max? , mode: 'extrude' | 'inset' },
 *        param?: { label, unit, value, perPx, min, max, step, set(v) }, onWheel?(e) }
 */
export class Modal {
  constructor(ctx) {
    this.ctx = ctx;
    this.vp = ctx.vp;
    this.type = ctx.type;
    this.axis = ctx.axis || null;    // 'x' | 'y' | 'z'
    this.planeLock = false;
    this.typed = '';
    this.snapHeld = false;
    this.snapToggle = !!ctx.snap;
    this.vpRect = this.vp.viewportAt(ctx.x, ctx.y);
    this.rig = this.vpRect.rig;
    this.start = { x: ctx.x, y: ctx.y };
    this.mouse = { x: ctx.x, y: ctx.y };
    this.pivot = ctx.pivot || [0, 0, 0];
    this.startHit = null;
    this.note = null;
    this.update();
  }

  get snapping() { return this.snapHeld !== this.snapToggle; }

  pivotScreen() { return this.vp.projectIn(this.pivot, this.vpRect); }

  /** Closest point on the line through the pivot along `a` to the ray under (x, y). */
  axisPoint(a, x, y) {
    const ray = this.vp.raycaster(x, y, this.vpRect).ray;
    const o = w2u(ray.origin), d = [-ray.direction.x, ray.direction.y, ray.direction.z];
    const w0 = vec.sub(this.pivot, o);
    const A = 1, B = a[0] * d[0] + a[1] * d[1] + a[2] * d[2], C = 1;
    const D = a[0] * w0[0] + a[1] * w0[1] + a[2] * w0[2], E = d[0] * w0[0] + d[1] * w0[1] + d[2] * w0[2];
    const den = A * C - B * B;
    if (Math.abs(den) < 1e-6) return null;
    const t = (B * E - C * D) / den;
    return vec.add(this.pivot, vec.mul(a, t));
  }

  hit(x, y) {
    if (this.axis && !this.planeLock) return this.axisPoint(AXES[this.axis], x, y);
    const n = this.axis && this.planeLock ? AXES[this.axis] : this.vp.viewDir(this.rig);
    return this.vp.planeHit(x, y, this.pivot, n, this.vpRect);
  }

  op() {
    const typed = this.typed !== '' && this.typed !== '-' && this.typed !== '.' ? parseFloat(this.typed) : null;
    const snap = this.snapping;
    const t = this.type;
    if (t === 'translate') {
      let d;
      if (typed !== null && Number.isFinite(typed)) d = vec.mul(AXES[this.axis || 'x'], typed);
      else {
        const h0 = this.hit(this.start.x, this.start.y), h1 = this.hit(this.mouse.x, this.mouse.y);
        d = h0 && h1 ? vec.sub(h1, h0) : [0, 0, 0];
        if (this.axis && this.planeLock) d[{ x: 0, y: 1, z: 2 }[this.axis]] = 0;
        if (this.axis && !this.planeLock) { const a = AXES[this.axis]; const k = d[0] * a[0] + d[1] * a[1] + d[2] * a[2]; d = vec.mul(a, k); }
        if (snap) { const g = this.ctx.snapStep || 0.1; d = d.map(v => Math.round(v / g) * g); }
      }
      return { type: 'translate', d };
    }
    if (t === 'rotate') {
      let angle;
      if (typed !== null && Number.isFinite(typed)) angle = typed;
      else {
        const ps = this.pivotScreen();
        const a0 = Math.atan2(-(this.start.y - ps.cy), this.start.x - ps.cx), a1 = Math.atan2(-(this.mouse.y - ps.cy), this.mouse.x - ps.cx);
        let deg = (a1 - a0) * 180 / Math.PI;
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        // Turn the way the mouse turns as seen on screen.
        const back = this.rig.basis().back;
        const axisW = this.axis ? u2w(AXES[this.axis]) : back;
        const facing = axisW.dot(back) >= 0 ? 1 : -1;
        angle = -deg * facing;
        if (snap) angle = Math.round(angle / 5) * 5;
        this.screenAngle = angle;
      }
      const axis = this.axis ? AXES[this.axis] : (() => { const b = this.rig.basis().back; return [-b.x, b.y, b.z]; })();
      this.lastAngle = angle;
      return { type: 'rotate', q: angleAxis(angle, axis), pivot: this.pivot, angle, axis };
    }
    if (t === 'scale') {
      let f;
      if (typed !== null && Number.isFinite(typed)) f = typed;
      else {
        const ps = this.pivotScreen();
        const d0 = Math.max(4, Math.hypot(this.start.x - ps.cx, this.start.y - ps.cy)), d1 = Math.hypot(this.mouse.x - ps.cx, this.mouse.y - ps.cy);
        f = d1 / d0;
        if (snap) f = Math.round(f * 10) / 10;
      }
      this.lastFactor = f;
      let s = [f, f, f];
      if (this.axis && !this.planeLock) s = AXES[this.axis].map(v => (v ? f : 1));
      else if (this.axis && this.planeLock) s = AXES[this.axis].map(v => (v ? 1 : f));
      return { type: 'scale', s, pivot: this.pivot };
    }
    if (t === 'slide') {
      let amount;
      const sl = this.ctx.slide;
      if (typed !== null && Number.isFinite(typed)) amount = typed;
      else {
        const upp = this.rig.unitsPerPixel(this.vpRect.h);
        if (sl.mode === 'inset') amount = Math.hypot(this.mouse.x - this.start.x, this.mouse.y - this.start.y) * upp * 0.5;
        else {
          // Along the motion direction as it appears on screen.
          const a = this.vp.projectIn(this.pivot, this.vpRect), b = this.vp.projectIn(vec.add(this.pivot, sl.dir), this.vpRect);
          const sx = b.cx - a.cx, sy = b.cy - a.cy, L = Math.hypot(sx, sy);
          amount = L > 1e-3 ? ((this.mouse.x - this.start.x) * sx + (this.mouse.y - this.start.y) * sy) / (L * L) : (this.start.y - this.mouse.y) * upp;
        }
        if (snap) amount = Math.round(amount / 0.05) * 0.05;
      }
      if (sl.max != null) amount = Math.max(0, Math.min(sl.max, amount));
      this.lastAmount = amount;
      return { type: 'slide', amount };
    }
    // param
    const p = this.ctx.param;
    let v;
    if (typed !== null && Number.isFinite(typed)) v = typed;
    else {
      v = p.value + (this.mouse.x - this.start.x) * p.perPx;
      if (snap && p.step) v = Math.round(v / p.step) * p.step;
    }
    v = Math.max(p.min ?? -Infinity, Math.min(p.max ?? Infinity, v));
    this.lastValue = v;
    return { type: 'param', value: v };
  }

  update() {
    // Every apply works from the snapshot taken at the start, so nothing needs undoing between mouse moves.
    const op = this.op();
    this.note = this.ctx.apply(op) || this.note;
    this.render(op);
  }

  render(op) {
    const el = this.ctx.readout;
    if (!el) return;
    const f = v => (Math.round(v * 1000) / 1000).toFixed(3);
    const ax = this.axis ? `<span class="ax-${this.axis}">${this.planeLock ? 'not ' : ''}${this.axis.toUpperCase()}</span>` : '';
    let vals = '';
    if (op.type === 'translate') vals = `<span class="val"><span class="ax-x">X</span> ${f(op.d[0])} <span class="ax-y">Y</span> ${f(op.d[1])} <span class="ax-z">Z</span> ${f(op.d[2])} m</span>`;
    else if (op.type === 'rotate') vals = `<span class="val">${(Math.round(op.angle * 10) / 10).toFixed(1)}°</span>`;
    else if (op.type === 'scale') vals = `<span class="val">×${(Math.round(this.lastFactor * 1000) / 1000).toFixed(3)}</span>`;
    else if (op.type === 'slide') vals = `<span class="val">${f(op.amount)} m</span>`;
    else vals = `<span class="val">${(Math.round(op.value * 100) / 100).toFixed(2)} ${this.ctx.param.unit || ''}</span>`;
    const label = this.ctx.label || (this.type === 'param' ? this.ctx.param.label : LABEL[this.type]);
    el.innerHTML = `<b>${label}</b>${ax}${vals}${this.typed ? `<span class="typed">${this.typed}_</span>` : ''}
      <span class="muted">${this.type === 'translate' || this.type === 'rotate' || this.type === 'scale' ? 'X Y Z axis · ' : ''}type a number · ${this.snapping ? '<b>snap on</b>' : 'Ctrl snap'} · Enter ok · Esc cancel</span>`;
    el.hidden = false;
  }

  move(e) { this.mouse = { x: e.clientX, y: e.clientY }; this.snapHeld = !!e.ctrlKey; this.update(); }

  /** Returns true when the key was used. */
  key(e) {
    let k = e.key;
    if (e.code === 'Period' || e.code === 'NumpadDecimal' || e.code === 'Comma' || k === 'Decimal') k = '.';
    else if (e.code === 'Minus' || e.code === 'NumpadSubtract') k = '-';
    else if (/^(Digit|Numpad)[0-9]$/.test(e.code || '')) k = e.code.slice(-1);
    if (k === 'Escape') { this.cancel(); return true; }
    if (k === 'Enter' || k === 'NumpadEnter') { this.confirm(); return true; }
    if (k === 'Control') { this.snapHeld = true; this.update(); return true; }
    const lower = k.toLowerCase();
    if ((lower === 'x' || lower === 'y' || lower === 'z') && !e.ctrlKey && (this.type === 'translate' || this.type === 'rotate' || this.type === 'scale')) {
      const plane = e.shiftKey && this.type !== 'rotate';
      if (this.axis === lower && this.planeLock === plane) { this.axis = null; this.planeLock = false; }
      else { this.axis = lower; this.planeLock = plane; }
      this.update();
      return true;
    }
    if (/^[0-9]$/.test(k) || (k === '.' || k === ',') || (k === '-' && !this.typed.includes('-'))) {
      if (k === '-') this.typed = this.typed.startsWith('-') ? this.typed.slice(1) : '-' + this.typed;
      else if (k === '.' || k === ',') { if (!this.typed.includes('.')) this.typed += '.'; }
      else this.typed += k;
      this.update();
      return true;
    }
    if (k === 'Backspace') { this.typed = this.typed.slice(0, -1); this.update(); return true; }
    return ['g', 'r', 's', 'a', 'h', 'e', 'i'].includes(lower) || k === 'Tab'; // swallow tool keys while transforming
  }

  keyUp(e) { if (e.key === 'Control') { this.snapHeld = false; this.update(); } }

  confirm() { if (this.done) return; this.done = true; this.ctx.readout && (this.ctx.readout.hidden = true); this.ctx.done(true, this.note); }
  cancel() { if (this.done) return; this.done = true; this.ctx.restore(); this.ctx.readout && (this.ctx.readout.hidden = true); this.ctx.done(false); }
}
