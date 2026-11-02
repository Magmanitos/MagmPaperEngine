const { app, BrowserWindow, ipcMain, Tray, Menu, nativeImage, screen, dialog, powerMonitor, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { Store } = require('./store');
const { createRecoveryController } = require('./recovery');
const { attach } = require('./desktop');
const { createCursorController } = require('./cursor');
const { createPlaybackController } = require('./playback');
const { isDesktopCovered } = require('./coverage');
const { createStartup, restoreWithRetry, STARTUP_ID } = require('./startup');
const { userDataDirectory, migrateLegacyLibrary } = require('./user-data');
const userDataPath = userDataDirectory(app.getPath('appData'));
fs.mkdirSync(userDataPath, { recursive: true });
app.setPath('userData', userDataPath);
const iconPath = path.join(__dirname, 'assets/icon.png');
const autoStarted = process.argv.includes('--autostart');
const startup = createStartup(app);
let library, manager, wallpaper, tray, health, quitting = false;
let suspended = false, contextRecoveryTimer;
let restoreGeneration = 0, lastStatus = '';
let cursorController;
let playbackController, playbackState = null, screenLocked = false;
function publishPlayback(state) {
  playbackState = state;
  if (manager && !manager.isDestroyed()) manager.webContents.send('playback:state', state);
}
const pending = new Map();
let queue = Promise.resolve();
const serial = task => { const next = queue.then(task); queue = next.catch(() => {}); return next; };
const recovery = createRecoveryController({
  enabled: () => !quitting && !suspended && !!library?.data.activeId,
  rebuild: current => serial(async () => { if (current()) await apply(library.data.activeId, current); }),
  onError: error => notify(`Не удалось автоматически восстановить обои: ${error.message}. Нажмите «На рабочий стол», чтобы повторить.`)
});
async function openWallpaperFolder() {
  const error = await shell.openPath(library.directory);
  if (error) throw new Error(`Не удалось открыть папку обоев: ${error}`);
}
function notify(message) {
  lastStatus = message;
  if (manager && !manager.isDestroyed()) manager.webContents.send('status', message);
}
function secure(window) {
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
}
function openManager() {
  if (manager && !manager.isDestroyed()) { manager.show(); manager.focus(); return; }
  manager = new BrowserWindow({ width: 1160, height: 780, minWidth: 880, minHeight: 620,
    title: 'MagmPaper', icon: iconPath, backgroundColor: '#101115', autoHideMenuBar: true,
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false, sandbox: true } });
  secure(manager);
  manager.webContents.on('did-finish-load', () => { if (lastStatus) notify(lastStatus); });
  manager.loadFile(path.join(__dirname, 'ui/index.html'));
  manager.on('close', event => { if (!quitting && tray) { event.preventDefault(); manager.hide(); } });
}
function openSettings() {
  openManager();
  const send = () => manager.webContents.send('open-settings');
  if (manager.webContents.isLoadingMainFrame()) manager.webContents.once('did-finish-load', send);
  else send();
}
function destroyWallpaper() {
  clearTimeout(contextRecoveryTimer); contextRecoveryTimer = null;
  playbackController?.stop();
  playbackController = null;
  publishPlayback(null);
  cursorController?.stop();
  cursorController = null;
  clearInterval(health);
  health = null;
  if (wallpaper && !wallpaper.isDestroyed()) wallpaper.destroy();
  wallpaper = null;
}
async function stop() { recovery.cancel(); restoreGeneration++; library.activate(null); destroyWallpaper(); notify('Обои остановлены.'); }
async function restoreLastWallpaper() {
  const id = library.data.activeId, generation = ++restoreGeneration;
  if (!id || process.platform !== 'win32') return;
  const shouldContinue = () => !quitting && !suspended && generation === restoreGeneration && library.data.activeId === id && !wallpaper;
  try {
    await restoreWithRetry({ shouldContinue, attempt: () => serial(async () => { if (shouldContinue()) await apply(id, shouldContinue); }) });
  } catch (error) {
    if (generation !== restoreGeneration || quitting) return;
    notify(error.message);
    openManager();
  }
}
async function apply(id, current = () => !quitting && !suspended) {
  if (process.platform !== 'win32') throw new Error('Установка обоев доступна в Windows 11. Предпросмотр работает и здесь.');
  const item = library.get(id);
  const display = screen.getPrimaryDisplay();
  const next = new BrowserWindow({ ...display.bounds, show: false, frame: false, skipTaskbar: true, focusable: false,
    backgroundColor: '#000000', resizable: false, thickFrame: false, hasShadow: false,
    webPreferences: { preload: path.join(__dirname, 'wallpaper-preload.js'), contextIsolation: true, nodeIntegration: false, sandbox: true, backgroundThrottling: false } });
  secure(next);
  next.setIgnoreMouseEvents(true);
  const senderId = next.webContents.id;
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Истекло время запуска шейдера.')), 15000);
      pending.set(senderId, { code: item.code, finish: error => {
        clearTimeout(timer);
        if (error) reject(new Error(String(error))); else resolve();
      } });
      next.loadFile(path.join(__dirname, 'ui/wallpaper.html')).catch(error => {
        clearTimeout(timer); reject(error);
      });
    });
    if (!current()) { next.destroy(); return; }
    const healthy = attach(next);
    library.activate(id);
    destroyWallpaper();
    wallpaper = next;
    next.showInactive();
    const wallpaperHandle = next.getNativeWindowHandle().readBigUInt64LE();
    cursorController = createCursorController(next, screen, display.bounds);
    playbackController = createPlaybackController({ window: next, cursor: cursorController,
      getSettings: () => library.data.settings, isCovered: () => isDesktopCovered(wallpaperHandle),
      isOnBattery: () => powerMonitor.isOnBatteryPower(), isLocked: () => screenLocked, onChange: publishPlayback,
      onError: error => notify(`Автопауза временно недоступна: ${error.message}`) });
    health = setInterval(() => {
      if (wallpaper !== next) return;
      try {
        if (next.isDestroyed()) { destroyWallpaper(); recovery.ensure(); }
        else if (!healthy()) recovery.ensure();
        else if (next.isMinimized() || !next.isVisible()) next.showInactive();
      } catch { recovery.ensure(); }
    }, 1500);
    next.webContents.on('render-process-gone', () => {
      if (wallpaper === next) { destroyWallpaper(); recovery.ensure(); }
    });
    notify(`На рабочем столе: ${item.name}`);
  } catch (error) { next.destroy(); throw error; }
  finally { pending.delete(senderId); }
}
function handle(channel, callback) {
  ipcMain.handle(channel, (event, ...args) => {
    if (!manager || event.sender !== manager.webContents || event.senderFrame !== manager.webContents.mainFrame)
      throw new Error('Недоступно.');
    return serial(() => callback(...args));
  });
}
if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', (_event, argv) => { if (!argv.includes('--autostart')) openManager(); });
  app.whenReady().then(async () => {
    if (process.platform === 'win32') app.setAppUserModelId(STARTUP_ID);
    try {
      const directory = migrateLegacyLibrary(app.getPath('appData'));
      const firstRun = !fs.existsSync(path.join(directory, 'wallpapers.json'));
      library = new Store(directory);
      if (firstRun) library.save({ name: 'Северное сияние', code: require('./example') });
    } catch (error) {
      dialog.showErrorBox('Не удалось открыть библиотеку', `${error.message}\nФайл не перезаписан. Проверьте wallpapers.json в ${app.getPath('userData')}.`);
      app.quit(); return;
    }
    handle('library:list', () => {
      const previous = library.data.activeId, data = library.list();
      if (previous && !data.activeId) { recovery.cancel(); restoreGeneration++; destroyWallpaper(); }
      return { ...data, runningId: wallpaper && !wallpaper.isDestroyed() ? data.activeId : null, platform: process.platform, playback: playbackState };
    });
    handle('library:open-folder', openWallpaperFolder);
    handle('library:save', item => library.save(item));
    handle('startup:get', () => startup.get());
    handle('startup:set', enabled => startup.set(enabled));
    handle('mouse:set', enabled => {
      library.setMouseEnabled(enabled);
      playbackController?.refresh();
      return enabled;
    });
    handle('settings:set', patch => {
      const settings = library.setSettings(patch);
      playbackController?.refresh();
      return settings;
    });
    handle('library:remove', id => {
      const active = library.data.activeId === id;
      library.remove(id);
      if (active) { recovery.cancel(); restoreGeneration++; destroyWallpaper(); }
    });
    handle('library:apply', id => { recovery.cancel(); restoreGeneration++; return apply(id); });
    handle('library:stop', stop);
    ipcMain.handle('wallpaper:source', event => {
      const entry = pending.get(event.sender.id);
      if (!entry) throw new Error('Источник недоступен.');
      return entry.code;
    });
    ipcMain.on('wallpaper:result', (event, error, recoverable) => {
      const entry = pending.get(event.sender.id);
      if (entry) entry.finish(error);
      else if (wallpaper && event.sender === wallpaper.webContents && error) {
        if (recoverable) recovery.ensure();
        else { destroyWallpaper(); notify(String(error)); }
      }
    });
    ipcMain.on('wallpaper:context', (event, lost) => {
      if (!wallpaper || event.sender !== wallpaper.webContents) return;
      clearTimeout(contextRecoveryTimer);
      if (lost) contextRecoveryTimer = setTimeout(() => recovery.ensure(), 4000);
      else { wallpaper.showInactive(); playbackController?.refresh(); }
    });
    tray = new Tray(nativeImage.createFromPath(iconPath).resize({ width: 32, height: 32 }));
    tray.setToolTip('MagmPaper — живые обои');
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: 'Открыть MagmPaper', click: openManager },
      { label: 'Папка обоев', click: () => openWallpaperFolder().catch(e => notify(e.message)) },
      { label: 'Параметры', click: openSettings },
      { label: 'Остановить обои', click: () => serial(stop).catch(e => notify(e.message)) },
      { type: 'separator' }, { label: 'Выход', click: () => app.quit() }
    ]));
    tray.on('double-click', openManager);
    if (!autoStarted) openManager();
    const displayChanged = () => { restoreGeneration++; recovery.request(); };
    screen.on('display-metrics-changed', displayChanged);
    screen.on('display-added', displayChanged);
    screen.on('display-removed', displayChanged);
    powerMonitor.on('suspend', () => { suspended = true; recovery.cancel(); restoreGeneration++; destroyWallpaper(); });
    powerMonitor.on('resume', () => { suspended = false; recovery.request(); });
    powerMonitor.on('lock-screen', () => { screenLocked = true; playbackController?.refresh(); });
    powerMonitor.on('unlock-screen', () => { screenLocked = false; playbackController?.refresh(); });
    powerMonitor.on('on-battery', () => playbackController?.refresh());
    powerMonitor.on('on-ac', () => playbackController?.refresh());
    void restoreLastWallpaper();
  }).catch(error => { dialog.showErrorBox('MagmPaper', error.message); app.quit(); });
  app.on('before-quit', () => { quitting = true; recovery.cancel(); destroyWallpaper(); });
  app.on('child-process-gone', (_event, details) => { if (details.type === 'GPU') recovery.ensure(); });
  app.on('window-all-closed', () => {});
}
