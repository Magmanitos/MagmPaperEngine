import {loadComponent} from './taskbar-component.js';
const $ = id => document.getElementById(id);
const sizeKeys=['height','icon','button','smallIcon','smallButton'];
const nativeLabels={alignment:'Расположение кнопок',search:'Поиск',autoHide:'Автоматическое скрытие',taskView:'Представление задач',widgets:'Виджеты'};
let state=null, busy=false;
function status(message,error=false) { $('taskbar-status').textContent=message; $('taskbar-status').classList.toggle('error',error); }
function controls() {
  $('taskbar-native-fields').disabled=busy||!state?.supported;
  $('taskbar-size-fields').disabled=busy||!state?.sizesAvailable;
  $('taskbar-apply-native').disabled=busy||!state?.supported;
  $('taskbar-apply-sizes').disabled=busy||!state?.sizesAvailable;
  $('taskbar-restore-native').disabled=busy||!state?.canRestoreNative;
  $('taskbar-restore-sizes').disabled=busy||!state?.canRestoreSizes||!state?.sizesAvailable;
  $('taskbar-refresh').disabled=busy;
  $('taskbar-windows').disabled=busy||!state?.supported;
}
function preview() {
  const model=$('taskbar-model');
  model.style.setProperty('--bar-height',Math.max(32,Math.min(96,Number($('taskbar-height').value)||48))+'px');
  model.style.setProperty('--icon-size',Math.max(16,Math.min(64,Number($('taskbar-icon').value)||24))+'px');
  model.style.setProperty('--button-width',Math.max(28,Math.min(100,Number($('taskbar-button').value)||44))+'px');
}
function render() {
  $('legacy-taskbar').hidden=!state?.canRestoreSizes&&!state?.sizesAvailable;
  if(state?.supported) {
    $('taskbar-alignment').value=String(state.native.alignment);
    $('taskbar-search').value=String(state.native.search);
    $('taskbar-autohide').checked=state.native.autoHide;
    $('taskbar-taskview').checked=!!state.native.taskView;
    $('taskbar-widgets').checked=!!state.native.widgets;
    for(const key of sizeKeys) $('taskbar-'+key).value=state.sizes[key];
  }
  $('taskbar-component').textContent=state?.sizesAvailable?'Мод найден и включён. Размеры доступны.':'Компонент размеров не найден или выключен. Установите Windhawk, включите мод и нажмите «Обновить».';
  controls();
}
async function action(task) {
  if(busy)return;
  busy=true;controls();
  try { await task(); }
  catch(error){status(error.message.replace(/^Error invoking remote method '[^']+': Error: /,''),true);}
  finally{busy=false;controls();}
}
export async function loadTaskbar(force=false) {
  if(state&&!force)return;
  await action(async()=>{state=await window.magm.taskbar();render();status(state.supported?'Параметры прочитаны. Изменения применяются только по кнопке.':'Этот раздел доступен в Windows 11.');});
  await loadComponent(force);
}
$('taskbar-refresh').onclick=()=>loadTaskbar(true);
function nativeStatus(result) {
  const report=result.nativeResult;
  if(!report) { status('Параметры Windows прочитаны.');return; }
  const parts=[];
  if(report.changed.length) parts.push(`${report.mode==='restore'?'Восстановлено':'Сохранено'}: ${report.changed.map(key=>nativeLabels[key]).join(', ')}.`);
  if(report.failed.length) parts.push(...report.failed.map(item=>item.message));
  if(!report.changed.length&&!report.failed.length) parts.push(report.mode==='restore'?'Исходные значения уже установлены.':'Нет изменений для применения.');
  if(report.failed.length&&report.mode==='restore') parts.push('Исходные значения недоступных пунктов сохранены для повторного возврата.');
  if(report.refreshWarning) parts.push(report.refreshWarning);
  else if(report.changed.some(key=>key!=='autoHide')) parts.push('Если изменения ещё не видны, выйдите из учётной записи Windows и войдите снова.');
  status(parts.join('\n'),report.failed.length>0);
}
$('taskbar-apply-native').onclick=()=>action(async()=>{
  const values={alignment:Number($('taskbar-alignment').value),search:Number($('taskbar-search').value),autoHide:$('taskbar-autohide').checked,taskView:Number($('taskbar-taskview').checked),widgets:Number($('taskbar-widgets').checked)};
  const patch=Object.fromEntries(Object.entries(values).filter(([key,value])=>value!==state.native[key]));
  if(!Object.keys(patch).length) { status('Нет изменений для применения.');return; }
  state=await window.magm.setTaskbar(patch);
  render();nativeStatus(state);
});
$('taskbar-apply-sizes').onclick=()=>action(async()=>{
  const values=Object.fromEntries(sizeKeys.map(key=>[key,Number($('taskbar-'+key).value)]));
  if(values.icon>values.height-8||values.button<values.icon||values.smallButton<values.smallIcon) throw new Error('Значки должны помещаться в панель и кнопки. Увеличьте высоту или ширину.');
  status('Подтвердите запрос Windows для записи настроек Windhawk…');
  state=await window.magm.setTaskbarSizes(values);render();status('Размеры сохранены и переданы Windhawk. Для отображения мод и Windhawk должны быть включены.');
});
for(const group of ['native','sizes']) $('taskbar-restore-'+group).onclick=()=>action(async()=>{
  state=await window.magm.restoreTaskbar(group);render();
  if(group==='native') nativeStatus(state);
  else status('Исходные значения восстановлены. Для некоторых настроек Windows может потребоваться повторный вход в систему.');
});
$('taskbar-help').onclick=()=>action(async()=>{await window.magm.taskbarHelp();status('На странице компонента установите Windhawk и мод Taskbar height and icon size. Затем вернитесь и нажмите «Обновить».');});
$('taskbar-windows').onclick=()=>action(()=>window.magm.taskbarWindows());
for(const key of sizeKeys) $('taskbar-'+key).addEventListener('input',preview);
const presets={compact:[36,20,36,16,28],standard:[48,24,44,16,32],large:[60,40,52,20,36]};
for(const button of document.querySelectorAll('[data-taskbar-preset]')) button.onclick=()=>{presets[button.dataset.taskbarPreset].forEach((value,i)=>$('taskbar-'+sizeKeys[i]).value=value);preview();};
controls();preview();
