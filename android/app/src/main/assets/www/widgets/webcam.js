import { API, PANEL_HEADERS, bridge, curPage, h, haptic, icon, pages, t } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- phone webcam
// The phone's camera as a webcam on the PC. The app runs the camera (Android, through the bridge): it keeps the latest
// picture on the phone for this widget's preview (/agent/api/webcam/preview, answered by the app itself, never by the
// computer) and, with the widget's "Send the picture to the computer" option on, sends the pictures to the agent, which
// hands them to video apps as OBS Virtual Camera. Nothing runs until the button is tapped; the app switches the camera
// off when the panel pauses (screen off, another app in front) or reloads. One camera at a time: the bridge's state
// names the widget it runs for, so another webcam widget shows "off".

const PC_NOTES = {
  obs: 'w.webcam.live', stream: 'w.webcam.stream', missing: 'w.webcam.missing', busy: 'w.webcam.busy',
  error: 'w.webcam.pcError', offline: 'w.webcam.offline', phone: 'w.webcam.phoneOnly', '': 'w.webcam.connecting',
};
const ERRORS = {
  permission: 'w.webcam.permission', 'permission-blocked': 'w.webcam.permissionBlocked',
  unavailable: 'w.webcam.unavailable', 'in-use': 'w.webcam.inUse', disabled: 'w.webcam.disabled',
};

W.webcam = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('wc');
    const key = `${page.id}/${w.id}`;
    const img = h('img', 'wc-img');
    img.alt = '';
    const note = h('div', 'wc-note');
    const live = h('span', 'wc-live', t('w.webcam.liveBadge'));
    live.hidden = true;
    const power = h('button', 'wc-btn wc-power', icon('camera'));
    const flip = h('button', 'wc-btn wc-flip', icon('rotate-ccw'));
    flip.setAttribute('aria-label', t('w.webcam.flip'));
    flip.title = t('w.webcam.flip');
    const bar = h('div', 'wc-bar', h('div', 'wc-cap'), flip, power);
    const cap = bar.firstChild;
    box.append(img, note, live, bar);

    const can = !!(bridge && bridge.webcamStart && bridge.webcamState);
    let alive = true, facing = o.facing === 'back' ? 'back' : 'front';
    let seq = -1, fetching = false, url = '', timer = 0, last = null;
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const read = () => { try { return JSON.parse(bridge.webcamState() || '{}'); } catch (_) { return {}; } };
    const mine = (s) => s.widget === key && (s.state === 'starting' || s.state === 'live' || s.state === 'error');
    const running = (s) => s.widget === key && (s.state === 'starting' || s.state === 'live');

    function show(s) {
      last = s;
      const on = running(s);
      power.replaceChildren(icon(on ? 'square' : 'camera'));
      power.classList.toggle('on', on);
      const label = t(on ? 'w.webcam.stop' : 'w.webcam.start');
      power.setAttribute('aria-label', label);
      power.title = label;
      box.classList.toggle('wc-on', on);
      img.classList.toggle('mirror', on && s.facing === 'front');
      live.hidden = !(on && s.state === 'live' && (s.pc === 'obs' || s.pc === 'stream'));
      let text;
      if (!mine(s)) text = t('w.webcam.off');
      else if (s.state === 'starting') text = t('w.webcam.starting');
      else if (s.state === 'error') text = t(ERRORS[s.error] || 'w.webcam.unavailable');
      else if (String(s.pc || '').startsWith('refused:')) text = s.pc.slice(8) || t('w.webcam.pcError');
      else text = t(PC_NOTES[s.pc || ''] || 'w.webcam.connecting');
      cap.textContent = text;
      // without a picture the note fills the widget; with one it's the caption over it
      const picture = on && s.state === 'live' && !!url;
      note.hidden = picture;
      note.textContent = picture ? '' : text;
      cap.hidden = !picture;
      img.classList.toggle('on', picture);
      if (!on && url) { URL.revokeObjectURL(url); url = ''; img.removeAttribute('src'); seq = -1; }
    }

    async function picture(s) {
      if (fetching || s.seq === seq || !visible()) return;
      fetching = true;
      try {
        const r = await fetch(`${API}/webcam/preview`, { cache: 'no-store', headers: PANEL_HEADERS });
        if (!alive || r.status !== 200) return;
        const blob = await r.blob();
        if (!alive || !running(read())) return;
        const next = URL.createObjectURL(blob);
        const old = url;
        url = next;
        seq = s.seq;
        img.src = next;
        if (old) setTimeout(() => URL.revokeObjectURL(old), 1000);
      } catch (_) {
        // the camera stopped in between: the next look at the state says so
      } finally { fetching = false; }
    }

    // a quick look while the camera runs (the preview), a slow one otherwise (another widget, a pause, an error)
    function poll() {
      clearTimeout(timer);
      if (!alive) return;
      const s = read();
      show(s);
      if (running(s) && s.state === 'live') picture(s);
      timer = setTimeout(poll, running(s) && visible() ? 80 : 1000);
    }

    function start() {
      bridge.webcamStart(key, facing, o.quality === 'sd' ? 'sd' : 'hd', o.pc !== false);
      show({ widget: key, state: 'starting', facing });
      poll();
    }

    power.onclick = () => {
      haptic();
      if (!can) return;
      if (running(read())) { bridge.webcamStop(); show({}); poll(); return; }
      if (bridge.canWebcam && !bridge.canWebcam()) { show({ widget: key, state: 'error', error: 'unavailable' }); return; }
      start();
    };
    flip.onclick = () => {
      haptic();
      facing = facing === 'front' ? 'back' : 'front';
      if (can && running(read())) start();
    };

    if (!can) {
      note.textContent = t('w.webcam.update');
      cap.hidden = true;
      power.disabled = true;
      flip.disabled = true;
    } else poll();

    return {
      update() { if (can && last && running(last) && visible()) poll(); },
      destroy() {
        alive = false;
        clearTimeout(timer);
        if (can && running(read())) bridge.webcamStop();
        if (url) URL.revokeObjectURL(url);
      },
    };
  },
};
