const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

function validate(input) {
  if (!input || typeof input.name !== 'string' || !input.name.trim() || input.name.trim().length > 80)
    throw new Error('Введите название от 1 до 80 символов.');
  if (typeof input.code !== 'string' || !input.code.trim() || input.code.length > 100000)
    throw new Error('Вставьте GLSL-код (не более 100 000 символов).');
  return { name: input.name.trim(), code: input.code };
}

class Store {
  constructor(directory) {
    fs.mkdirSync(directory, { recursive: true });
    this.file = path.join(directory, 'wallpapers.json');
    this.data = { version: 1, items: [], activeId: null };
    if (fs.existsSync(this.file)) {
      const data = JSON.parse(fs.readFileSync(this.file, 'utf8'));
      if (data.version !== 1 || !Array.isArray(data.items)) throw new Error('Неизвестный формат библиотеки.');
      const ids = new Set();
      for (const item of data.items) {
        validate(item);
        if (typeof item.id !== 'string' || !item.id || ids.has(item.id)) throw new Error('Повреждён идентификатор обоев.');
        ids.add(item.id);
      }
      if (data.activeId !== null && !ids.has(data.activeId)) throw new Error('Повреждён выбор активных обоев.');
      this.data = data;
    }
  }
  list() { return structuredClone(this.data); }
  get(id) {
    const item = this.data.items.find(item => item.id === id);
    if (!item) throw new Error('Обои не найдены.');
    return structuredClone(item);
  }
  commit(data) {
    const temporary = this.file + '.tmp';
    fs.writeFileSync(temporary, JSON.stringify(data, null, 2), { encoding: 'utf8', mode: 0o600 });
    fs.renameSync(temporary, this.file);
    this.data = data;
  }
  save(input) {
    const value = validate(input);
    const data = this.list();
    const item = { ...value, id: input.id || randomUUID() };
    if (input.id) {
      this.get(input.id);
      data.items[data.items.findIndex(i => i.id === input.id)] = item;
    } else data.items.push(item);
    this.commit(data);
    return item;
  }
  remove(id) {
    this.get(id);
    const data = this.list();
    data.items = data.items.filter(i => i.id !== id);
    if (data.activeId === id) data.activeId = null;
    this.commit(data);
  }
  activate(id) {
    if (id !== null) this.get(id);
    this.commit({ ...this.list(), activeId: id });
  }
}
module.exports = { Store, validate };
