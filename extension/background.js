const api = globalThis.browser || chrome;
const endpoint = 'http://127.0.0.1:17849/pins';
const commandEndpoint = 'http://127.0.0.1:17849/commands';
const cache = new Map();
let running = false, preparing = false, rerun = false, timer;
let closingActive = false, checkingCommands = false;
let identityWork = null;
let closeWork = Promise.resolve();
let state = { connected: false, phase: 'Ожидание', done: 0, total: 0, browser: '' };

function normalizeSettings(values) {
  return {
    concurrency: [1, 2, 4, 6, 8].includes(values.concurrency) ? values.concurrency : 6,
    interval: [0, 0.5, 1, 5, 10].includes(values.interval) ? values.interval : 0.5,
    reloadBatch: [1, 3, 6].includes(values.reloadBatch) ? values.reloadBatch : 3
  };
}
async function getSettings() { return normalizeSettings(await api.storage.local.get(['concurrency', 'interval', 'reloadBatch'])); }
async function configureAlarm(settings) {
  await api.alarms.clear('sync');
  if (settings.interval > 0) await api.alarms.create('sync', { periodInMinutes: settings.interval });
  await api.alarms.create('commands', { periodInMinutes: 0.5 });
}

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
  if (identityWork) return identityWork;
  identityWork = (async () => {
    const info = await api.storage.local.get(['sourceId', 'browserName', 'handledClose']);
    if (!info.sourceId) { info.sourceId = crypto.randomUUID(); await api.storage.local.set({ sourceId: info.sourceId }); }
    if (info.handledClose && !info.handledClose.finished && !closingActive) await api.storage.local.set({ handledClose: { id: info.handledClose.id, closed: 0, failed: -1, finished: true } });
    return { source: info.sourceId, browser: (info.browserName || detectBrowser()).slice(0, 50) };
  })();
  try { return await identityWork; } finally { identityWork = null; }
}
async function closeSaved(command) {
  if (!command || !/^[a-f0-9]{32}$/.test(command.id || '') || !Array.isArray(command.pins) || command.pins.length > 10000 || !Number.isFinite(command.expiresAt) || command.expiresAt <= Date.now()) return false;
  const stored = await api.storage.local.get(['handledClose', 'handledCloseIds']);
  const processed = Array.isArray(stored.handledCloseIds) ? stored.handledCloseIds.filter(item => item.expiresAt > Date.now()) : [];
  if (stored.handledClose?.id === command.id || processed.some(item => item.id === command.id)) return false;
  const wanted = new Set(command.pins.filter(id => typeof id === 'string' && /^\d{1,30}$/.test(id)));
  processed.push({ id: command.id, expiresAt: command.expiresAt });
  closingActive = true;
  try {
    await api.storage.local.set({ handledCloseIds: processed, handledClose: { id: command.id, closed: 0, failed: 0, finished: false } });
    let closed = 0, failed = 0;
    try {
      const tabs = await api.tabs.query({ url: 'https://*.pinterest.com/pin/*' });
      const matching = tabs.filter(tab => {
        const match = tab.url?.match(/^https:\/\/(?:[a-z0-9-]+\.)*pinterest\.com\/pin\/(\d+)\/?(?:[?#].*)?$/i);
        return match && wanted.has(match[1]);
      });
      for (let i = 0; i < matching.length; i += 6) {
        await Promise.all(matching.slice(i, i + 6).map(async tab => {
          try { await api.tabs.remove(tab.id); closed++; } catch (_) { failed++; }
        }));
      }
    } catch (_) { failed = -1; }
    await api.storage.local.set({ handledClose: { id: command.id, closed, failed, finished: true } });
    rerun = true;
    return true;
  } finally { closingActive = false; }
}
async function acceptClose(command) { closeWork = closeWork.catch(() => {}).then(() => closeSaved(command)); return closeWork; }
async function checkCommands() {
  if (checkingCommands) return;
  checkingCommands = true;
  let executed = false;
  try {
    const who = await identity();
    const handled = (await api.storage.local.get(['handledClose'])).handledClose;
    const ack = handled?.finished ? { id: handled.id, closed: handled.closed, failed: handled.failed } : null;
    const response = await fetch(commandEndpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ source: who.source, ack }) });
    state.connected = response.ok;
    if (response.ok && response.status === 200) executed = await acceptClose(await response.json());
  } catch (_) { state.connected = false; }
  finally { checkingCommands = false; }
  if (executed) setTimeout(checkCommands, 400);
}
async function sync() {
  if (running || preparing) { rerun = true; return; }
  running = true;
  try {
    const who = await identity();
    const settings = await getSettings();
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
      const handled = (await api.storage.local.get(['handledClose'])).handledClose;
      const ack = handled?.finished ? { id: handled.id, closed: handled.closed, failed: handled.failed } : null;
      const response = await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...who, generation, done: state.done, total: state.total, pins: items.map(x => x.pin), ack }) });
      state.connected = response.ok;
      if (!response.ok) throw new Error('Приложение отклонило подключение');
      if (response.status === 200) await acceptClose(await response.json());
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
    await Promise.all(Array.from({ length: Math.min(settings.concurrency, items.length) }, worker));
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
    const settings = await getSettings();
    const targets = tabs.filter(tab => (tab.discarded || tab.frozen) && /^https:\/\/(?:[a-z0-9-]+\.)*pinterest\.com\/pin\/\d+\/?(?:[?#].*)?$/i.test(tab.url || ''));
    state = { ...state, phase: 'Загрузка вкладок', done: 0, total: targets.length };
    for (let i = 0; i < targets.length; i += settings.reloadBatch) {
      await Promise.all(targets.slice(i, i + settings.reloadBatch).map(async tab => {
        try { cache.delete(tab.id); await api.tabs.reload(tab.id); } catch (_) { failed++; }
        state.done++;
      }));
      if (i + settings.reloadBatch < targets.length) await new Promise(resolve => setTimeout(resolve, 750));
    }
    return { total: targets.length, failed };
  } catch (_) { return { error: true }; }
  finally { preparing = false; rerun = false; schedule(); }
}
api.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type === 'status') { respond(state); return; }
  if (message?.type === 'sync') { schedule(); respond({ ok: true }); return; }
  if (message?.type === 'load-discarded') { loadDiscarded().then(respond); return true; }
  if (message?.type === 'settings-get') { getSettings().then(respond).catch(() => respond({ error: true })); return true; }
  if (message?.type === 'settings-save') {
    const settings = normalizeSettings(message);
    api.storage.local.set(settings).then(() => configureAlarm(settings)).then(() => { schedule(); respond(settings); }).catch(() => respond({ error: true }));
    return true;
  }
  if (message?.type === 'browser') { api.storage.local.set({ browserName: String(message.name || '').slice(0, 50) }).then(() => { schedule(); respond({ ok: true }); }); return true; }
});
api.tabs.onUpdated.addListener((id, change) => { if (change.status === 'loading') cache.delete(id); if (change.status === 'complete') schedule(); });
api.tabs.onRemoved.addListener(id => { cache.delete(id); schedule(); });
api.runtime.onStartup.addListener(() => { getSettings().then(configureAlarm); schedule(); });
api.runtime.onInstalled.addListener(() => { getSettings().then(configureAlarm); schedule(); });
api.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'commands') checkCommands(); else if (alarm.name === 'sync') schedule(); });
