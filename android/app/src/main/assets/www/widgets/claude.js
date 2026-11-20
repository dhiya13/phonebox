import { D, api, approvalWidgets, approvals, answeredHere, curPage, fmtTime, h, haptic, loadWork, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- Claude actions
// The Claude Code sessions that need you, with big buttons. Approve / Deny answer a permission prompt through the same
// approval the full-screen card answers (POST /approvals/{id}); Continue / Retry go to a session that finished while you
// were away and that Claude Code's Stop hook holds for the phone (POST /claude/held/{id}), so the answer reaches that
// one session and nothing is typed anywhere. Every button names its session. Nothing is answered unless the widget's
// own "Allow answering from the phone" option is on, and the agent checks the same option again.

W['claude-actions'] = {
  needs: ['work', 'approvals', 'tick'],
  create(box, { o, page, w }) {
    box.classList.add('ca');
    box.style.setProperty('--c', '#d97757');
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const allowed = o.control === true;
    const sum = h('span', 'w-sum');
    const list = h('div', 'ca-list');
    const note = h('div', 'ca-note', allowed ? '' : t('w.claudeActions.controlOff'));
    note.hidden = allowed;
    box.append(h('div', 'w-head', h('span', 's-label', '✳ ' + t('w.claudeActions.title')), sum), list, note);

    const busy = new Set(); // ids whose answer is on its way
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    // While this widget is on screen and allowed to answer, it stands in for the full-screen approval card.
    const covers = () => allowed && box.isConnected && visible();
    approvalWidgets.add(covers);

    const sessionOf = (id) => (D.work?.sessions || []).find((s) => s.id === 'claude:' + id);
    const nameOf = (title, project) => title || project || 'Claude Code';

    async function send(path, id, label, name, onOk) {
      if (busy.has(id)) return;
      haptic();
      busy.add(id);
      render(true);
      try {
        await api(path, 'POST', 4000);
        onOk?.();
        toast(t('w.claudeActions.sent', { action: label, session: name }), 'ok');
      } catch (e) {
        toast(e?.status === 403 ? t('w.claudeActions.controlOff') : t('w.claudeActions.gone'), 'warn');
      } finally {
        busy.delete(id);
        render(true);
      }
    }

    const button = (cls, label, onClick, disabled) => {
      const b = h('button', 'ca-btn ' + cls, label);
      b.type = 'button';
      b.disabled = disabled;
      b.addEventListener('click', onClick);
      return b;
    };

    function rows() {
      const out = [];
      const asked = new Set();
      for (const a of approvals) {
        asked.add(a.session);
        const name = nameOf(sessionOf(a.session)?.title, a.project);
        const question = a.kind === 'question';
        const kids = [h('div', 'ca-who', h('span', 'ca-name', name), h('span', 'ca-time', a.expiresAt > Date.now() ? fmtTime(a.expiresAt - Date.now()) : '')),
          h('div', 'ca-what', question ? t('w.claudeActions.question') : a.tool),
          a.detail && !question ? h('div', 'ca-detail', a.detail) : null];
        if (!question && allowed) {
          const off = busy.has(a.id);
          const go = (decision, label) => () => send(`/approvals/${encodeURIComponent(a.id)}?decision=${decision}&${wq}`, a.id, label, name, () => answeredHere(a.id));
          kids.push(h('div', 'ca-btns',
            button('ok', t('w.claudeActions.approve'), go('allow', t('w.claudeActions.approve')), off),
            button('no', t('w.claudeActions.deny'), go('deny', t('w.claudeActions.deny')), off)));
        }
        out.push(h('div', 'ca-item ' + (question ? 'ask' : 'perm'), kids.filter(Boolean)));
      }
      for (const s of D.work?.held || []) {
        const name = nameOf(s.title, s.project);
        const kids = [h('div', 'ca-who', h('span', 'ca-name', name), h('span', 'ca-time', t('w.claudeActions.waitsFor', { time: fmtTime(Math.max(0, s.expiresAt - Date.now())) }))),
          h('div', 'ca-what', t('w.claudeActions.finished'))];
        if (allowed) {
          const off = busy.has(s.id);
          const go = (action, label) => () => send(`/claude/held/${encodeURIComponent(s.id)}?action=${action}&${wq}`, s.id, label, name, loadWork);
          kids.push(h('div', 'ca-btns',
            button('ok', t('w.claudeActions.continue'), go('continue', t('w.claudeActions.continue')), off),
            button('warn', t('w.claudeActions.retry'), go('retry', t('w.claudeActions.retry')), off),
            button('dim', t('w.claudeActions.done'), go('done', t('w.claudeActions.done')), off)));
        }
        out.push(h('div', 'ca-item held', kids));
      }
      // waiting in the terminal with nothing the phone can answer (no hook, a prompt the hook doesn't see): shown so
      // you know, without buttons
      for (const s of D.work?.sessions || []) {
        if (s.tool !== 'claude' || s.status !== 'waiting' || asked.has(s.id.slice(7))) continue;
        out.push(h('div', 'ca-item wait',
          h('div', 'ca-who', h('span', 'ca-name', nameOf(s.title, s.project || s.name))),
          h('div', 'ca-what', s.waitingFor || t('w.claudeActions.terminal'))));
      }
      return out;
    }

    let last = '';
    function render(force) {
      // the countdowns move every second; everything else is drawn again only when it changed
      const key = JSON.stringify([approvals.map((a) => a.id), (D.work?.held || []).map((s) => s.id),
        (D.work?.sessions || []).filter((s) => s.tool === 'claude' && s.status === 'waiting').map((s) => s.id + s.title + s.waitingFor)]);
      if (!force && key === last) {
        const times = list.querySelectorAll('.ca-time');
        const items = [...approvals.map((a) => (a.expiresAt > Date.now() ? fmtTime(a.expiresAt - Date.now()) : '')),
          ...(D.work?.held || []).map((s) => t('w.claudeActions.waitsFor', { time: fmtTime(Math.max(0, s.expiresAt - Date.now())) }))];
        items.forEach((text, i) => { if (times[i]) times[i].textContent = text; });
        return;
      }
      last = key;
      const r = rows();
      const n = approvals.filter((a) => a.kind !== 'question').length + (D.work?.held || []).length;
      sum.textContent = n ? String(n) : '';
      list.replaceChildren(...(r.length ? r : [h('div', 'empty', t('w.claudeActions.none'))]));
    }
    render(true);

    return {
      update() { render(false); },
      destroy() { approvalWidgets.delete(covers); },
    };
  },
};
