const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

let clicked, sent;
const event = { addListener() {} };
const chrome = {
  tabs: { query: async () => [
    { id: 1, url: 'https://ru.pinterest.com/pin/123/', title: 'Один' },
    { id: 2, url: 'https://www.pinterest.com/pin/123/', title: 'Дубликат' },
    { id: 3, url: 'https://www.pinterest.com/pin/456/', title: 'Два' },
    { id: 4, url: 'https://evil.example/pin/789/' }
  ], onUpdated: event, onRemoved: event },
  scripting: { executeScript: async ({ target }) => target.tabId === 3 ? Promise.reject(new Error('unavailable')) : [{ result: 'https://i.pinimg.com/one.jpg' }] },
  action: { onClicked: { addListener(fn) { clicked = fn; } }, setBadgeText: async () => {} },
  runtime: { onStartup: event, onInstalled: event }, alarms: { onAlarm: event, create() {} }
};
const context = { chrome, fetch: async (url, options) => { sent = JSON.parse(options.body); } };
vm.runInNewContext(fs.readFileSync('extension/background.js', 'utf8'), context);
(async () => {
  await clicked();
  assert.equal(sent.length, 2);
  assert.equal(sent[0].id, '123');
  assert.equal(sent[0].image, 'https://i.pinimg.com/one.jpg');
  assert.equal(sent[1].id, '456');
  assert.equal(sent[1].image, '');
  console.log('Extension sync: OK');
})().catch(error => { console.error(error); process.exitCode = 1; });
