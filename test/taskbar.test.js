const { test } = require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {createTaskbar,validateNative,validateSizes,sizeScript,elevationCommand,SIZE,NATIVE,MOD,windowsApi}=require('../src/taskbar');
const sizes={height:48,icon:24,button:44,smallIcon:16,smallButton:32};
function fixture(t) {
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),'magm-taskbar-'));t.after(()=>fs.rmSync(dir,{recursive:true,force:true}));
  const values=new Map();for(const [name,,,value] of Object.values(SIZE))values.set(MOD+'\\Settings/'+name,value);
  values.set(MOD+'/Disabled',0);let autoHide=false,writes=0,notifications=0,fail=false;
  const adapter={read:(hive,key,name)=>values.get(key+'/'+name)??null,autoHide:()=>autoHide,setAutoHide:value=>{autoHide=value;},notify:()=>notifications++,write:async(key,value)=>{writes++;if(fail&&key==='widgets')throw new Error('write denied');const [sub,name]=NATIVE[key];if(value===null)values.delete(sub+'/'+name);else values.set(sub+'/'+name,value);},writeSizes:async settings=>{for(const [name,value]of Object.entries(settings)){if(value===null)values.delete(MOD+'\\Settings/'+name);else values.set(MOD+'\\Settings/'+name,value);}}};
  return {dir,adapter,values,module:createTaskbar(dir,'test',adapter),stats:()=>({writes,notifications}),fail:value=>fail=value};
}
test('taskbar applies on demand and restores missing registry values and auto-hide across restart',async t=>{
  const f=fixture(t),before=f.module.state();assert.equal(before.native.alignment,1);assert.equal(f.stats().writes,0);
  await f.module.applyNative({alignment:0,autoHide:true});assert.equal(f.module.state().native.autoHide,true);
  const reopened=createTaskbar(f.dir,'test',f.adapter);assert.equal(reopened.state().canRestoreNative,true);
  await reopened.restore('native');assert.equal(f.module.state().native.autoHide,false);assert.equal(f.values.has(NATIVE.alignment.join('/')),false);
  assert.equal(f.stats().notifications,2);
});
test('size changes preserve a complete original snapshot and leave unrelated mod settings intact',async t=>{
  const f=fixture(t);f.values.set(MOD+'\\Settings/OtherSetting',99);
  await f.module.applySizes({...sizes,height:60,icon:40,button:52});assert.equal(f.module.state().sizes.icon,40);
  await f.module.applySizes({...sizes,height:36,icon:20,button:32});await f.module.restore('sizes');
  assert.deepEqual(f.module.state().sizes,sizes);assert.equal(f.values.get(MOD+'\\Settings/OtherSetting'),99);
});
test('unsupported or disabled size component is not claimed as applied',async t=>{
  const f=fixture(t);f.values.set(MOD+'/Disabled',1);await assert.rejects(f.module.applySizes(sizes),/Windhawk/);
  assert.equal(fs.existsSync(path.join(f.dir,'taskbar-backup.json')),false);
  const unavailable=createTaskbar(f.dir,'linux');assert.equal(unavailable.state().supported,false);await assert.rejects(unavailable.applyNative({alignment:0}));
});
test('protected widgets do not cancel alignment, search, task view or auto-hide',async t=>{
  const f=fixture(t),write=f.adapter.write;
  f.adapter.write=async(key,value)=>{if(key==='widgets')throw Object.assign(new Error('raw denied'),{win32Code:5});return write(key,value);};
  const result=await f.module.applyNative({alignment:0,widgets:0,search:0,taskView:0,autoHide:true});
  assert.deepEqual(result.native,{alignment:0,widgets:1,search:0,taskView:0,autoHide:true});
  assert.deepEqual(result.nativeResult.changed,['alignment','search','taskView','autoHide']);
  assert.equal(result.nativeResult.failed[0].code,5);
  assert.match(result.nativeResult.failed[0].message,/Виджеты: Windows запретила/);
  assert.doesNotMatch(result.nativeResult.failed[0].message,/raw denied|reg.exe/);
  assert.equal(Object.hasOwn(JSON.parse(fs.readFileSync(path.join(f.dir,'taskbar-backup.json'))).native,'widgets'),false);
  const restored=await f.module.restore('native');assert.equal(restored.nativeResult.failed.length,0);
  assert.equal(restored.native.autoHide,false);assert.equal(f.values.has(NATIVE.alignment.join('/')),false);
});
test('unchanged fields including protected default widgets are never written or backed up',async t=>{
  const f=fixture(t);f.fail(true);
  const result=await f.module.applyNative(f.module.state().native);
  assert.equal(f.stats().writes,0);assert.equal(result.nativeResult.failed.length,0);
  assert.equal(fs.existsSync(path.join(f.dir,'taskbar-backup.json')),false);
  const changed=await f.module.applyNative({...f.module.state().native,autoHide:true});
  assert.deepEqual(changed.nativeResult.changed,['autoHide']);assert.equal(f.stats().writes,0);
});
test('restore skips unchanged legacy protected snapshots and continues past failed fields',async t=>{
  const f=fixture(t);
  fs.writeFileSync(path.join(f.dir,'taskbar-backup.json'),JSON.stringify({native:{widgets:null,alignment:1,autoHide:false,search:2},sizes:null}));
  f.values.set(NATIVE.alignment.join('/'),0);f.values.set(NATIVE.search.join('/'),0);f.adapter.setAutoHide(true);
  const write=f.adapter.write;
  f.adapter.write=async(key,value)=>{if(key==='widgets'||key==='alignment')throw Object.assign(new Error('denied'),{win32Code:5});return write(key,value);};
  const reopened=createTaskbar(f.dir,'test',f.adapter),result=await reopened.restore('native');
  assert.deepEqual(result.nativeResult.unchanged,['widgets']);
  assert.deepEqual(result.nativeResult.changed,['autoHide','search']);
  assert.equal(result.native.autoHide,false);assert.equal(result.native.search,2);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(f.dir,'taskbar-backup.json'))).native,{alignment:1});
  f.adapter.write=write;const retry=await reopened.restore('native');assert.equal(retry.canRestoreNative,false);
});
test('a failed readback rolls back only its own field and keeps other successful changes',async t=>{
  const f=fixture(t),write=f.adapter.write;
  f.adapter.write=async(key,value)=>write(key,key==='search'&&value===0?3:value);
  const result=await f.module.applyNative({alignment:0,search:0,autoHide:true});
  assert.deepEqual(result.nativeResult.changed,['alignment','autoHide']);
  assert.equal(result.nativeResult.failed[0].key,'search');assert.equal(result.native.search,2);
  assert.equal(f.values.has(NATIVE.search.join('/')),false);
});
test('IPC values and privileged command arguments are restricted to known integer settings',()=>{
  for(const value of [null,[],{unknown:0},{alignment:3},{autoHide:1},JSON.parse('{"__proto__":0}'),{constructor:0}])assert.throws(()=>validateNative(value));
  for(const value of [{...sizes,icon:80},{...sizes,height:32,icon:32},{...sizes,button:16},{...sizes,height:'48'},{...sizes,extra:1}])assert.throws(()=>validateSizes(value));
  assert.throws(()=>sizeScript({'IconSize; Start-Process calc':24}));assert.throws(()=>sizeScript({IconSize:'24; calc'}));
  assert.match(sizeScript({IconSize:32}),/SettingsChangeTime/);
});
test('Win32 registry reads and real taskbar auto-hide roundtrip', {skip:process.platform!=='win32'},t=>{
  const a=windowsApi();assert.equal(a.read('HKCU','Software\\MagmPaperMissingTestKey','Missing'),null);
  const before=a.autoHide();t.after(()=>a.setAutoHide(before));a.setAutoHide(!before);assert.equal(a.autoHide(),!before);a.setAutoHide(before);assert.equal(a.autoHide(),before);
});
test('Win32 DWORD writer handles create, update, delete and access denied without shell text', {skip:process.platform!=='win32'},t=>{
  const {execFileSync}=require('node:child_process');
  const ps=source=>execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from("$ErrorActionPreference='Stop'; "+source,'utf16le').toString('base64')],{windowsHide:true,timeout:30000,encoding:'utf8'});
  const sub=`Software\\MagmPaperNativeTest-${process.pid}`,root=`Registry::HKEY_CURRENT_USER\\${sub}`,a=windowsApi();
  const openAcl=`$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('${sub}',[Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,[Security.AccessControl.RegistryRights]393216);`;
  let originalAcl;
  t.after(()=>ps(`${originalAcl?`${openAcl} try { $acl=$key.GetAccessControl(); $acl.SetSecurityDescriptorSddlForm('${originalAcl}',[Security.AccessControl.AccessControlSections]::Access); $key.SetAccessControl($acl) } finally { $key.Dispose() };`:''} Remove-Item -LiteralPath '${root}' -Recurse -Force -ErrorAction SilentlyContinue`));
  a.writeUserDword(sub,'Setting',1);assert.equal(a.read('HKCU',sub,'Setting'),1);
  a.writeUserDword(sub,'Setting',0);assert.equal(a.read('HKCU',sub,'Setting'),0);
  a.writeUserDword(sub,'Setting',null);assert.equal(a.read('HKCU',sub,'Setting'),null);
  a.writeUserDword(sub,'Setting',null);a.writeUserDword(sub+'\\Missing','Setting',null);
  a.writeUserDword(sub,'Setting',1);
  originalAcl=ps(`${openAcl} try { $key.GetAccessControl().GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) } finally { $key.Dispose() }`).trim();
  ps(`${openAcl} try { $acl=$key.GetAccessControl(); $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User; $deny=[Security.AccessControl.RegistryAccessRule]::new($sid,[Security.AccessControl.RegistryRights]::SetValue,[Security.AccessControl.AccessControlType]::Deny); $acl.AddAccessRule($deny); $key.SetAccessControl($acl) } finally { $key.Dispose() }`);
  assert.throws(()=>a.writeUserDword(sub,'Setting',0),error=>error.win32Code===5&&!error.message.includes('reg.exe'));
  assert.equal(a.read('HKCU',sub,'Setting'),1);
});
test('Windows PowerShell writes DWORD settings, notifies the mod and restores absent values', {skip:process.platform!=='win32'},t=>{
  const {execFileSync}=require('node:child_process');
  const encode=source=>Buffer.from(source,'utf16le').toString('base64');
  const ps=source=>execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',encode(source)],{windowsHide:true,timeout:30000});
  const fixtureKey=`Software\\MagmPaperTaskbarTest-${process.pid}`;
  const root=`Registry::HKEY_CURRENT_USER\\${fixtureKey}`;
  // Exercise the production transaction against an isolated HKCU fixture, never real Windhawk settings.
  const script=values=>sizeScript(values).replace(`Registry::HKEY_LOCAL_MACHINE\\${MOD}`,root);
  t.after(()=>ps(`Remove-Item -LiteralPath '${root}' -Recurse -Force -ErrorAction SilentlyContinue`));
  ps(`New-Item -Path '${root}\\Settings' -Force | Out-Null; New-ItemProperty -LiteralPath '${root}\\Settings' -Name Unrelated -Value 99 -PropertyType DWord | Out-Null`);
  ps(script({TaskbarHeight:60,IconSize:40}));
  const a=windowsApi();
  assert.equal(a.read('HKCU',fixtureKey+'\\Settings','TaskbarHeight'),60);
  assert.equal(a.read('HKCU',fixtureKey+'\\Settings','IconSize'),40);
  const stamp=a.read('HKCU',fixtureKey,'SettingsChangeTime');assert.ok(stamp>0);
  ps(script({TaskbarHeight:null,IconSize:24,IconSizeSmall:null}));
  assert.equal(a.read('HKCU',fixtureKey+'\\Settings','TaskbarHeight'),null);
  assert.equal(a.read('HKCU',fixtureKey+'\\Settings','IconSize'),24);
  assert.equal(a.read('HKCU',fixtureKey+'\\Settings','Unrelated'),99);
  assert.ok(a.read('HKCU',fixtureKey,'SettingsChangeTime')>stamp);
  // Parse the elevated launcher without starting a UAC prompt in CI.
  ps(`$source=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('${encode(elevationCommand({IconSize:24}))}')); $tokens=$null; $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors); if($errors.Count){throw ($errors | Out-String)}`);
});
