// Electron returns cursor positions and display bounds in the same DIP space.
// Normalize before IPC; render resolution / Windows scaling do not affect mouse.
function normalizeCursor(point, bounds) {
  const clamp = value => Math.max(0, Math.min(1, value));
  return [clamp((point.x - bounds.x) / bounds.width), clamp(1 - (point.y - bounds.y) / bounds.height)];
}

function startCursorTracking(window, screen, bounds) {
  let last, timer;
  const stop = () => { clearInterval(timer); timer = null; };
  const update = () => {
    if (window.isDestroyed() || window.webContents.isDestroyed()) { stop(); return; }
    const position = normalizeCursor(screen.getCursorScreenPoint(), bounds);
    if (!last || position[0] !== last[0] || position[1] !== last[1]) {
      window.webContents.send('wallpaper:mouse', position);
      last = position;
    }
  };
  // The wallpaper stays click-through: no mouse hooks or input interception.
  update();
  timer = setInterval(update, 8); // 125 Hz samples, only changed positions cross IPC.
  return stop;
}
function createCursorController(window, screen, bounds) {
  let stopPolling, current;
  return {
    setEnabled(enabled) {
      if (enabled === current) return;
      stopPolling?.(); stopPolling = null;
      current = enabled;
      if (window.isDestroyed() || window.webContents.isDestroyed()) return;
      window.webContents.send('wallpaper:mouse-enabled', enabled);
      if (enabled) stopPolling = startCursorTracking(window, screen, bounds);
    },
    stop() { stopPolling?.(); stopPolling = null; current = undefined; }
  };
}
module.exports = { normalizeCursor, startCursorTracking, createCursorController };
