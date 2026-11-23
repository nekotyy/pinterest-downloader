const api = globalThis.browser || chrome;
const endpoint = 'http://127.0.0.1:17849/pins';
let sending = false;
async function sync() {
  if (sending) return;
  sending = true;
  try {
    const tabs = await api.tabs.query({ url: 'https://*.pinterest.com/pin/*' });
    const seen = new Set(), pins = [];
    for (const tab of tabs) {
      const match = tab.url?.match(/^https:\/\/(?:[a-z0-9-]+\.)*pinterest\.com\/pin\/(\d+)/i);
      if (!match || seen.has(match[1])) continue;
      seen.add(match[1]);
      let image = '';
      try {
        const result = await api.scripting.executeScript({ target: { tabId: tab.id }, func: () => {
          const meta = document.querySelector('meta[property="og:image"]')?.content;
          if (meta) return meta;
          return [...document.images].filter(i => i.currentSrc && i.naturalWidth >= 200 && i.naturalHeight >= 200)
            .sort ( (a,b) => b.naturalWidth*b.naturalHeight-a.naturalWidth*a.naturalHeight )[0]?.currentSrc || '';
        }});
        image = result[0]?.result || '';
      } catch (_) {}
      pins.push({ id: match[1], url: tab.url, title: tab.title || `Пин ${match[1]}`, image });
    }
    await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pins) });
    await api.action.setBadgeText({text: ''});
  } catch (_) { await api.action.setBadgeText({text: '!'}); }
  finally { sending = false; }
}
api.action.onClicked.addListener(sync);
api.tabs.onUpdated.addListener((id, change) => { if (change.status === 'complete') sync(); });
api.tabs.onRemoved.addListener(sync);
api.runtime.onStartup.addListener(sync);
api.runtime.onInstalled.addListener(() => { api.alarms.create('sync', { periodInMinutes: 0.5 }); sync(); });
api.alarms.onAlarm.addListener(sync);
