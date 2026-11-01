const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Store } = require('../src/store');
function setup(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'magmpaper-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return { dir, store: new Store(dir) };
}
test('save, edit, activate, reload, delete active wallpaper', t => {
  const { dir, store } = setup(t);
  const first = store.save({ name: ' Aurora ', code: 'void main() {}' });
  assert.equal(first.name, 'Aurora');
  store.activate(first.id);
  store.save({ ...first, code: 'void main() { gl_FragColor = vec4(1.0); }' });
  const reloaded = new Store(dir);
  assert.equal(reloaded.list().activeId, first.id);
  assert.match(reloaded.get(first.id).code, /gl_FragColor/);
  reloaded.remove(first.id);
  assert.deepEqual(new Store(dir).list(), { version: 1, items: [], activeId: null });
});
test('validation and unknown ids cannot alter saved library', t => {
  const { store } = setup(t);
  store.save({ name: 'Good', code: 'void main() {}' });
  const before = store.list();
  for (const value of [null, {}, { name: '', code: 'x' }, { name: 'x', code: '' }, { name: 'a'.repeat(81), code: 'x' }, { name: 'x', code: 'a'.repeat(100001) }])
    assert.throws(() => store.save(value));
  assert.throws(() => store.save({ id: 'missing', name: 'x', code: 'x' }));
  assert.throws(() => store.remove('missing'));
  assert.throws(() => store.activate('missing'));
  assert.deepEqual(store.list(), before);
});
test('corrupt library is reported without being overwritten', t => {
  const { dir } = setup(t);
  const file = path.join(dir, 'wallpapers.json');
  fs.writeFileSync(file, '{broken');
  assert.throws(() => new Store(dir));
  assert.equal(fs.readFileSync(file, 'utf8'), '{broken');
});
test('caller cannot mutate in-memory library or activation', t => {
  const { store } = setup(t);
  const item = store.save({ name: 'x', code: 'x' });
  store.get(item.id).name = 'changed';
  store.list().items.length = 0;
  assert.equal(store.get(item.id).name, 'x');
  store.activate(item.id); store.activate(null);
  assert.equal(store.list().activeId, null);
});
test('failed atomic replacement retains previous data', t => {
  const { store } = setup(t);
  store.save({ name: 'existing', code: 'x' });
  const before = store.list();
  fs.mkdirSync(store.file + '.tmp');
  assert.throws(() => store.save({ name: 'new', code: 'x' }));
  assert.deepEqual(store.list(), before);
  assert.deepEqual(JSON.parse(fs.readFileSync(store.file, 'utf8')), before);
});
