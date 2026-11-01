const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPlaybackController } = require('../src/playback');
const { DEFAULT_SETTINGS } = require('../src/settings');
const { coversArea } = require('../src/coverage');
test('coverage accepts maximized/fullscreen primary windows, excludes small windows and other monitors', () => {
  const area = { left: 0, top: 0, right: 1920, bottom: 1040 };
  assert.ok(coversArea([0,0,1920,1080], area));
  assert.ok(coversArea([0,0,1920,1040], area));
  assert.equal(coversArea([20,20,1500,900], area), false);
  assert.equal(coversArea([1920,0,3840,1080], area), false);
});
test('playback pauses and resumes for coverage, battery and lock; changes quality live without idle IPC', t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  let covered = false, battery = false, locked = false, checks = 0, stopped = false;
  const settings = { ...DEFAULT_SETTINGS }, messages = [], cursor = [];
  const controller = createPlaybackController({
    window: { isDestroyed: () => false, webContents: { isDestroyed: () => false, send: (channel, state) => messages.push([channel, state]) } },
    cursor: { setEnabled: state => cursor.push(state), stop: () => { stopped = true; } },
    getSettings: () => settings, isCovered: () => { checks++; return covered; }, isOnBattery: () => battery, isLocked: () => locked
  });
  const state = () => messages.at(-1)[1];
  assert.equal(state().paused, false);
  t.mock.timers.tick(1000); assert.equal(messages.length, 1);
  covered = true; t.mock.timers.tick(1000); assert.equal(state().reason, 'covered'); assert.equal(cursor.at(-1), false);
  covered = false; t.mock.timers.tick(1000); assert.equal(state().paused, false); assert.equal(cursor.at(-1), true);
  settings.fps = 30; settings.renderScale = 0.5; controller.refresh(); assert.equal(state().fps, 30); assert.equal(state().renderScale, 0.5);
  battery = true; controller.refresh(); assert.equal(state().paused, false);
  settings.pauseOnBattery = true; controller.refresh(); assert.equal(state().reason, 'battery');
  locked = true; controller.refresh(); assert.equal(state().reason, 'locked');
  locked = false; battery = false; covered = true; settings.pauseWhenCovered = false; controller.refresh(); assert.equal(state().paused, false);
  controller.stop(); const before = checks; t.mock.timers.tick(10000); assert.equal(checks, before); assert.ok(stopped);
});
