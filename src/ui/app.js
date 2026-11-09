import { loadTaskbar } from './taskbar.js';
import { ShaderPlayer } from './shader.js';
const $ = id => document.getElementById(id);
let data = { items: [] }, selected = null, editing = undefined, player, busy = false, dirty = false;
let currentRoute = 'overview';
let startupState = { supported: false, enabled: false };
function status(text, error = false) {
  for (const id of ['status', 'settings-status']) { $(id).textContent = text; $(id).classList.toggle('error', error); }
  if (error && $('settings-dialog').open) $('settings-status').scrollIntoView({ block: 'nearest' });
}
function showPlayback(state) {
  $('playback-state').hidden = !state?.paused;
  $('playback-state').textContent = state?.paused ? `Обои на паузе: ${{ covered: 'рабочий стол закрыт окном', battery: 'работа от батареи', locked: 'Windows заблокирована' }[state.reason]}.` : '';
}
function showSettings() {
  if (!data.settings) return;
  $('mouse-enabled').checked = data.settings.mouseEnabled;
  $('pause-covered').checked = data.settings.pauseWhenCovered;
  $('pause-battery').checked = data.settings.pauseOnBattery;
  $('fps').value = String(data.settings.fps);
  $('render-scale').value = String(data.settings.renderScale);
}
try { player = new ShaderPlayer($('canvas'), error => status(error, true)); } catch (error) { status(error.message, true); }
function buttons() {
  for (const id of ['apply', 'delete', 'edit']) $(id).disabled = busy || !selected || editing !== undefined;
  for (const id of ['add', 'save', 'preview', 'cancel', 'stop']) $(id).disabled = busy;
  $('apply').disabled ||= data.platform !== 'win32';
  $('list').inert = busy;
  $('startup').disabled = busy || !startupState.supported;
  $('mouse-enabled').disabled = busy || data.platform !== 'win32';
  for (const id of ['pause-covered','pause-battery','fps','render-scale']) $(id).disabled = busy;
  $('open-settings').disabled = busy;
  $('open-folder').disabled = busy;
  $('refresh-library').disabled = busy || editing !== undefined;
}
async function action(task) {
  if (busy) return;
  busy = true; buttons();
  try { await task(); } catch (error) { status(error.message.replace(/^Error invoking remote method '[^']+': Error: /, ''), true); }
  finally { busy = false; buttons(); }
}
function renderList() {
  $('count').textContent = data.items.length;
  $('overview-count').textContent = `${data.items.length} в коллекции`;
  $('list').replaceChildren();
  for (const item of data.items) {
    const button = document.createElement('button');
    button.textContent = item.name; button.classList.toggle('selected', selected?.id === item.id);
    button.setAttribute('aria-pressed', String(selected?.id === item.id));
    if (data.runningId === item.id) { const note = document.createElement('small'); note.textContent = '● На рабочем столе'; button.append(note); }
    button.onclick = () => {
      if (!leaveEditor()) return;
      select(item);
    };
    $('list').append(button);
  }
  buttons();
}
function select(item) {
  selected = item;
  $('title').textContent = item?.name || 'Ваша коллекция';
  $('empty').hidden = !!item;
  $('selection-info').textContent = item ? (data.runningId === item.id ? 'Сейчас на рабочем столе' : 'GLSL Sandbox · фрагментный шейдер') : 'Коллекция пока пуста';
  if (item && player) {
    try { player.load(item.code); status('Предпросмотр готов. Можно установить обои на рабочий стол.'); }
    catch (error) { status(error.message, true); }
  } else if (player) player.dispose();
  renderList();
}
async function refresh(id = selected?.id) {
  data = await window.magm.list();
  $('mouse-enabled').checked = data.settings.mouseEnabled;
  showSettings(); showPlayback(data.playback);
  select(data.items.find(i => i.id === id) || data.items[0] || null);
}
function leaveEditor() {
  if (dirty && !window.confirm('Отменить несохранённые изменения?')) return false;
  editing = undefined; dirty = false; $('editor').hidden = true; buttons(); return true;
}
function openEditor(item) {
  if (!leaveEditor()) return;
  editing = item?.id || null;
  $('editor-title').textContent = item ? 'Редактировать обои' : 'Новые обои';
  $('name').value = item?.name || '';
  $('code').value = item?.code || 'precision mediump float;\nuniform float time;\nuniform vec2 resolution;\nuniform vec2 mouse;\n\nvoid main() {\n    vec2 uv = gl_FragCoord.xy / resolution;\n    gl_FragColor = vec4(uv, 0.5 + 0.5 * sin(time), 1.0);\n}\n';
  $('editor').hidden = false; buttons(); $('name').focus();
  $('editor').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}
function preview() {
  if (!player) throw new Error('WebGL недоступен.');
  player.load($('code').value); $('empty').hidden = true;
}
$('name').oninput = $('code').oninput = () => { dirty = true; };
function updatePreviewPause() { if (player) player.paused = document.hidden || $('settings-dialog').open || currentRoute !== 'wallpapers'; }
function openSettings() {
  showSettings(); $('settings-status').textContent = '';
  if (!$('settings-dialog').open) $('settings-dialog').showModal();
  updatePreviewPause();
}
function navigate(route) {
  if (!['overview','wallpapers','taskbar'].includes(route)) return;
  currentRoute = route;
  for (const name of ['overview','wallpapers','taskbar']) $('page-'+name).hidden = route !== name;
  for (const button of document.querySelectorAll('[data-route]')) {
    if (button.dataset.route === route) button.setAttribute('aria-current','page');
    else button.removeAttribute('aria-current');
  }
  localStorage.setItem('magmpaper-section', route);
  updatePreviewPause();
  if (route === 'taskbar') void loadTaskbar();
}
for (const button of document.querySelectorAll('[data-route],[data-go]')) button.onclick = () => navigate(button.dataset.route || button.dataset.go);
navigate(localStorage.getItem('magmpaper-section') || 'overview');
$('open-settings').onclick = openSettings;
$('close-settings').onclick = () => $('settings-dialog').close();
$('settings-dialog').addEventListener('close', updatePreviewPause);
for (const [id, key] of [['pause-covered','pauseWhenCovered'], ['pause-battery','pauseOnBattery'], ['fps','fps'], ['render-scale','renderScale']]) {
  $(id).onchange = () => action(async () => {
    const value = $(id).type === 'checkbox' ? $(id).checked : Number($(id).value);
    try { data.settings = await window.magm.setSettings({ [key]: value }); status('Параметры сохранены.'); }
    finally { showSettings(); }
  });
}
$('mouse-enabled').onchange = () => action(async () => {
  try {
    data.settings.mouseEnabled = await window.magm.setMouseEnabled($('mouse-enabled').checked);
    status(data.settings.mouseEnabled ? 'Реакция обоев на мышь включена.' : 'Реакция обоев на мышь выключена.');
  } finally { $('mouse-enabled').checked = data.settings.mouseEnabled; }
});
$('startup').onchange = () => action(async () => {
  try {
    startupState = await window.magm.setStartup($('startup').checked);
    status(startupState.enabled ? 'Автозапуск включён. При входе в Windows восстановятся последние установленные обои.' : 'Автозапуск выключен.');
  } finally { $('startup').checked = startupState.enabled; }
});
$('open-folder').onclick = () => action(async () => { await window.magm.openFolder(); status('Открыта папка обоев. Изменения файлов появятся после обновления коллекции.'); });
$('refresh-library').onclick = () => action(async () => { await refresh(); status('Коллекция обновлена из GLSL-файлов.'); });
window.addEventListener('focus', () => { if (!busy && editing === undefined && currentRoute === 'wallpapers') void action(() => refresh()); });
$('add').onclick = () => openEditor(null);
$('edit').onclick = () => openEditor(selected);
$('cancel').onclick = () => { if (leaveEditor()) select(selected); };
$('preview').onclick = () => action(async () => { preview(); status('Шейдер скомпилирован. Проверьте изображение в предпросмотре.'); });
$('save').onclick = () => action(async () => {
  if (!$('name').value.trim()) throw new Error('Введите название обоев.');
  preview();
  const wasActive = editing && data.runningId === editing;
  const item = await window.magm.save({ id: editing || undefined, name: $('name').value, code: $('code').value });
  dirty = false; leaveEditor(); await refresh(item.id);
  status(wasActive ? 'Сохранено. Нажмите «На рабочий стол», чтобы применить изменения.' : 'Обои сохранены в коллекцию.');
});
$('apply').onclick = () => action(async () => {
  status('Запускаем обои…'); await window.magm.apply(selected.id); await refresh(); status('Обои установлены на основной монитор.');
});
$('stop').onclick = () => action(async () => { await window.magm.stop(); await refresh(); status('Обои остановлены.'); });
$('delete').onclick = () => { $('delete-name').textContent = selected.name; $('confirm-delete').showModal(); };
$('keep').onclick = () => $('confirm-delete').close();
$('confirm').onclick = () => action(async () => {
  $('confirm-delete').close(); await window.magm.remove(selected.id); selected = null; await refresh(); status('Обои удалены.');
});
window.magm.onStatus(message => {
  status(message);
  window.magm.list().then(value => { data = value; renderList(); }).catch(error => status(error.message, true));
});
window.magm.onPlayback(showPlayback);
window.magm.onOpenSettings(openSettings);
document.addEventListener('visibilitychange', updatePreviewPause);
window.addEventListener('beforeunload', () => player?.dispose());
await action(async () => {
  await refresh();
  startupState = await window.magm.startup();
  $('startup').checked = startupState.enabled;
  if (!startupState.supported) $('startup-hint').textContent = 'Автозапуск доступен только в Windows.';
});
