const { test } = require('node:test');
const assert = require('node:assert/strict');
const { normalizeCursor, startCursorTracking } = require('../src/cursor');

test('cursor coordinates match GLSL origin, monitor offset and DPI scaling', () => {
  const bounds = { x: -1280, y: -100, width: 1280, height: 720 }; // 1920x1080 at 150%, left monitor
  assert.deepEqual(normalizeCursor({ x: -1280, y: -100 }, bounds), [0, 1]);
  assert.deepEqual(normalizeCursor({ x: 0, y: 620 }, bounds), [1, 0]);
  assert.deepEqual(normalizeCursor({ x: -640, y: 260 }, bounds), [0.5, 0.5]);
  assert.deepEqual(normalizeCursor({ x: -960, y: 440 }, bounds), [0.25, 0.25]);
  assert.deepEqual(normalizeCursor({ x: 1000, y: -300 }, bounds), [1, 1]);
});
test('cursor tracking sends initial and changed positions, stops when wallpaper closes', t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const sent = [];
  let point = { x: 50, y: 50 }, destroyed = false, reads = 0;
  const window = { isDestroyed: () => destroyed, webContents: { isDestroyed: () => false, send: (...args) => sent.push(args) } };
  const screen = { getCursorScreenPoint: () => { reads++; return point; } };
  const stop = startCursorTracking(window, screen, { x: 0, y: 0, width: 100, height: 100 });
  assert.deepEqual(sent, [['wallpaper:mouse', [0.5, 0.5]]]);
  t.mock.timers.tick(40); assert.equal(sent.length, 1);
  point = { x: 25, y: 75 };
  t.mock.timers.tick(40); assert.deepEqual(sent[1], ['wallpaper:mouse', [0.25, 0.25]]);
  destroyed = true; t.mock.timers.tick(40);
  const before = reads; t.mock.timers.tick(1000); assert.equal(reads, before);
  stop();
});
test('stopping or replacing wallpaper stops cursor polling', t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  let reads = 0;
  const window = { isDestroyed: () => false, webContents: { isDestroyed: () => false, send: () => {} } };
  const stop = startCursorTracking(window, { getCursorScreenPoint: () => { reads++; return { x: 0, y: 0 }; } }, { x: 0, y: 0, width: 100, height: 100 });
  stop(); stop(); t.mock.timers.tick(1000);
  assert.equal(reads, 1);
});
