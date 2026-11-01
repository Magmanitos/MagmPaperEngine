// Real Electron / Win32 integration in an isolated host window and registry entry.
const { app, BrowserWindow, screen } = require('electron');
const assert = require('node:assert/strict');
const path = require('node:path');
const { fitToHost } = require('../src/desktop');
const { createStartup } = require('../src/startup');
const { createCursorController, normalizeCursor } = require('../src/cursor');
app.whenReady().then(async () => {
  let child, parent, destroy, startup, stopCursor, restoreCursor;
  try {
    assert.equal(process.platform, 'win32');
    const id = `com.magmanitos.magmpaper.test.${process.pid}`;
    app.setAppUserModelId(id);
    startup = createStartup({
      isPackaged: false,
      getAppPath: () => path.join(app.getPath('temp'), 'MagmPaper test with spaces'),
      getLoginItemSettings: options => app.getLoginItemSettings(options),
      setLoginItemSettings: options => app.setLoginItemSettings(options)
    }, 'win32', process.execPath, id);
    assert.equal(startup.set(true).enabled, true, 'Windows registry startup readback');
    assert.equal(startup.set(false).enabled, false, 'Windows registry entry removed');
    const koffi = require('koffi'), user = koffi.load('user32.dll');
    const create = user.func('void * __stdcall CreateWindowExW(uint32_t exStyle, str16 className, str16 title, uint32_t style, int x, int y, int width, int height, void *parent, void *menu, void *instance, void *param)');
    destroy = user.func('int32_t __stdcall DestroyWindow(void *hwnd)');
    const client = user.func('int32_t __stdcall GetClientRect(void *hwnd, _Out_ int32_t *rect)');
    const origin = user.func('int32_t __stdcall ClientToScreen(void *hwnd, _Inout_ int32_t *point)');
    const setCursor = user.func('int32_t __stdcall SetCursorPos(int x, int y)');
    const getCursor = user.func('int32_t __stdcall GetCursorPos(_Out_ int32_t *point)');
    const getStyle = user.func('intptr_t __stdcall GetWindowLongPtrW(void *hwnd, int index)');
    parent = create(0, 'STATIC', 'MagmPaper isolated host', 0x80000000, 37, 53, 800, 600, null, null, null, null);
    assert.ok(parent);
    child = new BrowserWindow({ ...screen.getPrimaryDisplay().bounds, show: false, frame: false,
      thickFrame: false, hasShadow: false, resizable: false, focusable: false, skipTaskbar: true,
      webPreferences: { preload: path.join(__dirname, '../src/wallpaper-preload.js'), sandbox: true, nodeIntegration: false, contextIsolation: true } });
    child.setIgnoreMouseEvents(true);
    await child.loadURL('data:text/html,<html><body style="margin:0">Window geometry test</body></html>');
    const hwnd = child.getNativeWindowHandle().readBigUInt64LE();
    const monitor = fitToHost(hwnd, parent);
    child.showInactive();
    await new Promise(resolve => setTimeout(resolve, 250));
    const rect = [0, 0, 0, 0]; assert.ok(client(hwnd, rect));
    assert.deepEqual(rect, [0, 0, monitor.right - monitor.left, monitor.bottom - monitor.top]);
    const point = [0, 0]; assert.ok(origin(hwnd, point));
    assert.deepEqual(point, [monitor.left, monitor.top]);
    const viewport = await child.webContents.executeJavaScript('({width:innerWidth,height:innerHeight,scale:devicePixelRatio})');
    assert.equal(Math.round(viewport.width * viewport.scale), rect[2]);
    assert.equal(Math.round(viewport.height * viewport.scale), rect[3]);
    assert.ok(Number(getStyle(hwnd, -20)) & 0x20, 'wallpaper remains click-through');
    const oldCursor = [0, 0]; assert.ok(getCursor(oldCursor));
    restoreCursor = () => setCursor(...oldCursor);
    await child.webContents.executeJavaScript('window.mouseMessages = 0; window.wallpaper.onMouse(value => { window.cursorReceived = value; window.mouseMessages++; }); window.wallpaper.onMouseEnabled(value => { window.mouseEnabled = value; }); true');
    const bounds = screen.getPrimaryDisplay().bounds;
    const controller = createCursorController(child, screen, bounds);
    stopCursor = () => controller.stop();
    controller.setEnabled(true);
    const positions = [];
    for (const fraction of [0.25, 0.75]) {
      const dip = { x: Math.round(bounds.x + bounds.width * fraction), y: Math.round(bounds.y + bounds.height * fraction) };
      const pixel = screen.dipToScreenPoint(dip);
      assert.ok(setCursor(pixel.x, pixel.y));
      const expected = normalizeCursor(screen.getCursorScreenPoint(), bounds);
      let received;
      for (let tries = 0; tries < 50; tries++) {
        received = await child.webContents.executeJavaScript('window.cursorReceived');
        if (received && Math.abs(received[0] - expected[0]) < 0.005 && Math.abs(received[1] - expected[1]) < 0.005) break;
        await new Promise(resolve => setTimeout(resolve, 40));
      }
      assert.ok(received && Math.abs(received[0] - expected[0]) < 0.005 && Math.abs(received[1] - expected[1]) < 0.005, 'OS cursor reaches sandboxed renderer through preload');
      positions.push(received);
    }
    assert.ok(positions[1][0] - positions[0][0] > 0.4 && positions[0][1] - positions[1][1] > 0.4);
    controller.setEnabled(false);
    await new Promise(resolve => setTimeout(resolve, 100));
    const beforeDisable = await child.webContents.executeJavaScript('({enabled:window.mouseEnabled,count:window.mouseMessages})');
    assert.equal(beforeDisable.enabled, false);
    const target = screen.dipToScreenPoint({ x: Math.round(bounds.x + bounds.width * 0.5), y: Math.round(bounds.y + bounds.height * 0.5) });
    assert.ok(setCursor(target.x,target.y));
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await child.webContents.executeJavaScript('window.mouseMessages'), beforeDisable.count);
    controller.setEnabled(true);
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(await child.webContents.executeJavaScript('window.mouseEnabled'), true);
    assert.ok(await child.webContents.executeJavaScript('window.mouseMessages') > beforeDisable.count);
    console.log('PASS: Electron viewport, Windows startup, actual OS cursor through IPC while click-through is enabled.');
  } catch (error) {
    console.error(error); process.exitCode = 1;
    if (process.env.GITHUB_ACTIONS) {
      const message = String(error.stack || error).replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
      console.log(`::error::${message}`);
    }
  } finally {
    stopCursor?.();
    restoreCursor?.();
    if (startup) startup.set(false);
    if (child) child.destroy();
    if (parent) destroy(parent);
    app.exit(process.exitCode || 0);
  }
});
