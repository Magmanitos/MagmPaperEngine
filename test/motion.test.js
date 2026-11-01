const { test } = require('node:test');
const assert = require('node:assert/strict');

test('frame clock keeps 60 FPS under RAF jitter and caps high-refresh displays', async () => {
  const { FrameClock } = await import('../src/ui/motion.mjs');
  for (const hz of [60, 120, 144]) {
    const clock = new FrameClock();
    let draws = 0;
    for (let i = 0; i < hz; i++) if (clock.advance(i * 1000 / hz + (i % 2 ? 0.15 : 0)) !== null) draws++;
    assert.ok(draws >= 59 && draws <= 61, `${hz} Hz produced ${draws} frames`);
    assert.notEqual(clock.advance(5000), null, 'recovers after suspension without replaying old frames');
    assert.equal(clock.advance(5001), null);
  }
});
test('mouse smoothing converges without overshoot and is independent of frame rate', async () => {
  const { MouseMotion } = await import('../src/ui/motion.mjs');
  const a = new MouseMotion(20), b = new MouseMotion(20);
  a.setTarget(1, 0); b.setTarget(1, 0);
  const first = [...a.advance(10)];
  assert.ok(first[0] > 0.5 && first[0] < 1 && first[1] > 0 && first[1] < 0.5);
  for (let i = 0; i < 9; i++) a.advance(10);
  for (let i = 0; i < 5; i++) b.advance(20);
  assert.ok(Math.abs(a.position[0] - b.position[0]) < 1e-10);
  assert.ok(a.position[0] > 0.99 && a.position[0] <= 1);
  a.setTarget(0, 1); const reversed = [...a.advance(10)];
  assert.ok(reversed[0] < 0.99 && reversed[0] > 0);
  a.setEnabled(false); a.setTarget(1, 1); a.advance(100);
  assert.deepEqual(a.position, [0.5, 0.5]);
  a.setEnabled(true); a.setTarget(1, 1); a.advance(10);
  assert.ok(a.position[0] > 0.5);
});
