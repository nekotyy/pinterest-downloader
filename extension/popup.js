const api = globalThis.browser || chrome;
const connection = document.querySelector('#connection'), progress = document.querySelector('#progress'), browser = document.querySelector('#browser');
let chosen = false;
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
refresh(); setInterval(refresh, 1000);
