// Audio visualizer: a calm, minimal take on the sound the PC is playing. It draws the same frames as Spectrum bars
// (spectrum.js keeps the agent capturing while either widget is on screen; one capture feeds both), in one of three
// styles: smooth bars, a mirrored wave or a ring, in the panel's accent colour or one of its own.
// The levels ease towards each frame, so it stays smooth at the display's rate; with nothing playing it settles on a
// faint resting line and stops drawing altogether.
import { W } from '../registry.js';
import { h, D, t } from '../main.js';
import { shows, watchSpectrum } from './spectrum.js';

const STYLES = ['bars', 'wave', 'ring'];
const GAIN = { low: 0.75, normal: 1, high: 1.4 };
const RISE = 22, FALL = 7;  // easing towards the frame, per second (rising faster than falling)
const STALE_MS = 600;       // frames stopped: let everything settle

// the panel's accent (Settings › Display) as [r, g, b]; main.js keeps it in --accent
function accentRgb() {
  const v = getComputedStyle(document.documentElement).getPropertyValue('--accent');
  const m = v.match(/(\d+)\D+(\d+)\D+(\d+)/);
  return m ? [+m[1], +m[2], +m[3]] : [45, 212, 191];
}
const hexRgb = (c) => [1, 3, 5].map((k) => parseInt(c.slice(k, k + 2), 16));
const rgba = (c, a) => `rgba(${c[0]},${c[1]},${c[2]},${a})`;

W.visualizer = {
  needs: ['spectrum'],
  create(box, ctx) {
    const o = ctx.o;
    const me = { box, page: ctx.page };
    watchSpectrum(me);
    const style = STYLES.includes(o.style) ? o.style : 'bars';
    const n = Math.max(4, Math.min(48, Math.round(+o.bars || 32)));
    const gain = GAIN[o.sensitivity] || GAIN.normal;
    const own = /^#[0-9a-f]{6}$/i.test(o.color || '') ? hexRgb(o.color) : [45, 212, 191];
    const follow = o.accent !== false;
    let rgb = follow ? accentRgb() : own;

    const canvas = h('canvas', 'viz-canvas');
    const wrap = h('div', 'viz-wrap', canvas);
    const msg = h('div', 'viz-msg', t('w.spectrum.waiting'));
    if (o.title) box.append(h('div', 'w-title', o.title));
    box.append(wrap, msg);
    const g = canvas.getContext('2d');

    const level = new Float32Array(n);
    let target = new Float32Array(n);
    let W2 = 0, H2 = 0, dpr = 1, raf = 0, last = 0;

    const resize = () => {
      if (!box.isConnected) return ro.disconnect();
      dpr = window.devicePixelRatio || 1;
      W2 = Math.max(1, Math.round(wrap.clientWidth * dpr));
      H2 = Math.max(1, Math.round(wrap.clientHeight * dpr));
      canvas.width = W2; canvas.height = H2;
      draw();
    };
    const ro = new ResizeObserver(resize);
    ro.observe(wrap);

    // rounded rectangle without roundRect (older WebViews)
    const pill = (x, y, w, hh, r) => {
      r = Math.max(0, Math.min(r, w / 2, hh / 2));
      g.moveTo(x + r, y);
      g.arcTo(x + w, y, x + w, y + hh, r);
      g.arcTo(x + w, y + hh, x, y + hh, r);
      g.arcTo(x, y + hh, x, y, r);
      g.arcTo(x, y, x + w, y, r);
      g.closePath();
    };

    const drawBars = () => {
      const gap = Math.max(1, (W2 / n) * 0.32), bw = Math.max(1, (W2 - gap * (n - 1)) / n);
      const min = Math.min(bw, 3 * dpr); // the resting stub
      const grad = g.createLinearGradient(0, H2, 0, 0);
      grad.addColorStop(0, rgba(rgb, 0.45));
      grad.addColorStop(1, rgba(rgb, 1));
      g.fillStyle = grad;
      g.beginPath();
      for (let i = 0; i < n; i++) {
        const hh = Math.max(min, level[i] * H2);
        pill(i * (bw + gap), H2 - hh, bw, hh, bw / 2);
      }
      g.fill();
    };

    const drawWave = () => {
      const mid = H2 / 2, amp = H2 / 2 - 2 * dpr, step = W2 / (n - 1);
      // a smooth curve through the levels (quadratic segments through the midpoints), mirrored around the centre line
      const curve = (sign) => {
        let px = 0, py = mid - sign * level[0] * amp;
        g.lineTo(px, py);
        for (let i = 1; i < n; i++) {
          const x = i * step, y = mid - sign * level[i] * amp;
          g.quadraticCurveTo(px, py, (px + x) / 2, (py + y) / 2);
          px = x; py = y;
        }
        g.lineTo(px, py);
      };
      g.beginPath();
      g.moveTo(0, mid);
      curve(1);
      g.lineTo(W2, mid);
      // back along the mirror image
      const pts = [];
      for (let i = 0; i < n; i++) pts.push([i * step, mid + level[i] * amp]);
      for (let i = n - 1; i > 0; i--) {
        const [x, y] = pts[i], [x0, y0] = pts[i - 1];
        g.quadraticCurveTo(x, y, (x + x0) / 2, (y + y0) / 2);
      }
      g.lineTo(0, mid);
      g.closePath();
      g.fillStyle = rgba(rgb, 0.22);
      g.fill();
      g.lineWidth = Math.max(1, 1.6 * dpr);
      g.lineJoin = 'round';
      g.strokeStyle = rgba(rgb, 0.95);
      g.beginPath(); g.moveTo(0, mid); curve(1); g.stroke();
      g.beginPath(); g.moveTo(0, mid); curve(-1); g.stroke();
    };

    const drawRing = () => {
      const cx = W2 / 2, cy = H2 / 2, half = Math.min(W2, H2) / 2;
      let bass = 0;
      for (let i = 0; i < Math.min(4, n); i++) bass += level[i];
      bass /= Math.min(4, n);
      const r0 = half * (0.42 + 0.06 * bass), room = half - r0 - 2 * dpr;
      // the spectrum runs down both sides from the top, so the ring is symmetric
      const spokes = n * 2, lw = Math.max(1, Math.min((2 * Math.PI * r0) / spokes * 0.55, 6 * dpr));
      g.lineCap = 'round';
      g.lineWidth = lw;
      g.strokeStyle = rgba(rgb, 0.95);
      g.beginPath();
      for (let k = 0; k < spokes; k++) {
        const i = k < n ? k : spokes - 1 - k;
        const a = -Math.PI / 2 + (k + 0.5) * (2 * Math.PI / spokes);
        const len = Math.max(lw * 0.2, level[i] * room);
        const c = Math.cos(a), s = Math.sin(a);
        g.moveTo(cx + c * r0, cy + s * r0);
        g.lineTo(cx + c * (r0 + len), cy + s * (r0 + len));
      }
      g.stroke();
      g.lineWidth = Math.max(1, dpr);
      g.strokeStyle = rgba(rgb, 0.18 + 0.3 * bass);
      g.beginPath();
      g.arc(cx, cy, Math.max(1, r0 - lw * 1.5), 0, 2 * Math.PI);
      g.stroke();
    };

    const draw = () => {
      g.clearRect(0, 0, W2, H2);
      if (W2 < 2 || H2 < 2) return;
      if (style === 'wave') drawWave(); else if (style === 'ring') drawRing(); else drawBars();
    };

    const step = (now) => {
      raf = 0;
      if (!box.isConnected) return;
      // not showing (another page, a hidden stack card, the night screen): rest, and start from rest when it shows again
      if (!shows(me)) { level.fill(0); target.fill(0); last = 0; draw(); return; }
      const dt = last ? Math.min(0.1, (now - last) / 1000) : 0;
      last = now;
      const fresh = Date.now() - (D.spectrumAt || 0) < STALE_MS;
      if (!fresh) target.fill(0);
      const up = 1 - Math.exp(-RISE * dt), down = 1 - Math.exp(-FALL * dt);
      let moving = false;
      for (let i = 0; i < n; i++) {
        const d = target[i] - level[i];
        level[i] += d * (d > 0 ? up : down);
        if (Math.abs(d) > 0.002) moving = true; else level[i] = target[i];
      }
      draw();
      if (moving || fresh) raf = requestAnimationFrame(step);
      else last = 0;
    };
    const kick = () => { if (!raf && shows(me)) raf = requestAnimationFrame(step); };

    // the same hints as Spectrum bars, only while nothing arrives; the accent is checked here too (Settings › Display)
    const hint = setInterval(() => {
      if (!box.isConnected) return clearInterval(hint);
      if (follow) {
        const a = accentRgb();
        if (a.join() !== rgb.join()) { rgb = a; if (!raf) draw(); }
      }
      const stale = Date.now() - (D.spectrumAt || 0) > 3000;
      msg.textContent = t(D.spectrumShown === false ? 'w.spectrum.save' : D.spectrumOk === false ? 'w.spectrum.unavailable' : 'w.spectrum.waiting');
      msg.hidden = !stale;
    }, 1000);

    return {
      update() {
        const src = D.spectrum;
        if (!src || !src.length) return;
        const next = new Float32Array(n);
        // the agent's bands averaged into this widget's bars (calmer than the loudest band), scaled by the sensitivity
        for (let i = 0; i < n; i++) {
          const a = Math.floor(i * src.length / n), b = Math.max(a + 1, Math.floor((i + 1) * src.length / n));
          let sum = 0, peak = 0;
          for (let k = a; k < b; k++) { sum += src[k] || 0; peak = Math.max(peak, src[k] || 0); }
          next[i] = Math.min(1, ((sum / (b - a) + peak) / 200) * gain);
        }
        target = next;
        msg.hidden = true;
        kick();
      },
    };
  },
};
