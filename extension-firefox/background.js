const api = globalThis.browser || chrome;
const endpoint = 'http://127.0.0.1:17849/pins';
const cache = new Map();
let running = false, preparing = false, rerun = false, timer;
let state = { connected: false, phase: 'Ожидание', done: 0, total: 0, browser: '' };

function detectBrowser() {
  const ua = navigator.userAgent;
  if (/Firefox\//i.test(ua)) return 'Firefox';
  if (/Edg\//i.test(ua)) return 'Microsoft Edge';
  if (/OPR\//i.test(ua)) return 'Opera';
  if (/YaBrowser\//i.test(ua)) return 'Яндекс Браузер';
  if (navigator.brave) return 'Brave';
  return 'Chrome / Chromium';
}
async function identity() {
  let info = await api.storage.local.get(['sourceId', 'browserName']);
  if (!info.sourceId) { info.sourceId = crypto.randomUUID(); await api.storage.local.set({ sourceId: info.sourceId }); }
  return { source: info.sourceId, browser: (info.browserName || detectBrowser()).slice(0, 50) };
}
async function sync() {
  if (running || preparing) { rerun = true; return; }
  running = true;
  try {
    const who = await identity();
    const generation = Date.now();
    const tabs = await api.tabs.query({ url: 'https://*.pinterest.com/pin/*' });
    const seen = new Set(), items = [];
    for (const tab of tabs) {
      const match = tab.url?.match(/^https:\/\/(?:[a-z0-9-]+\.)*pinterest\.com\/pin\/(\d+)\/?(?:[?#].*)?$/i);
      if (!match || seen.has(match[1])) continue;
      seen.add(match[1]);
      const saved = cache.get(tab.id);
      items.push({ tab, pin: { id: match[1], url: tab.url, title: tab.title || `Пин ${match[1]}`, image: saved?.url === tab.url ? saved.image : '' } });
    }
    state = { ...state, browser: who.browser, phase: 'Поиск изображений', done: 0, total: items.length };
    const batchSize = Math.max(50, Math.ceil(items.length / 20));
    async function send() {
      const response = await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...who, generation, done: state.done, total: state.total, pins: items.map(x => x.pin) }) });
      state.connected = response.ok;
      if (!response.ok) throw new Error('Приложение отклонило подключение');
    }
    try { await send(); } catch (_) { state.connected = false; }
    let next = 0;
    async function worker() {
      while (next < items.length) {
        const item = items[next++], tab = item.tab;
        if (!item.pin.image && !tab.discarded && !tab.frozen) {
          try {
            const result = await Promise.race([
              api.scripting.executeScript({ target: { tabId: tab.id }, func: () => {
                const meta = document.querySelector('meta[property="og:image"]')?.content;
                if (meta) return meta;
                return [...document.images].filter(i => i.currentSrc && i.naturalWidth >= 200 && i.naturalHeight >= 200)
                  .sort((a, b) => b.naturalWidth * b.naturalHeight - a.naturalWidth * a.naturalHeight)[0]?.currentSrc || '';
              }}),
              new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 10000))
            ]);
            item.pin.image = result[0]?.result || '';
            if (item.pin.image) cache.set(tab.id, { url: tab.url, image: item.pin.image });
          } catch (_) {}
        }
        state.done++;
        if (state.done % batchSize === 0) { try { await send(); } catch (_) { state.connected = false; } }
      }
    }
    await Promise.all(Array.from({ length: Math.min(6, items.length) }, worker));
    state.phase = 'Готово';
    try { await send(); } catch (_) { state.connected = false; }
    await api.action.setBadgeText({ text: state.connected ? '' : '!' });
  } catch (_) { state.connected = false; state.phase = 'Ошибка'; await api.action.setBadgeText({ text: '!' }); }
  finally { running = false; if (rerun) { rerun = false; schedule(); } }
}
function schedule() { if (preparing) { rerun = true; return; } clearTimeout(timer); timer = setTimeout(sync, 400); }
async function loadDiscarded() {
  if (running || preparing) return { busy: true };
  preparing = true; clearTimeout(timer);
  let failed = 0;
  try {
    const tabs = await api.tabs.query({ url: 'https://*.pinterest.com/pin/*' });
    const targets = tabs.filter(tab => (tab.discarded || tab.frozen) && /^https:\/\/(?:[a-z0-9-]+\.)*pinterest\.com\/pin\/\d+\/?(?:[?#].*)?$/i.test(tab.url || ''));
    state = { ...state, phase: 'Загрузка вкладок', done: 0, total: targets.length };
    for (let i = 0; i < targets.length; i += 3) {
      await Promise.all(targets.slice(i, i + 3).map(async tab => {
        try { cache.delete(tab.id); await api.tabs.reload(tab.id); } catch (_) { failed++; }
        state.done++;
      }));
      if (i + 3 < targets.length) await new Promise(resolve => setTimeout(resolve, 750));
    }
    return { total: targets.length, failed };
  } catch (_) { return { error: true }; }
  finally { preparing = false; rerun = false; schedule(); }
}
api.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type === 'status') { respond(state); return; }
  if (message?.type === 'sync') { schedule(); respond({ ok: true }); return; }
  if (message?.type === 'load-discarded') { loadDiscarded().then(respond); return true; }
  if (message?.type === 'browser') { api.storage.local.set({ browserName: String(message.name || '').slice(0, 50) }).then(() => { schedule(); respond({ ok: true }); }); return true; }
});
api.tabs.onUpdated.addListener((id, change) => { if (change.status === 'loading') cache.delete(id); if (change.status === 'complete') schedule(); });
api.tabs.onRemoved.addListener(id => { cache.delete(id); schedule(); });
api.runtime.onStartup.addListener(schedule);
api.runtime.onInstalled.addListener(() => { api.alarms.create('sync', { periodInMinutes: 0.5 }); schedule(); });
api.alarms.onAlarm.addListener(schedule);
