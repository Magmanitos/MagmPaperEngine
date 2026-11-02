const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { Store } = require('../src/store');
function setup(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'magmpaper-files-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return { dir, store: new Store(dir) };
}
test('v1 migration keeps shader text, IDs, active selection and settings, with an untouched backup', t => {
  const { dir, store } = setup(t);
  const old = { version: 1, items: [{ id: 'one', name: 'Мои обои', code: '// GLSL only\nvoid main() {}\n' }], activeId: 'one', settings: { mouseEnabled: false, fps: 30 } };
  const original = JSON.stringify(old);
  fs.writeFileSync(store.file, original);
  const migrated = new Store(dir), item = migrated.get('one');
  assert.equal(item.file, 'Мои обои.glsl');
  assert.equal(fs.readFileSync(path.join(migrated.directory, item.file), 'utf8'), old.items[0].code);
  assert.equal(fs.readFileSync(store.file + '.v1-backup', 'utf8'), original);
  assert.equal(migrated.list().activeId, 'one');
  assert.equal(migrated.list().settings.fps, 30);
  assert.equal(JSON.parse(fs.readFileSync(store.file, 'utf8')).items[0].code, undefined);
  assert.equal(new Store(dir).get('one').file, item.file);
});
test('Windows-safe filenames, duplicate names, rename and deletion never overwrite another shader', t => {
  const { store } = setup(t);
  const a = store.save({ name: 'Aurora', code: 'a' }), b = store.save({ name: 'Aurora', code: 'b' });
  assert.equal(a.file, 'Aurora.glsl'); assert.equal(b.file, 'Aurora (2).glsl');
  const c = store.save({ name: 'CON', code: 'c' }); assert.equal(c.file, '_CON.glsl');
  const d = store.save({ name: '../a:b?*', code: 'd' }); assert.ok(!/[/:?*]/.test(d.file));
  const renamed = store.save({ ...a, name: 'Новая волна', code: 'updated' });
  assert.equal(renamed.file, 'Новая волна.glsl');
  assert.equal(fs.existsSync(path.join(store.directory, a.file)), false);
  assert.equal(store.get(b.id).code, 'b');
  const recased = store.save({ ...renamed, name: 'НОВАЯ ВОЛНА' });
  assert.equal(store.get(recased.id).code, 'updated');
  store.remove(a.id); assert.equal(fs.existsSync(path.join(store.directory, renamed.file)), false);
});
test('files edited, added, renamed and removed in Explorer are reflected without losing settings', t => {
  const { store } = setup(t), a = store.save({ name: 'A', code: 'before' });
  store.activate(a.id); store.setSettings({ fps: 15 });
  fs.writeFileSync(path.join(store.directory, a.file), 'external edit');
  assert.equal(store.get(a.id).code, 'external edit');
  store.setSettings({ renderScale: 0.75 });
  assert.equal(store.get(a.id).code, 'external edit');
  fs.renameSync(path.join(store.directory, a.file), path.join(store.directory, 'Renamed.glsl'));
  const data = store.list();
  assert.equal(data.items.length, 1); assert.equal(data.items[0].name, 'Renamed');
  assert.equal(data.activeId, null); assert.equal(data.settings.fps, 15);
  fs.unlinkSync(path.join(store.directory, 'Renamed.glsl')); assert.equal(store.list().items.length, 0);
});
test('file-write failures roll back code and leave metadata usable', t => {
  const { dir, store } = setup(t), a = store.save({ name: 'A', code: 'before' });
  const original = fs.readFileSync(store.file, 'utf8');
  fs.mkdirSync(path.join(store.directory, a.file + '.tmp'));
  assert.throws(() => store.save({ ...a, code: 'after' }));
  assert.equal(fs.readFileSync(store.file, 'utf8'), original);
  assert.equal(new Store(dir).get(a.id).code, 'before');
});
test('interrupted multi-file migration is rolled back on reopening, committed journals are finalized', t => {
  const { dir, store } = setup(t), a = store.save({ name: 'A', code: 'before' });
  const journal = { revision: 'interrupted', changes: [{ file: a.file, before: 'before', after: 'after' }, { file: 'New.glsl', before: null, after: 'new' }] };
  fs.writeFileSync(store.file + '.journal', JSON.stringify(journal));
  fs.writeFileSync(path.join(store.directory, a.file), 'after');
  fs.writeFileSync(path.join(store.directory, 'New.glsl'), 'new');
  const reopened = new Store(dir);
  assert.equal(reopened.get(a.id).code, 'before');
  assert.equal(fs.existsSync(path.join(store.directory, 'New.glsl')), false);
  journal.revision = reopened.data.revision;
  fs.writeFileSync(store.file + '.journal', JSON.stringify(journal));
  assert.equal(new Store(dir).get(a.id).code, 'before');
  assert.equal(fs.existsSync(store.file + '.journal'), false);
});
test('migration preserves unrelated pre-existing files and rejects unsafe metadata paths', t => {
  const { dir, store } = setup(t);
  fs.writeFileSync(path.join(store.directory, 'Same.glsl'), 'unrelated');
  fs.writeFileSync(store.file, JSON.stringify({ version: 1, items: [{ id: 'a', name: 'Same', code: 'old' }], activeId: null }));
  const migrated = new Store(dir);
  assert.equal(migrated.get('a').file, 'Same (2).glsl');
  assert.equal(fs.readFileSync(path.join(store.directory, 'Same.glsl'), 'utf8'), 'unrelated');
  const bad = JSON.parse(fs.readFileSync(store.file, 'utf8')); bad.items[0].file = '../outside.glsl';
  fs.writeFileSync(store.file, JSON.stringify(bad)); assert.throws(() => new Store(dir));
});
test('a manifest replacement failure rolls back an already-written shader', t => {
  const { dir, store } = setup(t), item = store.save({ name: 'A', code: 'before' });
  const manifest = fs.readFileSync(store.file, 'utf8'), rename = fs.renameSync;
  t.mock.method(fs, 'renameSync', (from, to) => {
    if (to === store.file) throw new Error('simulated manifest sharing violation');
    return rename(from, to);
  });
  assert.throws(() => store.save({ ...item, code: 'after' }), /sharing violation/);
  assert.equal(fs.readFileSync(path.join(store.directory, item.file), 'utf8'), 'before');
  assert.equal(fs.readFileSync(store.file, 'utf8'), manifest);
  assert.equal(new Store(dir).get(item.id).code, 'before');
});
