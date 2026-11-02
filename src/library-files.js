const fs = require('node:fs');
const path = require('node:path');
function safeFile(file) {
  return typeof file === 'string' && file.length <= 180 && /\.glsl$/i.test(file) && !/[<>:"/\\|?*\x00-\x1f]/.test(file) && !/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(file);
}
function readCode(directory, file) {
  if (!safeFile(file)) throw new Error('Некорректное имя файла обоев.');
  const target = path.join(directory, file);
  if (!fs.existsSync(target)) return null;
  const stat = fs.lstatSync(target);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 400000) throw new Error(`Недоступен файл обоев: ${file}`);
  return fs.readFileSync(target, 'utf8');
}
function fileName(name, occupied) {
  let base = name.normalize('NFC').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').replace(/[. ]+$/g, '').trim() || 'Обои';
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(base)) base = '_' + base;
  let file = base + '.glsl', index = 2;
  while (occupied.has(file.toLowerCase())) file = `${base} (${index++}).glsl`;
  return file;
}
function replace(file, contents) {
  if (contents === null) { fs.rmSync(file, { force: true }); return; }
  const temporary = file + '.tmp';
  fs.writeFileSync(temporary, contents, { encoding: 'utf8', mode: 0o600 });
  fs.renameSync(temporary, file);
}
// A journal makes a multi-file edit recoverable even if Windows stops midway.
function recover(directory, manifest) {
  const journal = manifest + '.journal';
  if (!fs.existsSync(journal)) return;
  const transaction = JSON.parse(fs.readFileSync(journal, 'utf8'));
  const revision = fs.existsSync(manifest) ? JSON.parse(fs.readFileSync(manifest, 'utf8')).revision : null;
  if (revision !== transaction.revision) {
    for (const change of transaction.changes) {
      const current = readCode(directory, change.file);
      if (current === change.before) continue;
      if (current !== change.after) throw new Error(`Файл ${change.file} изменён вне приложения во время сохранения. Сохранены файл и журнал ${journal}.`);
      replace(path.join(directory, change.file), change.before);
    }
  }
  fs.unlinkSync(journal);
}
function commitFiles(directory, manifest, data, changes) {
  const journal = manifest + '.journal';
  // Fail before changing shaders if the manifest cannot be staged.
  fs.writeFileSync(manifest + '.tmp', JSON.stringify(data, null, 2), { encoding: 'utf8', mode: 0o600 });
  replace(journal, JSON.stringify({ revision: data.revision, changes }));
  try {
    for (const change of changes) {
      if (readCode(directory, change.file) !== change.before) throw new Error(`Файл ${change.file} изменён другим редактором. Обновите коллекцию.`);
      replace(path.join(directory, change.file), change.after);
    }
    fs.renameSync(manifest + '.tmp', manifest);
  } catch (error) {
    try { recover(directory, manifest); }
    catch (recoveryError) { throw new Error(`${error.message}\n${recoveryError.message}`); }
    throw error;
  }
  // A leftover committed journal is harmless and will be cleaned on next load.
  try { fs.unlinkSync(journal); } catch {}
}
module.exports = { safeFile, readCode, fileName, recover, commitFiles };
