const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { DEFAULT_SETTINGS, validateSettings } = require('./settings');
const { safeFile, readCode, fileName, recover, commitFiles } = require('./library-files');
function validate(input) {
  if (!input || typeof input.name !== 'string' || !input.name.trim() || input.name.trim().length > 80)
    throw new Error('Введите название от 1 до 80 символов.');
  if (typeof input.code !== 'string' || !input.code.trim() || input.code.length > 100000)
    throw new Error('Вставьте GLSL-код (не более 100 000 символов).');
  return { name: input.name.trim(), code: input.code };
}
function validateLibrary(data) {
  if (![1, 2].includes(data.version) || !Array.isArray(data.items)) throw new Error('Неизвестный формат библиотеки.');
  const ids = new Set(), files = new Set();
  for (const item of data.items) {
    validate({ ...item, code: data.version === 1 ? item.code : 'metadata' });
    if (typeof item.id !== 'string' || !item.id || ids.has(item.id)) throw new Error('Повреждён идентификатор обоев.');
    ids.add(item.id);
    if (data.version === 2) {
      if (!safeFile(item.file) || files.has(item.file.toLowerCase())) throw new Error('Повреждено имя файла обоев.');
      files.add(item.file.toLowerCase());
    }
  }
  if (data.activeId !== null && !ids.has(data.activeId)) throw new Error('Повреждён выбор активных обоев.');
  data.settings = { ...DEFAULT_SETTINGS, ...validateSettings(data.settings === undefined ? {} : data.settings) };
  return data;
}
class Store {
  constructor(directory) {
    fs.mkdirSync(directory, { recursive: true });
    this.file = path.join(directory, 'wallpapers.json');
    this.directory = path.join(directory, 'Wallpapers');
    fs.mkdirSync(this.directory, { recursive: true });
    recover(this.directory, this.file);
    this.data = { version: 2, items: [], activeId: null, settings: { ...DEFAULT_SETTINGS } };
    if (fs.existsSync(this.file)) {
      const data = validateLibrary(JSON.parse(fs.readFileSync(this.file, 'utf8')));
      if (data.version === 1) {
        const backup = this.file + '.v1-backup';
        if (!fs.existsSync(backup)) fs.copyFileSync(this.file, backup, fs.constants.COPYFILE_EXCL);
        const occupied = this.occupied();
        const items = data.items.map(item => {
          const file = fileName(item.name, occupied); occupied.add(file.toLowerCase());
          return { ...item, file };
        });
        this.commit({ ...data, version: 2, items });
      } else this.data = data;
    }
    this.refresh();
  }
  occupied(except) {
    return new Set(fs.readdirSync(this.directory).filter(file => file !== except).map(file => file.toLowerCase()));
  }
  refresh() {
    recover(this.directory, this.file);
    const items = [], known = new Set();
    for (const item of this.data.items) {
      const code = readCode(this.directory, item.file);
      if (code === null) continue; // External deletion/rename is reflected on refresh.
      validate({ ...item, code });
      items.push({ id: item.id, name: item.name, file: item.file, code });
      known.add(item.file.toLowerCase());
    }
    for (const file of fs.readdirSync(this.directory).sort()) {
      if (!/\.glsl$/i.test(file) || known.has(file.toLowerCase())) continue;
      const code = readCode(this.directory, file), name = file.slice(0, -5).slice(0, 80);
      validate({ name, code });
      items.push({ id: randomUUID(), name, file, code }); known.add(file.toLowerCase());
    }
    const next = { ...this.data, items, activeId: items.some(i => i.id === this.data.activeId) ? this.data.activeId : null };
    const metadata = value => JSON.stringify(value.items.map(({ id, name, file }) => ({ id, name, file })));
    if (metadata(next) !== metadata(this.data) || next.activeId !== this.data.activeId) this.commit(next);
    else this.data = next;
  }
  list() { this.refresh(); return structuredClone(this.data); }
  get(id) {
    this.refresh();
    const item = this.data.items.find(item => item.id === id);
    if (!item) throw new Error('Обои не найдены.');
    return structuredClone(item);
  }
  commit(data) {
    recover(this.directory, this.file);
    const changes = [];
    for (const item of data.items) {
      const before = readCode(this.directory, item.file);
      if (before !== item.code) changes.push({ file: item.file, before, after: item.code });
    }
    for (const item of this.data.items) {
      if (!data.items.some(next => next.file === item.file)) {
        const before = readCode(this.directory, item.file);
        if (before !== null) changes.push({ file: item.file, before, after: null });
      }
    }
    const revision = randomUUID();
    const manifest = { ...data, revision, items: data.items.map(({ id, name, file }) => ({ id, name, file })) };
    commitFiles(this.directory, this.file, manifest, changes);
    this.data = { ...data, revision };
  }
  save(input) {
    const value = validate(input), data = this.list();
    const previous = input.id ? this.get(input.id) : null;
    const file = previous && previous.name === value.name ? previous.file : fileName(value.name, this.occupied(previous?.file));
    const item = { ...value, id: previous?.id || randomUUID(), file: previous?.file.toLowerCase() === file.toLowerCase() ? previous.file : file };
    if (previous) data.items[data.items.findIndex(i => i.id === previous.id)] = item;
    else data.items.push(item);
    this.commit(data); return item;
  }
  remove(id) {
    this.get(id);
    const data = this.list();
    data.items = data.items.filter(i => i.id !== id);
    if (data.activeId === id) data.activeId = null;
    this.commit(data);
  }
  activate(id) {
    const data = this.list();
    if (id !== null && !data.items.some(item => item.id === id)) throw new Error('Обои не найдены.');
    if (data.activeId !== id) this.commit({ ...data, activeId: id });
  }
  setMouseEnabled(enabled) { this.setSettings({ mouseEnabled: enabled }); return enabled; }
  setSettings(patch) {
    const valid = validateSettings(patch), data = this.list();
    const settings = { ...data.settings, ...valid };
    this.commit({ ...data, settings }); return { ...settings };
  }
}
module.exports = { Store, validate, validateLibrary };
