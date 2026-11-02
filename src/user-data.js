const fs = require('node:fs');
const path = require('node:path');
const { validateLibrary } = require('./store');
const { readCode } = require('./library-files');

// Keep development, ZIP and installed builds on the same library from now on.
function userDataDirectory(appData) { return path.join(appData, 'MagmPaper'); }
function migrateLegacyLibrary(appData) {
  const directory = userDataDirectory(appData);
  const destination = path.join(directory, 'wallpapers.json');
  const legacy = path.join(appData, 'magmpaper-engine');
  const source = path.join(legacy, 'wallpapers.json');
  if (!fs.existsSync(destination) && fs.existsSync(source)) {
    const data = validateLibrary(JSON.parse(fs.readFileSync(source, 'utf8')));
    fs.mkdirSync(directory, { recursive: true });
    if (data.version === 2) {
      const shaders = path.join(directory, 'Wallpapers');
      fs.mkdirSync(shaders, { recursive: true });
      for (const item of data.items) {
        const code = readCode(path.join(legacy, 'Wallpapers'), item.file);
        if (code === null) throw new Error(`Отсутствует файл ${item.file} в старой коллекции.`);
        const existing = readCode(shaders, item.file);
        if (existing !== null && existing !== code) throw new Error(`Файл ${item.file} уже существует в новой папке.`);
        if (existing === null) fs.writeFileSync(path.join(shaders, item.file), code, { flag: 'wx' });
      }
    }
    fs.copyFileSync(source, destination, fs.constants.COPYFILE_EXCL);
  }
  return directory;
}
module.exports = { userDataDirectory, migrateLegacyLibrary };
