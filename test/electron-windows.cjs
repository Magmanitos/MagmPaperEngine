// Real Electron / Win32 integration in an isolated host window and registry entry.
const { app, BrowserWindow, screen } = require('electron');
const assert = require('node:assert/strict');
const path = require('node:path');
const { fitToHost } = require('../src/desktop');
const { createStartup } = require('../src/startup');
app.whenReady().then(async () => {
  let child, parent, destroy, startup;
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
    parent = create(0, 'STATIC', 'MagmPaper isolated host', 0x80000000, 37, 53, 800, 600, null, null, null, null);
    assert.ok(parent);
    child = new BrowserWindow({ ...screen.getPrimaryDisplay().bounds, show: false, frame: false,
      thickFrame: false, hasShadow: false, resizable: false, focusable: false, skipTaskbar: true,
      webPreferences: { sandbox: true, nodeIntegration: false, contextIsolation: true } });
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
    console.log('PASS: actual Electron viewport covers monitor; Windows login settings enable/disable with spaces.');
  } catch (error) {
    console.error(error); process.exitCode = 1;
  } finally {
    if (startup) startup.set(false);
    if (child) child.destroy();
    if (parent) destroy(parent);
    app.exit(process.exitCode || 0);
  }
});
