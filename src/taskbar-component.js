const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const DEFAULTS=Object.freeze({enabled:false,height:48,icon:24,button:44,color:'#171923',opacity:80,hideWidgets:false,radius:0});
function validate(input) {
  if(!input||typeof input!=='object'||Array.isArray(input)||Object.keys(input).length!==Object.keys(DEFAULTS).length) throw new Error('Некорректные настройки встроенного модуля.');
  for(const key of Object.keys(input)) if(!Object.hasOwn(DEFAULTS,key)) throw new Error('Неизвестная настройка встроенного модуля.');
  if(typeof input.enabled!=='boolean'||typeof input.hideWidgets!=='boolean'||!/^#[0-9a-f]{6}$/i.test(input.color)) throw new Error('Некорректный цвет или переключатель.');
  for(const [key,min,max] of [['height',32,96],['icon',16,64],['button',28,100],['opacity',0,100],['radius',0,24]]) {
    if(!Number.isInteger(input[key])||input[key]<min||input[key]>max) throw new Error('Размер, прозрачность или скругление вне допустимого диапазона.');
  }
  if(input.icon+8>input.height||input.button<input.icon) throw new Error('Значки должны помещаться в панель и кнопки.');
  return {...input,color:input.color.toLowerCase()};
}
function encode(config,sequence) {
  validate(config);
  return [sequence,+config.enabled,config.height,config.icon,config.button,parseInt(config.color.slice(1),16),config.opacity,+config.hideWidgets,config.radius].join(' ')+'\n';
}
function nativeError(code) {
  if(code===50||code===-2147467262) return 'На этой сборке Windows не удалось найти совместимую XAML-панель задач. Исходное оформление сохранено.';
  if(code===5||code===-2147024891) return 'Windows запретила подключение встроенного модуля к панели задач.';
  if(code===258) return 'Панель задач не подтвердила применение настроек вовремя.';
  return `Встроенный модуль не подтвердил изменение (код ${code}).`;
}
function createComponent({directory,binaries,platform=process.platform,launch=spawn,onChange=()=>{},conflicts=()=>false,timeout=20000}) {
  const file=path.join(directory,'taskbar-appearance.json');
  let settings={...DEFAULTS},loadError='';
  try {if(fs.existsSync(file)) settings=validate(JSON.parse(fs.readFileSync(file,'utf8')));}
  catch {loadError='Не удалось прочитать taskbar-appearance.json. Файл не перезаписан.';}
  let child=null,sequence=0,mode='idle',error=loadError,active=false,capabilities=null;
  const pending=new Map();
  const supported=platform==='win32'&&process.arch==='x64'&&(Number(os.release().split('.')[2])>=22000||launch!==spawn);
  function status() {return {supported,available:supported&&fs.existsSync(path.join(binaries,'MagmTaskbarHost.exe'))&&fs.existsSync(path.join(binaries,'MagmTaskbar.dll')),settings:{...settings},active,mode,error,capabilities};}
  function publish() {onChange(status());}
  function persist(next) {
    fs.mkdirSync(directory,{recursive:true});fs.writeFileSync(file+'.tmp',JSON.stringify(next,null,2));fs.renameSync(file+'.tmp',file);settings=next;
  }
  function cache() {
    const names=['MagmTaskbarHost.exe','MagmTaskbar.dll'];
    const contents=names.map(name=>fs.readFileSync(path.join(binaries,name)));
    const hash=crypto.createHash('sha256');for(const bytes of contents) hash.update(bytes);
    const target=path.join(directory,'NativeModules',hash.digest('hex').slice(0,24));
    fs.mkdirSync(target,{recursive:true});
    names.forEach((name,i)=>{
      const destination=path.join(target,name);
      if(!fs.existsSync(destination)) fs.writeFileSync(destination,contents[i],{flag:'wx'});
      if(!fs.readFileSync(destination).equals(contents[i])) throw new Error('Копия встроенного модуля повреждена. Переустановите MagmPaper.');
    });
    return path.join(target,names[0]);
  }
  function rejectPending(message) {for(const request of pending.values()){clearTimeout(request.timer);request.reject(new Error(message));}pending.clear();}
  function start() {
    if(child) return;
    if(!status().available) throw new Error('Встроенный модуль отсутствует или эта система не поддерживается. Установите полную сборку MagmPaper для Windows 11 x64.');
    if(conflicts()) throw new Error('Отключите моды Taskbar height and icon size и Windows 11 Taskbar Styler в Windhawk перед включением встроенного модуля.');
    const process=launch(cache(),['--parent',String(global.process.pid)],{windowsHide:true,stdio:['pipe','pipe','pipe']});
    child=process;let buffer='',stderr='';mode='starting';error='';publish();
    process.stdin.on('error',()=>rejectPending('Соединение со встроенным модулем закрыто.'));
    process.stderr.on('data',chunk=>{stderr=(stderr+chunk.toString()).slice(-2000);});
    process.stdout.on('data',chunk=>{
      buffer+=chunk.toString();
      if(buffer.length>65536) {rejectPending('Некорректный ответ встроенного модуля.');void shutdown();return;}
      let end;
      while((end=buffer.indexOf('\n'))>=0) {
        const line=buffer.slice(0,end);buffer=buffer.slice(end+1);
        let message;try {message=JSON.parse(line);} catch {continue;}
        if(child!==process) continue;
        const request=pending.get(message.sequence);
        if(message.event==='ready') capabilities={frames:message.frames,icons:message.icons,backgrounds:message.backgrounds};
        if(message.event==='applied') {
          if(message.errors!==0||(request?.config.enabled&&(!Number.isInteger(message.frames)||message.frames<1||!Number.isInteger(message.icons)||message.icons<1||!Number.isInteger(message.backgrounds)||message.backgrounds<1))) {
            if(request) {clearTimeout(request.timer);pending.delete(message.sequence);request.reject(new Error('Модуль не подтвердил состояние панели.'));}
            continue;
          }
          capabilities={frames:message.frames,icons:message.icons,backgrounds:message.backgrounds};
          if(request) {active=request.config.enabled;clearTimeout(request.timer);pending.delete(message.sequence);request.resolve();}
          mode=active?'active':'idle';error='';publish();
        }
        if(message.event==='error') {
          error=nativeError(message.code);mode='error';
          if(request) {clearTimeout(request.timer);pending.delete(message.sequence);request.reject(new Error(error));}
          else void shutdown();
          publish();
        }
      }
    });
    process.on('error',failure=>{if(child===process){error=`Не удалось запустить встроенный модуль: ${failure.message}`;mode='error';rejectPending(error);publish();}});
    process.on('close',code=>{
      if(child!==process) return;
      child=null;active=false;
      if(pending.size) {error=`Встроенный модуль завершился до подтверждения настроек (${code}).`;rejectPending(error);mode='error';}
      else if(mode!=='error') mode='idle';
      publish();
    });
  }
  function send(config) {
    start();const id=++sequence;
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Встроенный модуль не ответил. Исходное оформление будет восстановлено.'));},timeout);
      pending.set(id,{resolve,reject,timer,config});
      child.stdin.write(encode(config,id),failure=>{if(failure){clearTimeout(timer);pending.delete(id);reject(failure);}});
    });
  }
  async function shutdown() {
    const process=child;if(!process) return;
    rejectPending('Модуль отключается.');
    await new Promise(resolve=>{
      const kill=setTimeout(()=>{process.kill();},5500);
      const finish=setTimeout(()=>{if(child===process){child=null;active=false;}resolve();},7500);
      process.once('close',()=>{clearTimeout(kill);clearTimeout(finish);resolve();});
      process.stdin.end();
    });
  }
  async function apply(input) {
    if(loadError) throw new Error(loadError);
    const next=validate(input),previous={...settings};const wasRunning=!!child;
    try {
      if(next.enabled||child) await send(next);
      persist(next);error='';mode=active?'active':'idle';publish();return status();
    } catch(failure) {
      if(wasRunning&&child) {try {await send(previous);} catch {await shutdown();}}
      else await shutdown();
      error=failure.message;mode='error';publish();throw failure;
    }
  }
  async function restoreOnStart() {if(settings.enabled&&!loadError) try {await apply(settings);} catch {/* surfaced through status without stopping wallpapers */}}
  return {status,apply,shutdown,restoreOnStart};
}
module.exports={createComponent,validate,encode,DEFAULTS};
