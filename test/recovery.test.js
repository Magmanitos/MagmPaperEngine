const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRecoveryController } = require('../src/recovery');
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };
test('fullscreen resolution bursts coalesce, retry temporary desktop failure, then recover', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0, failures = 0;
  const recovery = createRecoveryController({ enabled: () => true, rebuild: async () => { if (++calls === 1) throw new Error('Explorer switching mode'); }, onError: () => failures++ });
  t.after(() => recovery.cancel());
  for (let i = 0; i < 5; i++) { recovery.request(); t.mock.timers.tick(100); }
  assert.equal(calls, 0); t.mock.timers.tick(750); await flush(); assert.equal(calls, 1);
  recovery.ensure(); t.mock.timers.tick(2000); await flush(); assert.equal(calls, 2);
  assert.equal(recovery.pending, false); assert.equal(failures, 0);
});
test('Stop, suspend or selecting another wallpaper cancels an in-flight rebuild and queued retries', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let enabled = true, current, finish, applied = 0;
  const recovery = createRecoveryController({ enabled: () => enabled, rebuild: async valid => {
    current = valid; await new Promise(resolve => { finish = resolve; }); if (valid()) applied++;
  }, onError: assert.fail });
  recovery.request(); t.mock.timers.tick(750); assert.equal(current(), true);
  recovery.cancel(); finish(); await flush(); assert.equal(applied, 0); assert.equal(current(), false);
  enabled = false; recovery.request(); t.mock.timers.tick(5000); assert.equal(recovery.pending, false);
});
test('repeated health probes cannot create an endless restart loop after persistent failure', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0, errors = 0;
  const recovery = createRecoveryController({ enabled: () => true, attempts: 2, rebuild: async () => { calls++; throw new Error('GPU unavailable'); }, onError: () => errors++ });
  recovery.ensure(); t.mock.timers.tick(750); await flush();
  recovery.ensure(); t.mock.timers.tick(2000); await flush();
  recovery.ensure(); t.mock.timers.tick(10000); await flush();
  assert.equal(calls, 2); assert.equal(errors, 1);
  recovery.request(); t.mock.timers.tick(750); await flush(); assert.equal(calls, 3); recovery.cancel();
});
