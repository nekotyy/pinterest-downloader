const api = globalThis.browser || chrome;
const connection = document.querySelector('#connection'), progress = document.querySelector('#progress'), browser = document.querySelector('#browser');
let chosen = false;
async function readSettings() {
  try {
    const settings = await api.runtime.sendMessage({ type: 'settings-get' });
    for (const name of ['concurrency', 'interval', 'reloadBatch']) document.querySelector(`#${name}`).value = String(settings[name]);
  } catch (_) { document.querySelector('#settings-status').textContent = 'Не удалось прочитать настройки.'; }
}
async function refresh() {
  try {
    const state = await api.runtime.sendMessage({ type: 'status' });
    connection.textContent = state.connected ? 'Приложение подключено' : 'Приложение не подключено';
    connection.classList.toggle('online', state.connected);
    progress.textContent = `${state.phase}: ${state.done} из ${state.total} вкладок`;
    if (!chosen && state.browser) browser.value = [...browser.options].some(x => x.value === state.browser) ? state.browser : 'Другой браузер';
  } catch (_) { connection.textContent = 'Расширение запускается'; }
}
browser.addEventListener('change', () => { chosen = true; api.runtime.sendMessage({ type: 'browser', name: browser.value }); });
document.querySelector('#sync').addEventListener('click', () => { api.runtime.sendMessage({ type: 'sync' }); setTimeout(refresh, 500); });
document.querySelector('#load').addEventListener('click', async event => {
  const button = event.currentTarget;
  button.disabled = true;
  const result = document.querySelector('#result');
  result.textContent = 'Запрашиваем загрузку вкладок...';
  try {
    const response = await api.runtime.sendMessage({ type: 'load-discarded' });
    result.textContent = response.busy ? 'Дождитесь завершения обработки.' : response.error ? 'Не удалось запросить загрузку вкладок.' : `Обработано: ${response.total}. Ошибок: ${response.failed}.`;
  } catch (_) { result.textContent = 'Не удалось запросить загрузку вкладок.'; }
  button.disabled = false;
  refresh();
});
document.querySelector('#save').addEventListener('click', async () => {
  const values = Object.fromEntries(['concurrency', 'interval', 'reloadBatch'].map(name => [name, Number(document.querySelector(`#${name}`).value)]));
  try {
    const result = await api.runtime.sendMessage({ type: 'settings-save', ...values });
    document.querySelector('#settings-status').textContent = result.error ? 'Не удалось сохранить настройки.' : 'Настройки сохранены.';
  } catch (_) { document.querySelector('#settings-status').textContent = 'Не удалось сохранить настройки.'; }
});
readSettings(); refresh(); setInterval(refresh, 1000);
