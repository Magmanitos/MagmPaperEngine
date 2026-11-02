const { test } = require('node:test');
const assert = require('node:assert/strict');

test('Win32: reparented wallpaper client fills the monitor with no border insets', { skip: process.platform !== 'win32' }, () => {
  const koffi = require('koffi');
  const user = koffi.load('user32.dll');
  const create = user.func('void * __stdcall CreateWindowExW(uint32_t exStyle, str16 className, str16 title, uint32_t style, int x, int y, int width, int height, void *parent, void *menu, void *instance, void *param)');
  const destroy = user.func('int32_t __stdcall DestroyWindow(void *hwnd)');
  const client = user.func('int32_t __stdcall GetClientRect(void *hwnd, _Out_ int32_t *rect)');
  const origin = user.func('int32_t __stdcall ClientToScreen(void *hwnd, _Inout_ int32_t *point)');
  const dpi = user.func('void * __stdcall SetThreadDpiAwarenessContext(void *context)');
  const previousDpi = dpi(BigInt.asUintN(64, -4n)); // PER_MONITOR_AWARE_V2
  const parent = create(0, 'STATIC', 'MagmPaper test host', 0x80000000, 37, 53, 800, 600, null, null, null, null);
  const child = create(0x00000300, 'STATIC', 'MagmPaper test wallpaper', 0x80c40000, 100, 100, 640, 480, null, null, null, null);
  try {
    assert.ok(parent && child, 'test windows were created');
    const before = [0, 0, 0, 0]; assert.ok(client(child, before));
    assert.ok(before[2] < 640, 'fixture has a non-client border before the fix');
    const { fitToHost } = require('../src/desktop');
    const monitor = fitToHost(child, parent);
    const after = [0, 0, 0, 0]; assert.ok(client(child, after));
    assert.deepEqual(after, [0, 0, monitor.right - monitor.left, monitor.bottom - monitor.top]);
    const point = [0, 0]; assert.ok(origin(child, point));
    assert.deepEqual(point, [monitor.left, monitor.top], 'client origin must cover the exact monitor origin');
  } finally {
    if (child) destroy(child);
    if (parent) destroy(parent);
    if (previousDpi) dpi(previousDpi);
  }
});
test('Win32 coverage excludes desktop overlays, wallpaper, small and minimized windows', { skip: process.platform !== 'win32' }, () => {
  const koffi = require('koffi'), user = koffi.load('user32.dll');
  const { primaryMonitor, isWindowCovering, isDesktopCovered } = require('../src/coverage');
  const create = user.func('void * __stdcall CreateWindowExW(uint32_t exStyle, str16 className, str16 title, uint32_t style, int x, int y, int width, int height, void *parent, void *menu, void *instance, void *param)');
  const destroy = user.func('int32_t __stdcall DestroyWindow(void *hwnd)');
  const show = user.func('int32_t __stdcall ShowWindow(void *hwnd, int command)');
  const setStyle = user.func('intptr_t __stdcall SetWindowLongPtrW(void *hwnd, int index, intptr_t value)');
  const layer = user.func('int32_t __stdcall SetLayeredWindowAttributes(void *hwnd, uint32_t key, uint8_t alpha, uint32_t flags)');
  const position = user.func('int32_t __stdcall SetWindowPos(void *hwnd, void *after, int x, int y, int width, int height, uint32_t flags)');
  const foreground = user.func('int32_t __stdcall SetForegroundWindow(void *hwnd)');
  const area = primaryMonitor().work;
  const hwnd = create(0, 'STATIC', 'MagmPaper coverage fixture', 0x90000000, area.left, area.top, area.right-area.left, area.bottom-area.top, null,null,null,null);
  assert.ok(hwnd);
  try {
    assert.equal(isWindowCovering(hwnd, area), true);
    assert.equal(isWindowCovering(hwnd, area, koffi.address(hwnd)), false, 'wallpaper handle is always excluded');
    for (const extended of [0x80, 0x08000000, 0x20]) {
      setStyle(hwnd, -20, extended);
      assert.equal(isWindowCovering(hwnd, area), false, `overlay style ${extended}`);
    }
    setStyle(hwnd, -20, 0x80000);
    assert.ok(layer(hwnd, 0, 0, 2));
    assert.equal(isWindowCovering(hwnd, area), false, 'invisible layered surface');
    assert.ok(layer(hwnd, 0, 180, 2));
    assert.equal(isWindowCovering(hwnd, area), false, 'partly transparent surface');
    assert.ok(layer(hwnd, 0, 255, 2));
    assert.equal(isWindowCovering(hwnd, area), true, 'opaque layered application');
    setStyle(hwnd, -20, 0);
    assert.ok(position(hwnd, null, area.left+20, area.top+20, 300, 200, 0x0014));
    assert.equal(isWindowCovering(hwnd, area), false, 'ordinary small window');
    assert.ok(position(hwnd, BigInt.asUintN(64, -1n), area.left, area.top, area.right-area.left, area.bottom-area.top, 0x0010));
    foreground(hwnd);
    assert.equal(isDesktopCovered(), true, 'real EnumWindows sees a topmost full-screen application');
    show(hwnd, 6); assert.equal(isWindowCovering(hwnd, area), false);
    show(hwnd, 0); assert.equal(isWindowCovering(hwnd, area), false);
  } finally { destroy(hwnd); }
});
