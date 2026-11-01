// Explorer's WorkerW hosting protocol is undocumented. Fail explicitly if it changes.
let api;
function nativeApi() {
  if (process.platform !== 'win32') throw new Error('Живые обои доступны только в Windows 11.');
  if (api) return api;
  const koffi = require('koffi');
  const user = koffi.load('user32.dll');
  const callback = koffi.proto('int32_t __stdcall EnumWindowsProc(void *hwnd, intptr_t lparam)');
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
    valid: user.func('int32_t __stdcall IsWindow(void *hwnd)')
  };
  return api;
}
function address(pointer) { return pointer === null ? 0n : nativeApi().koffi.address(pointer); }
function attach(window) {
  const a = nativeApi();
  const progman = a.find('Progman', null);
  if (!progman) throw new Error('Рабочий стол Explorer не найден.');
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
  if (!worker) throw new Error('Windows не предоставила слой WorkerW. Перезапустите Проводник и повторите.');
  const hwnd = window.getNativeWindowHandle().readBigUInt64LE();
  const style = Number(a.getStyle(hwnd, -16));
  a.setStyle(hwnd, -16, (style & ~0x80000000) | 0x40000000); // WS_POPUP -> WS_CHILD
  a.parent(hwnd, worker);
  if (address(a.getParent(hwnd)) !== address(worker)) throw new Error('Не удалось прикрепить обои к рабочему столу.');
  // The window was positioned on the primary monitor before reparenting.
  // Map its screen coordinates into WorkerW's client coordinates.
  const user = a.koffi.load('user32.dll');
  const map = user.func('int __stdcall MapWindowPoints(void *from, void *to, _Inout_ int32_t *points, uint32_t count)');
  const { screen } = require('electron');
  const display = screen.getPrimaryDisplay();
  const pixel = screen.dipToScreenPoint({ x: display.bounds.x, y: display.bounds.y });
  const point = [pixel.x, pixel.y];
  map(null, worker, point, 1);
  if (!a.position(hwnd, null, point[0], point[1], Math.round(display.bounds.width * display.scaleFactor), Math.round(display.bounds.height * display.scaleFactor), 0x0010 | 0x0020 | 0x0040))
    throw new Error('Не удалось задать размер обоев.');
  return () => a.valid(worker) && address(a.getParent(hwnd)) === address(worker);
}
module.exports = { attach };
