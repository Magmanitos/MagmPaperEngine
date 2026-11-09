const $=id=>document.getElementById(id);
const keys=['height','icon','button','color','opacity','hideWidgets','radius','enabled'];
let state=null,busy=false,dirty=false;
function message(text,error=false){$('appearance-status').textContent=text;$('appearance-status').classList.toggle('error',error);}
function controls(){
  $('appearance-fields').disabled=busy||!state?.available;
  $('appearance-apply').disabled=busy||!state?.available;
  $('appearance-disable').disabled=busy||!state?.settings?.enabled;
}
function values(){return Object.fromEntries(keys.map(key=>[key,key==='enabled'||key==='hideWidgets'?$('appearance-'+key).checked:key==='color'?$('appearance-'+key).value:Number($('appearance-'+key).value)]));}
function preview(){
  const next=values(),model=$('taskbar-model');
  model.style.setProperty('--bar-height',Math.max(32,Math.min(96,next.height||48))+'px');
  model.style.setProperty('--icon-size',Math.max(16,Math.min(64,next.icon||24))+'px');
  model.style.setProperty('--button-width',Math.max(28,Math.min(100,next.button||44))+'px');
  const color=next.color.slice(1);model.style.backgroundColor=`rgba(${parseInt(color.slice(0,2),16)},${parseInt(color.slice(2,4),16)},${parseInt(color.slice(4,6),16)},${Math.max(0,Math.min(100,next.opacity))/100})`;
  model.style.borderRadius=next.radius+'px';
}
function render(){
  if(state?.settings) for(const key of keys){if(typeof state.settings[key]==='boolean')$('appearance-'+key).checked=state.settings[key];else $('appearance-'+key).value=state.settings[key];}
  dirty=false;preview();controls();
}
function runtimeMessage(){
  if(state?.error) message(state.error,true);
  else if(state?.mode==='starting')message('Подключение встроенного модуля к панели Windows…');
  else if(state?.active)message('Оформление MagmPaper работает. Дополнительные приложения не нужны.');
  else if(state?.available)message('Модуль установлен вместе с MagmPaper. Выберите оформление и нажмите «Применить».');
  else message('Нужна полная сборка MagmPaper для Windows 11 x64 со встроенным модулем.',true);
}
export async function loadComponent(force=false){
  if(busy||(state&&!force))return;
  try{state=await window.magm.taskbarComponent();render();runtimeMessage();}catch(error){message(error.message,true);}
}
async function apply(next){
  if(busy)return;busy=true;controls();message(next.enabled?'Подключение и применение оформления…':'Возврат оформления Windows…');
  try{state=await window.magm.setTaskbarComponent(next);render();message(next.enabled?'Оформление применено встроенным модулем.':'Исходное оформление возвращено. Настройки поведения Windows сохранены.');}
  catch(error){try{state=await window.magm.taskbarComponent();render();}catch{}message(error.message.replace(/^Error invoking remote method '[^']+': Error: /,''),true);}
  finally{busy=false;controls();}
}
$('appearance-apply').onclick=()=>apply(values());
$('appearance-disable').onclick=()=>apply({...state.settings,enabled:false});
for(const key of keys)$('appearance-'+key).oninput=()=>{dirty=true;preview();};
const presets={compact:[36,20,36],standard:[48,24,44],large:[64,40,52]};
for(const button of document.querySelectorAll('[data-appearance-preset]'))button.onclick=()=>{presets[button.dataset.appearancePreset].forEach((value,i)=>$('appearance-'+['height','icon','button'][i]).value=value);dirty=true;preview();};
window.magm.onTaskbarComponent(value=>{state=value;if(!busy&&!dirty)render();runtimeMessage();controls();});
controls();
