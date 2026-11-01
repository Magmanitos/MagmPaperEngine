const { app, BrowserWindow, ipcMain, Tray, Menu, nativeImage, screen, dialog, powerMonitor } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { Store } = require('./store');
const { attach } = require('./desktop');
let library, manager, wallpaper, tray, health, quitting = false;
const pending = new Map();
let queue = Promise.resolve();
const serial = task => { const next = queue.then(task); queue = next.catch(() => {}); return next; };
function notify(message) { if (manager && !manager.isDestroyed()) manager.webContents.send('status', message); }
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
  manager.loadFile(path.join(__dirname, 'ui/index.html'));
  manager.on('close', event => { if (!quitting && tray) { event.preventDefault(); manager.hide(); } });
}
function destroyWallpaper() {
  clearInterval(health);
  health = null;
  if (wallpaper && !wallpaper.isDestroyed()) wallpaper.destroy();
  wallpaper = null;
}
async function stop() { library.activate(null); destroyWallpaper(); notify('Обои остановлены.'); }
async function apply(id) {
  if (process.platform !== 'win32') throw new Error('Установка обоев доступна в Windows 11. Предпросмотр работает и здесь.');
  const item = library.get(id);
  const display = screen.getPrimaryDisplay();
  const next = new BrowserWindow({ ...display.bounds, show: false, frame: false, skipTaskbar: true, focusable: false,
    backgroundColor: '#000000', resizable: false,
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
  app.on('second-instance', openManager);
  app.whenReady().then(async () => {
    try {
      const directory = app.getPath('userData');
      const firstRun = !fs.existsSync(path.join(directory, 'wallpapers.json'));
      library = new Store(directory);
      if (firstRun) library.save({ name: 'Северное сияние', code: require('./example') });
    } catch (error) {
      dialog.showErrorBox('Не удалось открыть библиотеку', `${error.message}\nФайл не перезаписан. Проверьте wallpapers.json в ${app.getPath('userData')}.`);
      app.quit(); return;
    }
    handle('library:list', () => ({ ...library.list(), runningId: wallpaper && !wallpaper.isDestroyed() ? library.data.activeId : null, platform: process.platform }));
    handle('library:save', item => library.save(item));
    handle('library:remove', id => {
      const active = library.data.activeId === id;
      library.remove(id);
      if (active) destroyWallpaper();
    });
    handle('library:apply', apply);
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
      { label: 'Остановить обои', click: () => serial(stop).catch(e => notify(e.message)) },
      { type: 'separator' }, { label: 'Выход', click: () => app.quit() }
    ]));
    tray.on('double-click', openManager);
    openManager();
    screen.on('display-metrics-changed', () => {
      if (wallpaper) serial(() => apply(library.data.activeId)).catch(e => notify(e.message));
    });
    powerMonitor.on('suspend', destroyWallpaper);
    powerMonitor.on('resume', () => {
      if (library.data.activeId) serial(() => apply(library.data.activeId)).catch(e => notify(e.message));
    });
    if (library.data.activeId && process.platform === 'win32') {
      try { await serial(() => apply(library.data.activeId)); } catch (error) { notify(error.message); }
    }
  }).catch(error => { dialog.showErrorBox('MagmPaper', error.message); app.quit(); });
  app.on('before-quit', () => { quitting = true; destroyWallpaper(); });
  app.on('window-all-closed', () => {});
}
