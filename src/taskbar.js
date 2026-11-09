const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const run = promisify(execFile);
const ADVANCED = 'Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced';
const SEARCH = 'Software\\Microsoft\\Windows\\CurrentVersion\\Search';
const MOD = 'SOFTWARE\\Windhawk\\Engine\\Mods\\taskbar-icon-size';
const NATIVE = { alignment: [ADVANCED, 'TaskbarAl'], search: [SEARCH, 'SearchboxTaskbarMode'], taskView: [ADVANCED, 'ShowTaskViewButton'], widgets: [ADVANCED, 'TaskbarDa'] };
const NATIVE_DEFAULTS = { alignment:1, search:2, taskView:1, widgets:1 };
const NATIVE_LABELS = { alignment:'Расположение кнопок', search:'Поиск', taskView:'Представление задач', widgets:'Виджеты', autoHide:'Автоматическое скрытие' };
const SIZE = { height: ['TaskbarHeight', 32, 96, 48], icon: ['IconSize', 16, 64, 24], button: ['TaskbarButtonWidth', 28, 100, 44], smallIcon: ['IconSizeSmall', 16, 32, 16], smallButton: ['TaskbarButtonWidthSmall', 24, 64, 32] };
function validateNative(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Некорректные параметры панели задач.');
  for (const [key, value] of Object.entries(input)) {
    if (!(Object.hasOwn(NATIVE,key)) && key !== 'autoHide') throw new Error('Неизвестный параметр панели задач.');
    if (key === 'autoHide' ? typeof value !== 'boolean' : !Number.isInteger(value) || !(key === 'search' ? [0,1,2,3] : [0,1]).includes(value)) throw new Error('Некорректное значение панели задач.');
  }
  return input;
}
function validateSizes(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).length !== Object.keys(SIZE).length) throw new Error('Укажите все размеры панели задач.');
  for (const [key, value] of Object.entries(input)) {
    const spec = Object.hasOwn(SIZE,key) ? SIZE[key] : null;
    if (!spec || !Number.isInteger(value) || value < spec[1] || value > spec[2]) throw new Error('Размер вне допустимого диапазона.');
  }
  if (input.icon > input.height - 8 || input.button < input.icon || input.smallButton < input.smallIcon) throw new Error('Значки должны помещаться по высоте панели (отступ 8 px) и ширине кнопки.');
  return input;
}
let native;
function windowsApi() {
  if (native) return native;
  const k = require('koffi'), adv = k.load('advapi32.dll'), user = k.load('user32.dll'), shell = k.load('shell32.dll');
  const rect = k.struct('TaskbarRect', { left:'int32_t',top:'int32_t',right:'int32_t',bottom:'int32_t' });
  const appbar = k.struct('MagmAppbar', { size:'uint32_t', hwnd:'void *', callback:'uint32_t', edge:'uint32_t', rect, param:'intptr_t' });
  const get = adv.func('int32_t __stdcall RegGetValueW(void *key, str16 subkey, str16 name, uint32_t flags, void *type, _Out_ uint32_t *data, _Inout_ uint32_t *size)');
  const open = adv.func('int32_t __stdcall RegOpenKeyExW(void *key, str16 subkey, uint32_t options, uint32_t access, _Out_ void **result)');
  const create = adv.func('int32_t __stdcall RegCreateKeyExW(void *key, str16 subkey, uint32_t reserved, str16 className, uint32_t options, uint32_t access, void *security, _Out_ void **result, void *disposition)');
  const set = adv.func('int32_t __stdcall RegSetValueExW(void *key, str16 name, uint32_t reserved, uint32_t type, uint32_t *data, uint32_t size)');
  const remove = adv.func('int32_t __stdcall RegDeleteValueW(void *key, str16 name)');
  const close = adv.func('int32_t __stdcall RegCloseKey(void *key)');
  const currentUser=BigInt.asUintN(64,-2147483647n);
  function check(code) { if(code) { const error=new Error(`Ошибка Windows (код ${code}).`); error.win32Code=code; throw error; } }
  const appMessage = shell.func('uintptr_t __stdcall SHAppBarMessage(uint32_t message, MagmAppbar *data)');
  const broadcast = user.func('intptr_t __stdcall SendMessageTimeoutW(void *hwnd, uint32_t msg, uintptr_t wparam, str16 lparam, uint32_t flags, uint32_t timeout, _Out_ uintptr_t *result)');
  const find = user.func('void * __stdcall FindWindowW(str16 className, str16 title)');
  function bar(message, param=0) {
    const hwnd = find('Shell_TrayWnd', null);
    if (!hwnd) throw new Error('Панель задач Windows сейчас недоступна.');
    return Number(appMessage(message, { size:k.sizeof(appbar), hwnd, callback:0, edge:0, rect:{left:0,top:0,right:0,bottom:0}, param }));
  }
  native = {
    read(hive, key, name) {
      const data=[0], size=[4];
      const result=get(BigInt.asUintN(64, hive === 'HKCU' ? -2147483647n : -2147483646n),key,name,0x10010,null,data,size);
      if (result === 2 || result === 3) return null;
      if (result) throw new Error(`Не удалось прочитать параметры Windows (код ${result}).`);
      return data[0];
    },
    writeUserDword(subkey, name, value) {
      const key=[null];
      let code=open(currentUser,subkey,0,0x102,key); // KEY_SET_VALUE | KEY_WOW64_64KEY
      if(code===2 || code===3) {
        if(value===null) return;
        code=create(currentUser,subkey,0,null,0,0x102,null,key,null);
      }
      check(code);
      try {
        const result=value===null ? remove(key[0],name) : set(key[0],name,0,4,[value],4);
        if(value!==null || result!==2) check(result);
      } finally { close(key[0]); }
    },
    autoHide: () => !!(bar(4) & 1),
    setAutoHide: enabled => { const state=bar(4); bar(10,enabled ? state|1 : state&~1); },
    notify: () => broadcast(0xffffn,0x1a,0,'TraySettings',2,1000,[0])
  };
  return native;
}
function sizeScript(values) {
  // No executable paths or source text are accepted from the renderer.
  const allowed=Object.values(SIZE).map(s=>s[0]);
  for (const [key,value] of Object.entries(values)) if (!allowed.includes(key) || (value !== null && (!Number.isInteger(value) || value<0 || value>0xffffffff))) throw new Error('Некорректная копия параметров.');
  const lines=Object.entries(values).map(([key,value]) => value === null ? `if ($entry.GetValueNames() -contains '${key}') { Remove-ItemProperty -LiteralPath $settings -Name '${key}' }` : `New-ItemProperty -LiteralPath $settings -Name '${key}' -Value ([uint32]${value}) -PropertyType DWord -Force | Out-Null`);
  return `$ErrorActionPreference='Stop'\n$mod='Registry::HKEY_LOCAL_MACHINE\\${MOD}'\n$settings=$mod+'\\Settings'\nif (!(Test-Path -LiteralPath $settings)) { exit 2 }\n$entry=Get-Item -LiteralPath $settings\n$old=@{}\n${allowed.map(key=>`$old['${key}']=$entry.GetValue('${key}',$null)`).join('\n')}\ntry {\n${lines.join('\n')}\n$previous=(Get-Item -LiteralPath $mod).GetValue('SettingsChangeTime',0)\n$stamp=[int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -band 2147483647)\nif ($stamp -le $previous) { $stamp=($previous+1) -band 2147483647 }\nNew-ItemProperty -LiteralPath $mod -Name SettingsChangeTime -Value $stamp -PropertyType DWord -Force | Out-Null\nexit 0\n} catch { foreach($key in $old.Keys) { if ($null -eq $old[$key]) { if ($entry.GetValueNames() -contains $key) { Remove-ItemProperty -LiteralPath $settings -Name $key } } else { New-ItemProperty -LiteralPath $settings -Name $key -Value $old[$key] -PropertyType DWord -Force | Out-Null } }; exit 1 }`;
}
function elevationCommand(values) {
  const encoded=Buffer.from(sizeScript(values),'utf16le').toString('base64');
  return `try { $p=Start-Process -FilePath "$PSHOME\\powershell.exe" -Verb RunAs -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','${encoded}' -Wait -PassThru; exit $p.ExitCode } catch { exit 1223 }`;
}
async function writeSizes(values) {
  try { await run('powershell.exe',['-NoProfile','-NonInteractive','-Command',elevationCommand(values)],{windowsHide:true,timeout:180000}); }
  catch { throw new Error('Размеры не применены. Подтвердите запрос Windows, проверьте Windhawk и повторите.'); }
}
function createTaskbar(directory, platform=process.platform, adapter) {
  const file=path.join(directory,'taskbar-backup.json');
  const supported=platform==='win32' && Number(os.release().split('.')[2])>=22000;
  const api=()=>adapter || windowsApi();
  let backup=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):{native:{},sizes:null};
  if (!backup || !backup.native || typeof backup.native!=='object' || Array.isArray(backup.native)) throw new Error('Повреждена резервная копия панели задач.');
  for(const [key,value] of Object.entries(backup.native)) {
    if(key==='autoHide' ? typeof value!=='boolean' : !Object.hasOwn(NATIVE,key) || (value!==null && (!Number.isInteger(value)||value<0||value>0xffffffff))) throw new Error('Повреждена резервная копия панели задач.');
  }
  if(backup.sizes!==null) {
    if(!backup.sizes || typeof backup.sizes!=='object' || Array.isArray(backup.sizes)) throw new Error('Повреждена резервная копия размеров.');
    sizeScript(backup.sizes);
  }
  function saveBackup() { fs.mkdirSync(directory,{recursive:true}); fs.writeFileSync(file+'.tmp',JSON.stringify(backup,null,2)); fs.renameSync(file+'.tmp',file); }
  function assertSupported() { if (!supported && !adapter) throw new Error('Настройки доступны в Windows 11.'); }
  function state() {
    if (!supported && !adapter) return {supported:false,build:os.release(),sizesAvailable:false};
    const a=api(), raw=Object.fromEntries(Object.entries(NATIVE).map(([key,[sub,name]])=>[key,a.read('HKCU',sub,name)]));
    const settings=Object.fromEntries(Object.entries(SIZE).map(([key,spec])=>[key,a.read('HKLM',MOD+'\\Settings',spec[0])]));
    const disabled=a.read('HKLM',MOD,'Disabled');
    return {supported:true,build:os.release(),native:{alignment:raw.alignment??1,search:raw.search??2,taskView:raw.taskView??1,widgets:raw.widgets??1,autoHide:a.autoHide()},sizes:Object.fromEntries(Object.entries(SIZE).map(([key,spec])=>[key,settings[key]??spec[3]])),sizesAvailable:disabled===0 && settings.height!==null && settings.icon!==null,canRestoreNative:Object.keys(backup.native).length>0,canRestoreSizes:!!backup.sizes};
  }
  function readNative(key) { return key==='autoHide' ? api().autoHide() : api().read('HKCU',...NATIVE[key]); }
  async function writeNative(key,value) {
    if(key==='autoHide') return api().setAutoHide(value);
    if(adapter) return adapter.write(key,value);
    return api().writeUserDword(...NATIVE[key],value);
  }
  function failure(key,error) {
    const code=Number.isInteger(error.win32Code)?error.win32Code:null;
    const detail=code===5
      ? 'Windows запретила изменение из приложения. Используйте «Открыть параметры Windows».'
      : error.message || 'Windows не подтвердила изменение.';
    return {key,code,message:`${NATIVE_LABELS[key]}: ${detail}`};
  }
  function notify(result) {
    if(!result.changed.length) return;
    try { api().notify(); }
    catch { result.refreshWarning='Не удалось уведомить панель задач. Для отображения сохранённых настроек может потребоваться повторный вход в Windows.'; }
  }
  async function applyNative(patch) {
    assertSupported();validateNative(patch);
    const result={mode:'apply',changed:[],unchanged:[],failed:[]}, pending=[];
    for(const [key,value] of Object.entries(patch)) {
      const before=readNative(key);
      if((before ?? NATIVE_DEFAULTS[key])===value) { result.unchanged.push(key);continue; }
      const existing=Object.hasOwn(backup.native,key);
      if(!existing) backup.native[key]=before;
      pending.push({key,value,before,existing});
    }
    // Persist recovery values before any system write. A protected setting must
    // neither cancel unrelated changes nor leave an unmodified value in backup.
    if(pending.length) saveBackup();
    for(const {key,value,before,existing} of pending) {
      try {
        await writeNative(key,value);
        if(readNative(key)!==value) throw new Error('Windows не подтвердила изменение.');
        result.changed.push(key);
      } catch(error) {
        const item=failure(key,error);
        try {
          if(readNative(key)!==before) await writeNative(key,before);
          if(readNative(key)!==before) throw new Error('Восстановление не подтверждено.');
          if(!existing) delete backup.native[key];
        } catch { item.message+=' Исходное значение осталось в резервной копии; возврат не подтверждён.'; }
        result.failed.push(item);
      }
    }
    if(pending.length) saveBackup();
    notify(result);
    return {...state(),nativeResult:result};
  }
  async function restoreNative() {
    const result={mode:'restore',changed:[],unchanged:[],failed:[]};
    for(const [key,value] of Object.entries(backup.native)) {
      try {
        // Also repairs old 0.2.0 backups containing protected but unchanged keys.
        if(readNative(key)===value) result.unchanged.push(key);
        else {
          await writeNative(key,value);
          if(readNative(key)!==value) throw new Error('Windows не подтвердила восстановление.');
          result.changed.push(key);
        }
        delete backup.native[key];
      } catch(error) { result.failed.push(failure(key,error)); }
    }
    saveBackup();notify(result);
    return {...state(),nativeResult:result};
  }
  async function applySizes(input) {
    assertSupported();validateSizes(input);
    if(!state().sizesAvailable) throw new Error('Установите и включите в Windhawk мод Taskbar height and icon size. Поддерживается обычная установка Windhawk, без пользовательских конфигураций мода.');
    if(!backup.sizes) { backup.sizes=Object.fromEntries(Object.values(SIZE).map(([name])=>[name,api().read('HKLM',MOD+'\\Settings',name)]));saveBackup(); }
    const values=Object.fromEntries(Object.entries(input).map(([key,value])=>[SIZE[key][0],value]));
    await (adapter?.writeSizes || writeSizes)(values);
    for(const [key,value] of Object.entries(values)) if(api().read('HKLM',MOD+'\\Settings',key)!==value) throw new Error('Windhawk не сохранил размеры. Обновите состояние и повторите.');
    return state();
  }
  async function restore(group) {
    assertSupported();
    if(group==='native') return restoreNative();
    else if(group==='sizes' && backup.sizes) {
      if(!state().sizesAvailable) throw new Error('Включите мод Windhawk для восстановления размеров.');
      await (adapter?.writeSizes||writeSizes)(backup.sizes);
      for(const [key,value] of Object.entries(backup.sizes)) if(api().read('HKLM',MOD+'\\Settings',key)!==value) throw new Error('Windhawk не подтвердил восстановление. Резервная копия сохранена.');
      backup.sizes=null;
    }
    else throw new Error('Нет сохранённых параметров для восстановления.');
    saveBackup();return state();
  }
  return {state,applyNative,applySizes,restore};
}
module.exports={createTaskbar,validateNative,validateSizes,sizeScript,elevationCommand,SIZE,NATIVE,MOD,windowsApi};
