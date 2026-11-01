const STARTUP_ID = 'com.magmanitos.magmpaper';
function createStartup(app, platform = process.platform, executable = process.execPath, startupId = STARTUP_ID) {
  // Electron quotes each argument itself, including project paths containing spaces.
  const options = { path: executable, args: app.isPackaged ? ['--autostart'] : [app.getAppPath(), '--autostart'] };
  function get() {
    if (platform !== 'win32') return { supported: false, enabled: false };
    const state = app.getLoginItemSettings(options);
    const entry = state.launchItems.find(item => item.name === startupId && item.scope === 'user');
    return { supported: true, enabled: state.openAtLogin && !!entry?.enabled };
  }
  function set(enabled) {
    if (typeof enabled !== 'boolean') throw new Error('Некорректная настройка автозапуска.');
    if (platform !== 'win32') throw new Error('Автозапуск доступен только в Windows.');
    app.setLoginItemSettings({ ...options, openAtLogin: enabled, enabled: true });
    const state = get();
    if (state.enabled !== enabled) throw new Error('Не удалось изменить автозапуск. Проверьте настройки автозагрузки Windows.');
    return state;
  }
  return { get, set };
}

async function restoreWithRetry({ attempt, shouldContinue, attempts = 20, wait = () => new Promise(resolve => setTimeout(resolve, 3000)) }) {
  for (let index = 0; index < attempts; index++) {
    if (!shouldContinue()) return;
    try { await attempt(); return; }
    catch (error) {
      if (error.code !== 'DESKTOP_NOT_READY' || index === attempts - 1) throw error;
      await wait();
    }
  }
}
module.exports = { createStartup, restoreWithRetry, STARTUP_ID };
