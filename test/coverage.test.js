const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createCoverageDetector } = require('../src/coverage');
const { createPlaybackController } = require('../src/playback');
const { DEFAULT_SETTINGS } = require('../src/settings');
const area = { left: 0, top: 0, right: 1920, bottom: 1040 };
function fixture() {
  let next = 0;
  const windows = [];
  const add = (properties = {}) => {
    const window = { id: ++next, visible: true, minimized: false, cloaked: false, name: 'AppWindow',
      style: 0x90000000, extended: 0, bounds: [0, 0, 1920, 1080], ...properties };
    windows.push(window); return window;
  };
  const detector = createCoverageDetector({
    foreground: () => windows.find(w => w.foreground), root: w => w,
    address: w => w.id, visible: w => w.visible, minimized: w => w.minimized,
    style: (w, index) => index === -16 ? w.style : w.extended,
    name: (w, buffer) => buffer.write(w.name + '\0', 'utf16le'),
    findChild: w => w.desktop ? {} : null,
    layered: (w, key, alpha, flags) => { alpha[0] = w.alpha ?? 255; flags[0] = w.flags ?? 2; return !w.perPixel; },
    attribute: (w, attr, output) => {
      if (attr === 14) output[0] = Number(w.cloaked);
      else if (w.noDwmBounds) return -1;
      else output.splice(0, 4, ...w.bounds);
      return 0;
    },
    rect: (w, output) => { output.splice(0, 4, ...w.bounds); return 1; },
    enumerate: callback => { for (const w of windows) if (!callback(w)) break; }
  });
  return { add, windows, covered: wallpaper => detector.isDesktopCovered(area, wallpaper) };
}
test('full-screen shell helpers, transparent overlays and wallpaper itself do not pause a visible desktop', () => {
  const { add, covered } = fixture();
  const wallpaper = add(); // Explicit exclusion survives changes in wallpaper styles.
  add({ extended: 0x80 }); // Tool window (e.g. shell input surface).
  add({ extended: 0x08000000 }); // No-activate overlay.
  add({ extended: 0x20 });
  add({ style: 0x50000000 }); // Child window.
  add({ extended: 0x80000, alpha: 0 });
  add({ extended: 0x80000, alpha: 180 });
  add({ extended: 0x80000, flags: 1 });
  add({ extended: 0x80000, perPixel: true });
  add({ name: 'Shell_TrayWnd' });
  add({ name: 'Shell_SecondaryTrayWnd' });
  add({ name: 'WorkerW' });
  add({ name: 'Progman', desktop: true });
  assert.equal(covered(wallpaper), false);
});
test('small, minimized, hidden and cloaked windows leave desktop running; real full-screen cover pauses', () => {
  const { add, covered } = fixture();
  add({ bounds: [20, 20, 1500, 900] });
  add({ minimized: true }); add({ visible: false }); add({ cloaked: true });
  add({ bounds: [1920, 0, 3840, 1080] });
  const application = add({ visible: false });
  add({ name: 'WorkerW', desktop: true });
  assert.equal(covered(), false);
  application.visible = true;
  assert.equal(covered(), true, 'small foreground window does not hide a full-screen window behind it');
  application.bounds = [0, 0, 1920, 1040]; application.noDwmBounds = true;
  assert.equal(covered(), true, 'maximized work area with GetWindowRect fallback');
  application.extended = 0x80000; application.alpha = 255;
  assert.equal(covered(), true, 'fully opaque layered application still covers the desktop');
});
test('Show Desktop raised icon surface stops search before windows left behind it', () => {
  const { add, windows, covered } = fixture();
  const desktop = add({ name: 'WorkerW', desktop: true });
  const application = add();
  assert.equal(covered(), false);
  windows.splice(0, 2, application, desktop);
  assert.equal(covered(), true);
});
test('autopause resumes after minimize, resize and Show Desktop while full-screen overlays remain', t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const { add, windows, covered } = fixture();
  add({ extended: 0x80 });
  const application = add({ bounds: [10, 10, 800, 600] });
  const desktop = add({ name: 'Progman', desktop: true });
  const states = [], cursor = [];
  const controller = createPlaybackController({
    window: { isDestroyed: () => false, webContents: { isDestroyed: () => false, send: (channel, state) => states.push(state) } },
    cursor: { setEnabled: enabled => cursor.push(enabled), stop() {} },
    getSettings: () => DEFAULT_SETTINGS, isCovered: covered, isOnBattery: () => false, isLocked: () => false
  });
  t.after(() => controller.stop());
  const check = paused => { t.mock.timers.tick(1000); assert.equal(states.at(-1).paused, paused); assert.equal(cursor.at(-1), !paused); };
  check(false);
  application.bounds = [0, 0, 1920, 1080]; check(true);
  application.minimized = true; check(false);
  application.minimized = false; check(true);
  application.bounds = [100, 100, 1400, 900]; check(false);
  application.bounds = [0, 0, 1920, 1080]; check(true);
  windows.splice(windows.indexOf(desktop), 1); windows.unshift(desktop); check(false);
});

test('Win+D desktop focus overrides an exclusive game retaining stale visible/fullscreen bounds', () => {
  const { add, covered } = fixture();
  const game = add({ foreground: true });
  const desktop = add({ name: 'Progman', desktop: true });
  assert.equal(covered(), true);
  game.foreground = false; desktop.foreground = true;
  assert.equal(covered(), false);
  desktop.foreground = false; game.foreground = true;
  assert.equal(covered(), true);
});
