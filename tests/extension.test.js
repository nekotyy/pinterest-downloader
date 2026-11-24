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
  const alarms = new Map();
  const events = { addListener() {} };
  const tabs = Array.from({ length: 1000 }, (_, i) => ({ id: i + 1, url: `https://ru.pinterest.com/pin/${i + 1}/`, title: `Пин ${i + 1}`, discarded: i === 9 }));
  tabs.push({ id: 1001, url: tabs[0].url, title: 'Дубликат' }, { id: 1002, url: 'https://evil.example/pin/55/' });
  const storage = {};
  const api = {
    tabs: { query: async () => tabs, reload: async id => { reloads++; tabs.find(tab => tab.id === id).discarded = false; }, onUpdated: { addListener(fn) { tabUpdated = fn; } }, onRemoved: events },
    scripting: { executeScript: async ({ target }) => { active++; calls++; maxActive = Math.max(active, maxActive); await Promise.resolve(); active--; if (target.tabId === 4) throw Error('unavailable'); return [{ result: `https://i.pinimg.com/${target.tabId}.jpg` }]; } },
    action: { setBadgeText: async () => {} },
    runtime: { onStartup: events, onInstalled: events, onMessage: { addListener(fn) { messageListener = fn; } } },
    alarms: { onAlarm: events, create: (name, value) => { alarms.set(name, value.periodInMinutes); }, clear: async name => { alarms.delete(name); } },
    storage: { local: { get: async () => ({ ...storage }), set: async data => Object.assign(storage, data) } }
  };
  const context = { [browserMode]: api, navigator: { userAgent: browserMode === 'browser' ? 'Firefox/120' : 'Chrome/120' }, crypto,
    setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
    fetch: async (url, options) => { sent.push(JSON.parse(options.body)); return { ok: true }; } };
  vm.createContext(context);
  vm.runInContext(source, context);
  const ask = message => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('extension did not respond')), 1000);
    messageListener(message, {}, value => { clearTimeout(timer); resolve(value); });
  });
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
  const savedSettings = await ask({ type: 'settings-save', concurrency: 2, interval: 5, reloadBatch: 1 });
  assert.equal(savedSettings.concurrency, 2);
  assert.equal(storage.interval, 5);
  assert.equal(alarms.get('sync'), 5, 'saved interval configures automatic synchronization');
  maxActive = 0;
  vm.runInContext('cache.clear()', context);
  await vm.runInContext('sync()', context);
  assert.ok(maxActive <= 2, 'the chosen scan concurrency limits active requests');
  const invalidSettings = await ask({ type: 'settings-save', concurrency: 999, interval: -1, reloadBatch: 999 });
  assert.equal(invalidSettings.concurrency, 6);
  assert.equal(invalidSettings.interval, 0.5);
  assert.equal(invalidSettings.reloadBatch, 3);
  await ask({ type: 'settings-save', concurrency: 1, interval: 0, reloadBatch: 1 });
  assert.equal(alarms.has('sync'), false, 'manual mode disables the automatic scan alarm');
  assert.equal(alarms.get('commands'), 0.5, 'manual mode keeps command polling active');
  let commandRequest;
  context.fetch = async (url, options) => { commandRequest = { url, body: JSON.parse(options.body) }; return { ok: true, status: 204 }; };
  const previousCalls = calls;
  await vm.runInContext('checkCommands()', context);
  assert.ok(commandRequest.url.endsWith('/commands'));
  assert.equal(commandRequest.body.source, id);
  assert.equal(commandRequest.body.pins, undefined, 'command polling does not resend the tab list');
  assert.equal(calls, previousCalls, 'command polling does not scan tabs');
}

async function checkClose(browserMode) {
  const storage = { sourceId: '12345678-1234-1234-1234-123456789abc' }, removed = [], sent = [];
  const events = { addListener() {} };
  const tabs = [
    { id: 1, url: 'https://www.pinterest.com/pin/123/', title: 'One' },
    { id: 2, url: 'https://www.pinterest.com/pin/456/', title: 'Other pin' },
    { id: 3, url: 'https://ru.pinterest.com/pin/123/', title: 'Duplicate' },
    { id: 4, url: 'https://example.com/pin/123/', title: 'Other site' }
  ];
  const api = {
    tabs: { query: async () => tabs, remove: async id => { removed.push(id); if (id === 3) throw Error('not allowed'); }, onUpdated: events, onRemoved: events },
    scripting: { executeScript: async () => [{ result: 'https://i.pinimg.com/image.jpg' }] },
    action: { setBadgeText: async () => {} }, runtime: { onStartup: events, onInstalled: events, onMessage: events },
    alarms: { onAlarm: events, create() {} }, storage: { local: { get: async () => ({ ...storage }), set: async data => Object.assign(storage, data) } }
  };
  const command = { id: 'a'.repeat(32), pins: ['123'], expiresAt: Date.now() + 60000 };
  let responseCommand = command;
  const context = { [browserMode]: api, navigator: { userAgent: 'Chrome/120' }, crypto,
    setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
    fetch: async (url, options) => { sent.push(JSON.parse(options.body)); return { ok: true, status: 200, json: async () => responseCommand }; } };
  vm.createContext(context); vm.runInContext(source, context);
  await vm.runInContext('sync()', context);
  await vm.runInContext('sync()', context);
  assert.deepEqual(removed, [1, 3], 'only matching Pinterest tabs are attempted and repeated commands do not close tabs again');
  assert.equal(storage.handledClose.closed, 1);
  assert.equal(storage.handledClose.failed, 1);
  assert.ok(sent.some(item => item.ack?.id === command.id && item.ack.closed === 1 && item.ack.failed === 1));
  responseCommand = { id: 'b'.repeat(32), pins: ['456'], expiresAt: Date.now() + 60000 };
  await vm.runInContext('sync()', context);
  responseCommand = command;
  await vm.runInContext('sync()', context);
  assert.deepEqual(removed, [1, 3, 2], 'a delayed response from an older command is not executed again');
  responseCommand = { id: 'c'.repeat(32), pins: ['456'], expiresAt: Date.now() - 1 };
  await vm.runInContext('sync()', context);
  assert.deepEqual(removed, [1, 3, 2], 'expired commands do not close tabs');
  responseCommand = { id: 'd'.repeat(32), pins: ['456'], expiresAt: Date.now() + 60000 };
  storage.handledClose = { id: responseCommand.id, finished: false };
  storage.handledCloseIds = [{ id: responseCommand.id, expiresAt: responseCommand.expiresAt }];
  await vm.runInContext('sync()', context);
  assert.deepEqual(removed, [1, 3, 2], 'interrupted commands are not replayed');
  assert.ok(sent.some(item => item.ack?.id === responseCommand.id && item.ack.failed === -1), 'interrupted command is reported');
}

async function checkConcurrentHeartbeat() {
  const storage = { sourceId: '12345678-1234-1234-1234-123456789abc' }, sent = [], events = { addListener() {} };
  let release, started, polled, removed = 0;
  const closeStarted = new Promise(resolve => { started = resolve; });
  const closeGate = new Promise(resolve => { release = resolve; });
  const commandPolled = new Promise(resolve => { polled = resolve; });
  const command = { id: 'e'.repeat(32), pins: ['123'], expiresAt: Date.now() + 60000 };
  const api = {
    tabs: { query: async () => [{ id: 1, url: 'https://www.pinterest.com/pin/123/', title: 'Pin' }], remove: async () => { removed++; started(); await closeGate; }, onUpdated: events, onRemoved: events },
    scripting: { executeScript: async () => [{ result: 'https://i.pinimg.com/image.jpg' }] },
    action: { setBadgeText: async () => {} }, runtime: { onStartup: events, onInstalled: events, onMessage: events },
    alarms: { onAlarm: events, create() {} }, storage: { local: { get: async () => ({ ...storage }), set: async data => Object.assign(storage, data) } }
  };
  const context = { chrome: api, navigator: { userAgent: 'Chrome/120' }, crypto,
    setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
    fetch: async (url, options) => { const body = JSON.parse(options.body); sent.push({ url, body }); if (url.endsWith('/commands')) polled(); return { ok: true, status: 200, json: async () => command }; } };
  vm.createContext(context); vm.runInContext(source, context);
  const wait = promise => Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error('timed out waiting for extension')), 2000))]);
  const sync = vm.runInContext('sync()', context);
  await wait(closeStarted);
  const heartbeat = vm.runInContext('checkCommands()', context);
  await wait(commandPolled);
  assert.equal(sent.find(item => item.url.endsWith('/commands')).body.ack, null, 'an active command is not acknowledged as interrupted');
  assert.equal(storage.handledClose.finished, false);
  release(); await wait(Promise.all([sync, heartbeat]));
  assert.equal(removed, 1, 'concurrent heartbeat does not repeat the close command');
  assert.equal(storage.handledClose.closed, 1);
  assert.equal(storage.handledClose.failed, 0);
}
async function checkFirstRunIdentity() {
  const storage = {}, sent = [], events = { addListener() {} };
  const api = {
    tabs: { query: async () => [], onUpdated: events, onRemoved: events },
    action: { setBadgeText: async () => {} }, runtime: { onStartup: events, onInstalled: events, onMessage: events },
    alarms: { onAlarm: events, create() {} },
    storage: { local: { get: async () => { const snapshot = { ...storage }; await Promise.resolve(); return snapshot; }, set: async data => Object.assign(storage, data) } }
  };
  const context = { chrome: api, navigator: { userAgent: 'Chrome/120' }, crypto,
    setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
    fetch: async (url, options) => { sent.push(JSON.parse(options.body)); return { ok: true, status: 204 }; } };
  vm.createContext(context); vm.runInContext(source, context);
  await Promise.all([vm.runInContext('sync()', context), vm.runInContext('checkCommands()', context)]);
  assert.equal(new Set(sent.map(item => item.source)).size, 1, 'first synchronization and heartbeat use the same profile ID');
}
(async () => { await check('chrome'); await check('browser'); await checkClose('chrome'); await checkClose('browser'); await checkConcurrentHeartbeat(); await checkFirstRunIdentity(); console.log('Browser synchronization, unloaded tabs and safe tab closing: OK'); })()
  .catch(error => { console.error(error); process.exitCode = 1; });
