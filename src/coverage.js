let api;
function nativeApi() {
  if (api) return api;
  const koffi = require('koffi'), user = koffi.load('user32.dll'), dwm = koffi.load('dwmapi.dll');
  const rect = koffi.struct('CoverageRect', { left: 'int32_t', top: 'int32_t', right: 'int32_t', bottom: 'int32_t' });
  koffi.struct('CoverageMonitor', { size: 'uint32_t', monitor: rect, work: rect, flags: 'uint32_t' });
  koffi.proto('int32_t __stdcall CoverageEnum(void *hwnd, intptr_t param)');
  api = {
    visible: user.func('int32_t __stdcall IsWindowVisible(void *hwnd)'),
    minimized: user.func('int32_t __stdcall IsIconic(void *hwnd)'),
    style: user.func('intptr_t __stdcall GetWindowLongPtrW(void *hwnd, int index)'),
    name: user.func('int32_t __stdcall GetClassNameW(void *hwnd, _Out_ uint16_t *name, int count)'),
    rect: user.func('int32_t __stdcall GetWindowRect(void *hwnd, _Out_ int32_t *rect)'),
    attribute: dwm.func('int32_t __stdcall DwmGetWindowAttribute(void *hwnd, uint32_t attribute, _Out_ int32_t *value, uint32_t size)'),
    monitor: user.func('void * __stdcall MonitorFromWindow(void *hwnd, uint32_t flags)'),
    info: user.func('int32_t __stdcall GetMonitorInfoW(void *monitor, _Inout_ CoverageMonitor *info)'),
    enumerate: user.func('int32_t __stdcall EnumWindows(CoverageEnum *callback, intptr_t param)')
  };
  return api;
}
function coversArea(rect, area) {
  return rect[0] <= area.left + 2 && rect[1] <= area.top + 2 && rect[2] >= area.right - 2 && rect[3] >= area.bottom - 2;
}
function primaryMonitor() {
  const a = nativeApi(), empty = () => ({ left: 0, top: 0, right: 0, bottom: 0 });
  const info = { size: 40, monitor: empty(), work: empty(), flags: 0 };
  if (!a.info(a.monitor(null, 1), info)) throw new Error('Не удалось определить границы монитора.');
  return info;
}
function isWindowCovering(hwnd, area) {
  const a = nativeApi();
  if (!a.visible(hwnd) || a.minimized(hwnd) || (Number(a.style(hwnd, -20)) & 0x20)) return false;
  const buffer = Buffer.alloc(512);
  a.name(hwnd, buffer, 256);
  const name = buffer.toString('utf16le').split('\0')[0];
  if (['Progman', 'WorkerW', 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd'].includes(name)) return false;
  const cloaked = [0];
  if (a.attribute(hwnd, 14, cloaked, 4) === 0 && cloaked[0]) return false;
  const rect = [0, 0, 0, 0];
  if ((a.attribute(hwnd, 9, rect, 16) !== 0 || rect[2] <= rect[0] || rect[3] <= rect[1]) && !a.rect(hwnd, rect)) return false;
  return coversArea(rect, area);
}
function isDesktopCovered() {
  if (process.platform !== 'win32') return false;
  const a = nativeApi(), area = primaryMonitor().work;
  let covered = false;
  a.enumerate(hwnd => { if (isWindowCovering(hwnd, area)) { covered = true; return 0; } return 1; }, 0);
  return covered;
}
module.exports = { isDesktopCovered, isWindowCovering, primaryMonitor, coversArea };
