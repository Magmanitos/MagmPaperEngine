const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Store } = require('../src/store');
const { migrateLegacyLibrary, userDataDirectory } = require('../src/user-data');
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'magmpaper-migration-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
test('installed app imports VS Code collection, active wallpaper and preferences without removing the source', t => {
  const root = fixture(t), legacy = new Store(path.join(root, 'magmpaper-engine'));
  const item = legacy.save({ name: 'My wallpaper', code: 'void main() {}' });
  legacy.activate(item.id); legacy.setSettings({ fps: 30, mouseEnabled: false });
  const original = fs.readFileSync(legacy.file, 'utf8');
  const directory = migrateLegacyLibrary(root);
  assert.equal(directory, userDataDirectory(root));
  assert.deepEqual(new Store(directory).list(), legacy.list());
  assert.equal(fs.readFileSync(legacy.file, 'utf8'), original);
});
test('reinstall and repeated migration never overwrite an existing installed library', t => {
  const root = fixture(t);
  new Store(path.join(root, 'magmpaper-engine')).save({ name: 'Old collection', code: 'old' });
  const installed = new Store(userDataDirectory(root));
  installed.save({ name: 'Current collection', code: 'new' });
  const original = fs.readFileSync(installed.file, 'utf8');
  migrateLegacyLibrary(root); migrateLegacyLibrary(root);
  assert.equal(fs.readFileSync(installed.file, 'utf8'), original);
});
test('corrupt legacy data is reported and preserved without creating a replacement library', t => {
  const root = fixture(t), legacy = path.join(root, 'magmpaper-engine');
  fs.mkdirSync(legacy); fs.writeFileSync(path.join(legacy, 'wallpapers.json'), '{corrupt');
  assert.throws(() => migrateLegacyLibrary(root));
  assert.equal(fs.readFileSync(path.join(legacy, 'wallpapers.json'), 'utf8'), '{corrupt');
  assert.equal(fs.existsSync(path.join(userDataDirectory(root), 'wallpapers.json')), false);
});
test('fresh installation uses a stable AppData directory', t => {
  const root = fixture(t);
  assert.equal(migrateLegacyLibrary(root), path.join(root, 'MagmPaper'));
});
