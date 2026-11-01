function createPlaybackController({ window, cursor, getSettings, isCovered, isOnBattery, isLocked, onChange = () => {}, onError = () => {} }) {
  let last = '', timer, errorReported = false;
  const stop = () => { clearInterval(timer); timer = null; cursor.stop(); };
  const refresh = () => {
    if (window.isDestroyed() || window.webContents.isDestroyed()) { stop(); return; }
    const settings = getSettings();
    let reason = null;
    try {
      reason = isLocked() ? 'locked' : settings.pauseOnBattery && isOnBattery() ? 'battery'
        : settings.pauseWhenCovered && isCovered() ? 'covered' : null;
      errorReported = false;
    } catch (error) { if (!errorReported) onError(error); errorReported = true; }
    const state = { paused: reason !== null, reason, fps: settings.fps, renderScale: settings.renderScale };
    const key = JSON.stringify(state);
    if (key !== last) { window.webContents.send('wallpaper:runtime', state); onChange(state); last = key; }
    cursor.setEnabled(settings.mouseEnabled && !state.paused);
  };
  refresh();
  timer = setInterval(refresh, 1000);
  return { refresh, stop };
}
module.exports = { createPlaybackController };
