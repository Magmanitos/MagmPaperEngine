const fs = require('node:fs');
const path = require('node:path');
const { Store } = require('./store');

// Keep development, ZIP and installed builds on the same library from now on.
function userDataDirectory(appData) { return path.join(appData, 'MagmPaper'); }
function migrateLegacyLibrary(appData) {
  const directory = userDataDirectory(appData);
  const destination = path.join(directory, 'wallpapers.json');
  const legacy = path.join(appData, 'magmpaper-engine');
  const source = path.join(legacy, 'wallpapers.json');
  if (!fs.existsSync(destination) && fs.existsSync(source)) {
    new Store(legacy); // Validate before copying; preserve the original on errors.
    fs.mkdirSync(directory, { recursive: true });
    fs.copyFileSync(source, destination, fs.constants.COPYFILE_EXCL);
  }
  return directory;
}
module.exports = { userDataDirectory, migrateLegacyLibrary };
