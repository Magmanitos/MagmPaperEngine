const fs=require('node:fs');
const path=require('node:path');
module.exports=async function({packager}) {
  for(const name of ['MagmTaskbarHost.exe','MagmTaskbar.dll']) {
    const file=path.join(packager.projectDir,'native/bin',name);
    if(!fs.existsSync(file)||fs.statSync(file).size<4096) {
      throw new Error('Missing integrated taskbar component. Build it on Windows x64 with npm run build:native before packaging.');
    }
  }
};
