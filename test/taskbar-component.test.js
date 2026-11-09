const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {EventEmitter}=require('node:events');
const {PassThrough,Writable}=require('node:stream');
const {createComponent,DEFAULTS,validate,encode}=require('../src/taskbar-component');
function fixture(t){
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'magm-component-')),binaries=path.join(directory,'bin');
  fs.mkdirSync(binaries);for(const name of ['MagmTaskbarHost.exe','MagmTaskbar.dll'])fs.writeFileSync(path.join(binaries,name),'native fixture');
  t.after(()=>fs.rmSync(directory,{force:true,recursive:true}));
  const sent=[];let failure=0,hang=false,starts=0,conflict=false,capabilities={frames:1,icons:5,backgrounds:1};
  function launch(){
    starts++;const child=new EventEmitter();child.stdout=new PassThrough();child.stderr=new PassThrough();
    child.stdin=new Writable({write(chunk,encoding,callback){
      const packet=chunk.toString().trim().split(' ').map(Number);sent.push(packet);callback();
      if(hang)return;
      queueMicrotask(()=>{const code=failure;failure=0;child.stdout.write(JSON.stringify({event:code?'error':'applied',sequence:packet[0],code,...capabilities,errors:0})+'\n');});
    },final(callback){callback();queueMicrotask(()=>child.emit('close',0));}});
    child.kill=()=>child.emit('close',1);return child;
  }
  const options={directory,binaries,launch,platform:'win32',conflicts:()=>conflict,timeout:50};
  const component=createComponent(options);t.after(()=>component.shutdown());
  return{component,options,sent,starts:()=>starts,failure:code=>failure=code,hang:value=>hang=value,conflict:value=>conflict=value,capabilities:value=>capabilities=value};
}
test('component validates every field and sends only numeric packets',()=>{
  for(const value of [{...DEFAULTS,color:'red'},{...DEFAULTS,color:'#ffffff;calc'},{...DEFAULTS,enabled:1},{...DEFAULTS,icon:64},{...DEFAULTS,extra:0},{...DEFAULTS,opacity:101},{...DEFAULTS,radius:-1}])assert.throws(()=>validate(value));
  assert.match(encode({...DEFAULTS,enabled:true},1),/^1 1 48 24 44 \d+ 80 0 0\n$/);
});
test('module launches only on explicit enable and saves only confirmed configuration',async t=>{
  const f=fixture(t);assert.equal(f.starts(),0);assert.equal(f.component.status().active,false);
  await f.component.apply({...DEFAULTS,color:'#abcdef'});assert.equal(f.starts(),0);
  await f.component.apply({...DEFAULTS,enabled:true,height:64,icon:40,button:52});assert.equal(f.starts(),1);
  assert.equal(f.component.status().active,true);await f.component.shutdown();
  const saved=JSON.parse(fs.readFileSync(path.join(f.options.directory,'taskbar-appearance.json')));
  assert.equal(saved.enabled,true);assert.equal(saved.icon,40);
  const restarted=createComponent(f.options);await restarted.restoreOnStart();assert.equal(restarted.status().active,true);await restarted.shutdown();
});
test('failed native confirmation restores previous settings and does not persist a false success',async t=>{
  const f=fixture(t);await f.component.apply({...DEFAULTS,enabled:true});f.failure(13);
  await assert.rejects(f.component.apply({...DEFAULTS,enabled:true,opacity:0}),/не подтвердил/);
  assert.equal(f.sent.at(-1)[6],80);assert.equal(f.component.status().settings.opacity,80);
  assert.equal(JSON.parse(fs.readFileSync(path.join(f.options.directory,'taskbar-appearance.json'))).opacity,80);
});
test('unsupported shell, timeout and conflicting Windhawk mods never save enabled state',async t=>{
  const f=fixture(t);f.conflict(true);await assert.rejects(f.component.apply({...DEFAULTS,enabled:true}),/Windhawk/);assert.equal(f.starts(),0);
  f.conflict(false);f.failure(50);await assert.rejects(f.component.apply({...DEFAULTS,enabled:true}),/совместимую/);assert.equal(f.component.status().active,false);
  f.hang(true);await assert.rejects(f.component.apply({...DEFAULTS,enabled:true}),/не ответил/);assert.equal(f.component.status().settings.enabled,false);
  assert.equal(fs.existsSync(path.join(f.options.directory,'taskbar-appearance.json')),false);
});
test('corrupt preferences are preserved and cache integrity is checked before launching',async t=>{
  const f=fixture(t),file=path.join(f.options.directory,'taskbar-appearance.json');fs.writeFileSync(file,'broken');
  const corrupt=createComponent(f.options);await assert.rejects(corrupt.apply({...DEFAULTS,enabled:true}),/не перезаписан/);assert.equal(fs.readFileSync(file,'utf8'),'broken');
  fs.unlinkSync(file);await f.component.apply({...DEFAULTS,enabled:true});await f.component.shutdown();
  const cache=path.join(f.options.directory,'NativeModules');const folder=fs.readdirSync(cache)[0];fs.writeFileSync(path.join(cache,folder,'MagmTaskbar.dll'),'modified');
  await assert.rejects(f.component.apply({...DEFAULTS,enabled:true}),/повреждена/);
});
test('an incomplete XAML panel cannot report successful appearance changes',async t=>{
  const f=fixture(t);f.capabilities({frames:1,icons:4,backgrounds:0});
  await assert.rejects(f.component.apply({...DEFAULTS,enabled:true}),/не подтвердил/);
  assert.equal(f.component.status().settings.enabled,false);
  assert.equal(fs.existsSync(path.join(f.options.directory,'taskbar-appearance.json')),false);
});
