// Explorer's WorkerW hosting protocol is undocumented. Fail explicitly if it changes.
let api;
function nativeApi() {
  if (process.platform !== 'win32') throw new Error('Живые обои доступны только в Windows 11.');
  if (api) return api;
  const koffi = require('koffi');
  const user = koffi.load('user32.dll');
  const callback = koffi.proto('int32_t __stdcall EnumWindowsProc(void *hwnd, intptr_t lparam)');
  const rect = koffi.struct('MagmRect', { left: 'int32_t', top: 'int32_t', right: 'int32_t', bottom: 'int32_t' });
  koffi.struct('MagmMonitorInfo', { cbSize: 'uint32_t', monitor: rect, work: rect, flags: 'uint32_t' });
  api = {
    koffi, callback,
    find: user.func('void * __stdcall FindWindowW(str16 className, str16 title)'),
    findEx: user.func('void * __stdcall FindWindowExW(void *parent, void *after, str16 className, str16 title)'),
    enum: user.func('int32_t __stdcall EnumWindows(EnumWindowsProc *callback, intptr_t lparam)'),
    send: user.func('intptr_t __stdcall SendMessageTimeoutW(void *hwnd, uint32_t msg, uintptr_t wparam, intptr_t lparam, uint32_t flags, uint32_t timeout, _Out_ uintptr_t *result)'),
    parent: user.func('void * __stdcall SetParent(void *child, void *parent)'),
    getParent: user.func('void * __stdcall GetParent(void *child)'),
    getStyle: user.func('intptr_t __stdcall GetWindowLongPtrW(void *hwnd, int index)'),
    setStyle: user.func('intptr_t __stdcall SetWindowLongPtrW(void *hwnd, int index, intptr_t value)'),
    position: user.func('int32_t __stdcall SetWindowPos(void *hwnd, void *after, int x, int y, int width, int height, uint32_t flags)'),
    valid: user.func('int32_t __stdcall IsWindow(void *hwnd)'),
    monitor: user.func('void * __stdcall MonitorFromWindow(void *hwnd, uint32_t flags)'),
    monitorInfo: user.func('int32_t __stdcall GetMonitorInfoW(void *monitor, _Inout_ MagmMonitorInfo *info)'),
    map: user.func('int32_t __stdcall MapWindowPoints(void *from, void *to, _Inout_ int32_t *points, uint32_t count)')
  };
  return api;
}
function address(pointer) { return pointer === null ? 0n : nativeApi().koffi.address(pointer); }
function desktopNotReady(message) { return Object.assign(new Error(message), { code: 'DESKTOP_NOT_READY' }); }
function fitToHost(hwnd, worker) {
  const a = nativeApi();
  // Read physical monitor bounds while this is still a top-level window.
  // rcMonitor includes the taskbar area; rcWork and DIP sizes must not be used here.
  const info = { cbSize: 40, monitor: { left: 0, top: 0, right: 0, bottom: 0 }, work: { left: 0, top: 0, right: 0, bottom: 0 }, flags: 0 };
  if (!a.monitorInfo(a.monitor(hwnd, 1), info)) throw new Error('Не удалось получить размеры монитора.');
  const bounds = info.monitor;
  // Frameless Electron windows can retain non-client resize borders (8 px per side).
  // Remove them before applying SWP_FRAMECHANGED so the canvas fills the whole window.
  const style = Number(a.getStyle(hwnd, -16));
  a.setStyle(hwnd, -16, (style & ~(0x80000000 | 0x00c00000 | 0x00040000)) | 0x40000000);
  const extended = Number(a.getStyle(hwnd, -20));
  a.setStyle(hwnd, -20, extended & ~(0x00000100 | 0x00000200 | 0x00020000));
  a.parent(hwnd, worker);
  if (address(a.getParent(hwnd)) !== address(worker)) throw new Error('Не удалось прикрепить обои к рабочему столу.');
  const point = [bounds.left, bounds.top];
  a.map(null, worker, point, 1);
  if (!a.position(hwnd, null, point[0], point[1], bounds.right - bounds.left, bounds.bottom - bounds.top, 0x0004 | 0x0010 | 0x0020))
    throw new Error('Не удалось задать размер обоев.');
  return bounds;
}
function attach(window) {
  const a = nativeApi();
  const progman = a.find('Progman', null);
  if (!progman) throw desktopNotReady('Рабочий стол Explorer не найден.');
  a.send(progman, 0x052c, 0, 0, 0x0002, 2000, [0]);
  let worker = null;
  a.enum(hwnd => {
    if (a.findEx(hwnd, null, 'SHELLDLL_DefView', null)) {
      worker = a.findEx(null, hwnd, 'WorkerW', null);
      return worker ? 0 : 1;
    }
    return 1;
  }, 0);
  // New Windows 11 Explorer builds can keep WorkerW below Progman.
  if (!worker) worker = a.findEx(progman, null, 'WorkerW', null);
  if (!worker) throw desktopNotReady('Windows не предоставила слой WorkerW. Перезапустите Проводник и повторите.');
  const hwnd = window.getNativeWindowHandle().readBigUInt64LE();
  fitToHost(hwnd, worker);
  return () => a.valid(worker) && address(a.getParent(hwnd)) === address(worker);
}
module.exports = { attach, fitToHost };
