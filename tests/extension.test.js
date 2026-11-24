const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync('extension/background.js', 'utf8');
assert.equal(source, fs.readFileSync('extension-firefox/background.js', 'utf8'));
for (const file of ['popup.html', 'popup.css', 'popup.js']) assert.equal(fs.readFileSync(`extension/${file}`, 'utf8'), fs.readFileSync(`extension-firefox/${file}`, 'utf8'));
for (const folder of ['extension', 'extension-firefox']) {
  const manifest = JSON.parse(fs.readFileSync(`${folder}/manifest.json`, 'utf8'));
  assert.equal(manifest.action.default_popup, 'popup.html');
  assert.ok(manifest.permissions.includes('storage'));
  assert.ok(fs.existsSync(`${folder}/${manifest.icons['128']}`));
}

async function check(browserMode) {
  let messageListener, tabUpdated, sent = [], active = 0, maxActive = 0, calls = 0, reloads = 0;
  const events = { addListener() {} };
  const tabs = Array.from({ length: 1000 }, (_, i) => ({ id: i + 1, url: `https://ru.pinterest.com/pin/${i + 1}/`, title: `Пин ${i + 1}`, discarded: i === 9 }));
  tabs.push({ id: 1001, url: tabs[0].url, title: 'Дубликат' }, { id: 1002, url: 'https://evil.example/pin/55/' });
  const storage = {};
  const api = {
    tabs: { query: async () => tabs, reload: async id => { reloads++; tabs.find(tab => tab.id === id).discarded = false; }, onUpdated: { addListener(fn) { tabUpdated = fn; } }, onRemoved: events },
    scripting: { executeScript: async ({ target }) => { active++; calls++; maxActive = Math.max(active, maxActive); await Promise.resolve(); active--; if (target.tabId === 4) throw Error('unavailable'); return [{ result: `https://i.pinimg.com/${target.tabId}.jpg` }]; } },
    action: { setBadgeText: async () => {} },
    runtime: { onStartup: events, onInstalled: events, onMessage: { addListener(fn) { messageListener = fn; } } },
    alarms: { onAlarm: events, create() {} },
    storage: { local: { get: async () => ({ ...storage }), set: async data => Object.assign(storage, data) } }
  };
  const context = { [browserMode]: api, navigator: { userAgent: browserMode === 'browser' ? 'Firefox/120' : 'Chrome/120' }, crypto,
    setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
    fetch: async (url, options) => { sent.push(JSON.parse(options.body)); return { ok: true }; } };
  vm.createContext(context);
  vm.runInContext(source, context);
  await vm.runInContext('sync()', context);
  const result = sent.at(-1);
  assert.equal(result.pins.length, 1000);
  assert.equal(result.done, 1000);
  assert.equal(result.pins[0].image, 'https://i.pinimg.com/1.jpg');
  assert.equal(result.pins[3].image, '');
  assert.equal(result.pins[9].image, '');
  assert.ok(maxActive <= 6);
  assert.equal(calls, 999);
  let status;
  messageListener({ type: 'status' }, {}, value => { status = value; });
  assert.equal(status.connected, true);
  assert.equal(status.total, 1000);
  assert.equal(status.phase, 'Готово');
  const id = result.source;
  sent = []; await vm.runInContext('sync()', context);
  assert.equal(sent.at(-1).source, id);
  assert.equal(calls, 1000, 'only unavailable loaded tab is retried');
  tabUpdated(1, { status: 'loading' });
  await vm.runInContext('sync()', context);
  assert.equal(calls, 1002, 'a reloaded tab is read again');
  context.fetch = async () => { throw new Error('connection refused'); };
  await vm.runInContext('sync()', context);
  messageListener({ type: 'status' }, {}, value => { status = value; });
  assert.equal(status.connected, false, 'status reports a disconnected application');
  assert.equal(reloads, 0, 'automatic synchronization does not reload tabs');
  const resultLoad = await new Promise(resolve => {
    assert.equal(messageListener({ type: 'load-discarded' }, {}, resolve), true);
  });
  assert.equal(resultLoad.failed, 0);
  assert.equal(resultLoad.total, 1);
  assert.equal(reloads, 1, 'only unloaded Pinterest tabs are reloaded');
  assert.equal(tabs[9].discarded, false);
  tabs[9].discarded = true;
  api.tabs.reload = async () => { throw new Error('not allowed'); };
  const failedLoad = await new Promise(resolve => messageListener({ type: 'load-discarded' }, {}, resolve));
  assert.equal(failedLoad.failed, 1, 'reload failures are reported');
}
(async () => { await check('chrome'); await check('browser'); console.log('Chrome and Firefox sync, 1000 tabs and unloaded tabs: OK'); })()
  .catch(error => { console.error(error); process.exitCode = 1; });
