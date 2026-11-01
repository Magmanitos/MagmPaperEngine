let api;
function nativeApi() {
  if (api) return api;
  const koffi = require('koffi'), user = koffi.load('user32.dll'), dwm = koffi.load('dwmapi.dll');
  const rect = koffi.struct('CoverageRect', { left: 'int32_t', top: 'int32_t', right: 'int32_t', bottom: 'int32_t' });
  koffi.struct('CoverageMonitor', { size: 'uint32_t', monitor: rect, work: rect, flags: 'uint32_t' });
  koffi.proto('int32_t __stdcall CoverageEnum(void *hwnd, intptr_t param)');
  api = {
    address: hwnd => hwnd == null ? 0n : typeof hwnd === 'bigint' ? hwnd : koffi.address(hwnd),
    visible: user.func('int32_t __stdcall IsWindowVisible(void *hwnd)'),
    minimized: user.func('int32_t __stdcall IsIconic(void *hwnd)'),
    style: user.func('intptr_t __stdcall GetWindowLongPtrW(void *hwnd, int index)'),
    name: user.func('int32_t __stdcall GetClassNameW(void *hwnd, _Out_ uint16_t *name, int count)'),
    findChild: user.func('void * __stdcall FindWindowExW(void *parent, void *after, str16 className, str16 title)'),
    layered: user.func('int32_t __stdcall GetLayeredWindowAttributes(void *hwnd, _Out_ uint32_t *key, _Out_ uint8_t *alpha, _Out_ uint32_t *flags)'),
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
function createCoverageDetector(a) {
  function inspect(hwnd, area, wallpaperHandle) {
    if (wallpaperHandle != null && a.address(hwnd) === a.address(wallpaperHandle)) return 'ignore';
    if (!a.visible(hwnd) || a.minimized(hwnd)) return 'ignore';
    const cloaked = [0];
    if (a.attribute(hwnd, 14, cloaked, 4) === 0 && cloaked[0]) return 'ignore';
    const buffer = Buffer.alloc(512);
    a.name(hwnd, buffer, 256);
    const name = buffer.toString('utf16le').split('\0')[0];
    // EnumWindows follows Z order. Win+D can raise the desktop above windows
    // without minimizing them, so windows below its icon surface cannot cover it.
    if (['Progman', 'WorkerW'].includes(name)) {
      return a.findChild(hwnd, null, 'SHELLDLL_DefView', null) ? 'desktop' : 'ignore';
    }
    if (['Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'SHELLDLL_DefView'].includes(name)) return 'ignore';
    const style = Number(a.style(hwnd, -16)), extended = Number(a.style(hwnd, -20));
    // Child windows, click-through surfaces, tool windows and non-activating
    // overlays are not evidence that an application has hidden the desktop.
    if ((style & 0x40000000) || (extended & (0x20 | 0x80 | 0x08000000))) return 'ignore';
    if (extended & 0x00080000) {
      const key = [0], alpha = [0], flags = [0];
      // Per-pixel transparency (UpdateLayeredWindow), color keys and partial
      // alpha cannot guarantee an opaque rectangle, even with WS_VISIBLE set.
      if (!a.layered(hwnd, key, alpha, flags) || (flags[0] & 1) || !(flags[0] & 2) || alpha[0] !== 255) return 'ignore';
    }
    const bounds = [0, 0, 0, 0];
    if ((a.attribute(hwnd, 9, bounds, 16) !== 0 || bounds[2] <= bounds[0] || bounds[3] <= bounds[1]) && !a.rect(hwnd, bounds)) return 'ignore';
    return coversArea(bounds, area) ? 'cover' : 'ignore';
  }
  return {
    isWindowCovering: (hwnd, area, wallpaperHandle) => inspect(hwnd, area, wallpaperHandle) === 'cover',
    isDesktopCovered(area, wallpaperHandle) {
      let covered = false;
      a.enumerate(hwnd => {
        const kind = inspect(hwnd, area, wallpaperHandle);
        if (kind === 'cover') covered = true;
        return kind === 'ignore' ? 1 : 0;
      }, 0);
      return covered;
    }
  };
}
function isWindowCovering(hwnd, area, wallpaperHandle) {
  return createCoverageDetector(nativeApi()).isWindowCovering(hwnd, area, wallpaperHandle);
}
function isDesktopCovered(wallpaperHandle) {
  if (process.platform !== 'win32') return false;
  return createCoverageDetector(nativeApi()).isDesktopCovered(primaryMonitor().work, wallpaperHandle);
}
module.exports = { isDesktopCovered, isWindowCovering, primaryMonitor, coversArea, createCoverageDetector };
