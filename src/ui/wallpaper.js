import { ShaderPlayer } from './shader.js';
try {
  const player = new ShaderPlayer(document.getElementById('canvas'), (error, recoverable) => window.wallpaper.result(error, recoverable), { smoothMouse: true, onContextState: lost => window.wallpaper.context(lost) });
  const unsubscribe = window.wallpaper.onMouse(position => player.setMouse(...position));
  const unsubscribeEnabled = window.wallpaper.onMouseEnabled(enabled => player.setMouseEnabled(enabled));
  const unsubscribeRuntime = window.wallpaper.onRuntime(state => {
    player.configure(state);
    player.paused = state.paused;
  });
  window.addEventListener('beforeunload', () => { unsubscribe(); unsubscribeEnabled(); unsubscribeRuntime(); player.dispose(); });
  player.load(await window.wallpaper.source());
  // Render one frame before reporting readiness; allocation failures must reach main.
  cancelAnimationFrame(player.frame);
  player.tick(performance.now() + 34);
  window.wallpaper.result(null);
} catch (error) { window.wallpaper.result(error.message); }
