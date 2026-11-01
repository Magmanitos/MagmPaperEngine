import { ShaderPlayer } from './shader.js';
const $ = id => document.getElementById(id);
let data = { items: [] }, selected = null, editing = undefined, player, busy = false, dirty = false;
function status(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); }
try { player = new ShaderPlayer($('canvas'), error => status(error, true)); } catch (error) { status(error.message, true); }
function buttons() {
  for (const id of ['apply', 'delete', 'edit']) $(id).disabled = busy || !selected || editing !== undefined;
  for (const id of ['add', 'save', 'preview', 'cancel', 'stop']) $(id).disabled = busy;
  $('apply').disabled ||= data.platform !== 'win32';
  $('list').inert = busy;
}
async function action(task) {
  if (busy) return;
  busy = true; buttons();
  try { await task(); } catch (error) { status(error.message.replace(/^Error invoking remote method '[^']+': Error: /, ''), true); }
  finally { busy = false; buttons(); }
}
function renderList() {
  $('count').textContent = data.items.length;
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
document.addEventListener('visibilitychange', () => { if (player) player.paused = document.hidden; });
window.addEventListener('beforeunload', () => player?.dispose());
await action(() => refresh());
