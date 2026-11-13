import { API, D, PANEL_HEADERS, api, cfg, cssVar, gotoPageRef, h, haptic, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- community plugins (docs/PLUGINS.md)
// Every widget type "plugin.<plugin>.<widget>" is drawn by W.plugin (main.js falls back to it). The plugin's
// metadata comes with the config (cfg.plugins, enabled plugins only); its data from /api/plugins/state (D.plugins).
//  - "view" widgets reuse the Pushed value widget: no plugin code runs on the phone.
//  - "ui" widgets are the plugin's own HTML in a sandboxed iframe (like the Custom HTML widget), with a strict
//    Content-Security-Policy. It sees only its own plugin's data and can only ask for the actions its widget declares;
//    the panel checks them and the agent checks them again.

const uiCache = new Map(); // "<plugin>@<version>/<widget>" -> Promise<html>

/** Sends a declared action to the plugin's backend; resolves with the plugin's "data", rejects with its error. */
export async function pluginAction(plugin, page, widget, name, args) {
  const q = new URLSearchParams({ page, widget, name });
  if (args !== undefined) q.set('args', JSON.stringify(args));
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), 20000);
  try {
    const r = await fetch(`${API}/plugins/${encodeURIComponent(plugin)}/action?${q}`, { method: 'POST', headers: PANEL_HEADERS, cache: 'no-store', signal: ctl.signal });
    let body = {};
    try { body = await r.json(); } catch (_) {}
    if (!r.ok || body.ok === false) throw new Error(body.error || 'HTTP ' + r.status);
    return body.data;
  } finally { clearTimeout(t); }
}

/** JSON that is safe inside a <script> element. */
const scriptJson = (v) => JSON.stringify(v ?? null).replace(/</g, '\\u003c').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');

export function pluginPrelude({ plugin, widget, instance, options, network }) {
  const csp = `default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:${network ? ' https:' : ''}; font-src data:; media-src data:${network ? '; connect-src https:' : ''}`;
  const v = (name, fallback) => cssVar(name) || fallback;
  return `<!doctype html><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}">
<meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark">
<style>:root{color-scheme:dark;--pb-text:${v('--text', '#eef2f6')};--pb-dim:${v('--muted', '#8a94a3')};--pb-accent:${v('--accent', '#2dd4bf')};--pb-card:${v('--card', 'rgba(255,255,255,.05)')}}
html,body{margin:0;height:100%;background:transparent;color:var(--pb-text);font-family:Roboto,"Segoe UI",system-ui,sans-serif;overflow:hidden}</style>
<script>(function(){var subs={},data={},reqs={},seq=0;
function post(m){m.phonebox=m.phonebox||'';parent.postMessage(m,'*')}
window.phonebox={plugin:${scriptJson(plugin)},widget:${scriptJson(widget)},instance:${scriptJson(instance)},options:${scriptJson(options)},data:data,
on:function(k,f){(subs[k]=subs[k]||[]).push(f);if(data[k]!==undefined)try{f(data[k])}catch(e){console.error(e)}},
action:function(name,args){return new Promise(function(ok,fail){var id=++seq;reqs[id]={ok:ok,fail:fail};post({phonebox:'action',req:id,name:String(name),args:args})})},
goto:function(p){post({phonebox:'goto',page:p})},
toast:function(text,kind){post({phonebox:'toast',text:String(text),kind:kind})}};
addEventListener('message',function(e){var m=e.data;if(!m||e.source!==parent)return;
if(m.phonebox==='data'){data[m.kind]=m.value;(subs[m.kind]||[]).forEach(function(f){try{f(m.value)}catch(err){console.error(err)}})}
else if(m.phonebox==='result'&&reqs[m.req]){var r=reqs[m.req];delete reqs[m.req];m.ok?r.ok(m.data):r.fail(new Error(m.error||'failed'))}});})();<\/script>`;
}

function frameWidget(box, ctx, meta, wm, key) {
  const frame = h('iframe', 'html-frame plugin-frame');
  frame.setAttribute('sandbox', 'allow-scripts');
  frame.setAttribute('referrerpolicy', 'no-referrer');
  frame.title = wm.name;
  box.append(frame);
  const cacheKey = `${meta.id}@${meta.version}/${wm.id}`;
  if (!uiCache.has(cacheKey)) uiCache.set(cacheKey, api(`/plugins/${encodeURIComponent(meta.id)}/ui/${encodeURIComponent(wm.id)}`, 'GET', 8000).catch((e) => { uiCache.delete(cacheKey); throw e; }));
  let loaded = false, sentState, sentItem;
  const post = (m) => { try { frame.contentWindow?.postMessage(m, '*'); } catch (_) {} };
  const send = (force) => {
    if (!loaded) return;
    const state = D.plugins?.[meta.id] || {};
    const item = state[key];
    if (force || state !== sentState) post({ phonebox: 'data', kind: 'state', value: Object.fromEntries(Object.entries(state).map(([k, x]) => [k, x.data])) });
    if (force || item?.at !== sentItem?.at) post({ phonebox: 'data', kind: 'instance', value: item ? item.data : null });
    sentState = state;
    sentItem = item;
  };
  uiCache.get(cacheKey).then((html) => {
    frame.srcdoc = pluginPrelude({ plugin: meta.id, widget: wm.id, instance: { page: ctx.page.id, id: ctx.w.id, key }, options: ctx.o, network: meta.network }) + html;
  }, () => box.replaceChildren(h('div', 'empty', `${wm.name}: could not load the widget from the computer`)));
  frame.addEventListener('load', () => { loaded = true; send(true); });
  const onMsg = async (e) => {
    const m = e.data;
    if (e.source !== frame.contentWindow || !m || typeof m !== 'object') return;
    if (m.phonebox === 'action') {
      const reply = (r) => post({ phonebox: 'result', req: m.req, ...r });
      if (!(wm.actions || []).includes(m.name)) { reply({ ok: false, error: `the widget does not declare the action "${m.name}"` }); return; }
      haptic();
      try { reply({ ok: true, data: await pluginAction(meta.id, ctx.page.id, ctx.w.id, m.name, m.args) }); }
      catch (err) { reply({ ok: false, error: err.name === 'AbortError' ? 'The computer did not answer' : err.message }); }
    } else if (m.phonebox === 'goto') gotoPageRef(m.page);
    else if (m.phonebox === 'toast') toast(String(m.text || '').slice(0, 140), m.kind === 'ok' || m.kind === 'err' ? m.kind : '', meta.name);
  };
  window.addEventListener('message', onMsg);
  return {
    update(kind) { if (kind === 'plugins') send(false); },
    destroy() { window.removeEventListener('message', onMsg); },
  };
}

function viewWidget(box, ctx, meta, wm) {
  const v = wm.view;
  box.classList.add('w-push'); // the Pushed value widget's look
  const o = { ...ctx.o, style: v.style, unit: ctx.o.unit ?? v.unit ?? '', min: v.min ?? 0, max: v.max ?? 100, key: wm.name };
  const inner = W.push.create(box, { ...ctx, o, source: () => D.plugins?.[meta.id]?.[v.key], empty: 'Waiting for the plugin…' });
  return { needs: ['plugins', 'tick'], update: inner.update, destroy: inner.destroy };
}

W.plugin = {
  needs: ['plugins'],
  create(box, ctx) {
    box.classList.add('w-plugin');
    const [, id, widget] = ctx.w.type.split('.');
    const meta = (cfg.plugins || []).find((p) => p.id === id);
    const wm = meta?.widgets?.find((x) => x.id === widget);
    if (!meta || !wm) {
      box.append(h('div', 'empty', meta ? `${meta.name}: no widget "${widget}"` : `🧩 ${id}\nnot installed or off`));
      return {};
    }
    // the plugin publishes a widget's own data under "<page>.<widget>"
    const key = `${ctx.page.id}.${ctx.w.id}`;
    return wm.view ? viewWidget(box, ctx, meta, wm) : frameWidget(box, ctx, meta, wm, key);
  },
};
