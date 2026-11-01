const { app, BrowserWindow, ipcMain, Tray, Menu, nativeImage, screen, dialog, powerMonitor } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { Store } = require('./store');
const { attach } = require('./desktop');
const { createCursorController } = require('./cursor');
const { createPlaybackController } = require('./playback');
const { isDesktopCovered } = require('./coverage');
const { createStartup, restoreWithRetry, STARTUP_ID } = require('./startup');
const autoStarted = process.argv.includes('--autostart');
const startup = createStartup(app);
let library, manager, wallpaper, tray, health, quitting = false;
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
    title: 'MagmPaper', backgroundColor: '#101115', autoHideMenuBar: true,
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
async function stop() { restoreGeneration++; library.activate(null); destroyWallpaper(); notify('Обои остановлены.'); }
async function restoreLastWallpaper() {
  const id = library.data.activeId, generation = ++restoreGeneration;
  if (!id || process.platform !== 'win32') return;
  const shouldContinue = () => !quitting && generation === restoreGeneration && library.data.activeId === id && !wallpaper;
  try {
    await restoreWithRetry({ shouldContinue, attempt: () => serial(async () => { if (shouldContinue()) await apply(id); }) });
  } catch (error) {
    if (generation !== restoreGeneration || quitting) return;
    notify(error.message);
    openManager();
  }
}
async function apply(id) {
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
    const healthy = attach(next);
    library.activate(id);
    destroyWallpaper();
    wallpaper = next;
    next.showInactive();
    cursorController = createCursorController(next, screen, display.bounds);
    playbackController = createPlaybackController({ window: next, cursor: cursorController,
      getSettings: () => library.data.settings, isCovered: isDesktopCovered,
      isOnBattery: () => powerMonitor.isOnBatteryPower(), isLocked: () => screenLocked, onChange: publishPlayback,
      onError: error => notify(`Автопауза временно недоступна: ${error.message}`) });
    health = setInterval(() => {
      if (!wallpaper || wallpaper.isDestroyed()) return;
      try {
        if (!healthy()) {
          destroyWallpaper();
          notify('Проводник был перезапущен. Нажмите «На рабочий стол», чтобы восстановить обои.');
        }
      } catch (error) { destroyWallpaper(); notify(error.message); }
    }, 3000);
    next.webContents.on('render-process-gone', () => {
      if (wallpaper === next) { destroyWallpaper(); notify('Рендерер обоев остановился. Попробуйте другой шейдер.'); }
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
      const directory = app.getPath('userData');
      const firstRun = !fs.existsSync(path.join(directory, 'wallpapers.json'));
      library = new Store(directory);
      if (firstRun) library.save({ name: 'Северное сияние', code: require('./example') });
    } catch (error) {
      dialog.showErrorBox('Не удалось открыть библиотеку', `${error.message}\nФайл не перезаписан. Проверьте wallpapers.json в ${app.getPath('userData')}.`);
      app.quit(); return;
    }
    handle('library:list', () => ({ ...library.list(), runningId: wallpaper && !wallpaper.isDestroyed() ? library.data.activeId : null, platform: process.platform, playback: playbackState }));
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
      if (active) destroyWallpaper();
    });
    handle('library:apply', id => { restoreGeneration++; return apply(id); });
    handle('library:stop', stop);
    ipcMain.handle('wallpaper:source', event => {
      const entry = pending.get(event.sender.id);
      if (!entry) throw new Error('Источник недоступен.');
      return entry.code;
    });
    ipcMain.on('wallpaper:result', (event, error) => {
      const entry = pending.get(event.sender.id);
      if (entry) entry.finish(error);
      else if (wallpaper && event.sender === wallpaper.webContents && error) {
        destroyWallpaper(); notify(String(error));
      }
    });
    const pixels = Buffer.alloc(32 * 32 * 4);
    for (let y = 0; y < 32; y++) for (let x = 0; x < 32; x++) {
      const i = (y * 32 + x) * 4;
      pixels[i] = 225; pixels[i + 1] = 130; pixels[i + 2] = 125;
      pixels[i + 3] = x > 3 && x < 28 && y > 3 && y < 28 ? 255 : 0;
    }
    tray = new Tray(nativeImage.createFromBitmap(pixels, { width: 32, height: 32 }));
    tray.setToolTip('MagmPaper — живые обои');
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: 'Открыть MagmPaper', click: openManager },
      { label: 'Параметры', click: openSettings },
      { label: 'Остановить обои', click: () => serial(stop).catch(e => notify(e.message)) },
      { type: 'separator' }, { label: 'Выход', click: () => app.quit() }
    ]));
    tray.on('double-click', openManager);
    if (!autoStarted) openManager();
    screen.on('display-metrics-changed', () => {
      if (wallpaper) serial(() => apply(library.data.activeId)).catch(e => notify(e.message));
    });
    powerMonitor.on('suspend', () => { restoreGeneration++; destroyWallpaper(); });
    powerMonitor.on('resume', () => {
      void restoreLastWallpaper();
    });
    powerMonitor.on('lock-screen', () => { screenLocked = true; playbackController?.refresh(); });
    powerMonitor.on('unlock-screen', () => { screenLocked = false; playbackController?.refresh(); });
    powerMonitor.on('on-battery', () => playbackController?.refresh());
    powerMonitor.on('on-ac', () => playbackController?.refresh());
    void restoreLastWallpaper();
  }).catch(error => { dialog.showErrorBox('MagmPaper', error.message); app.quit(); });
  app.on('before-quit', () => { quitting = true; destroyWallpaper(); });
  app.on('window-all-closed', () => {});
}
