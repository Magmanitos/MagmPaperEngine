const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createStartup, restoreWithRetry, STARTUP_ID } = require('../src/startup');

function fakeApp(isPackaged) {
  let saved;
  return {
    isPackaged,
    getAppPath: () => 'C:\\Users\\Example Name\\MagmPaperEngine',
    setLoginItemSettings: value => { saved = value; },
    getLoginItemSettings: query => ({
      openAtLogin: !!saved?.openAtLogin && JSON.stringify(saved.args) === JSON.stringify(query.args) && saved.path === query.path,
      launchItems: saved?.openAtLogin ? [{ name: STARTUP_ID, scope: 'user', enabled: true }] : []
    }),
    saved: () => saved
  };
}
for (const packaged of [false, true]) {
  test(`autostart enable/disable and readback for ${packaged ? 'packaged app' : 'development path with spaces'}`, () => {
    const app = fakeApp(packaged), startup = createStartup(app, 'win32', 'C:\\Program Files\\MagmPaper\\app.exe');
    assert.equal(startup.get().enabled, false);
    assert.equal(startup.set(true).enabled, true);
    assert.deepEqual(app.saved().args, packaged ? ['--autostart'] : [app.getAppPath(), '--autostart']);
    assert.equal(startup.set(false).enabled, false);
    assert.throws(() => startup.set('true'));
  });
}
test('unsupported platforms and Windows startup policy failures are reported', () => {
  const app = fakeApp(true);
  assert.deepEqual(createStartup(app, 'linux').get(), { supported: false, enabled: false });
  assert.throws(() => createStartup(app, 'linux').set(true));
  app.setLoginItemSettings = () => {};
  assert.throws(() => createStartup(app, 'win32').set(true), /Не удалось/);
});
test('startup waits for Explorer readiness then applies wallpaper', async () => {
  let attempts = 0, waits = 0;
  await restoreWithRetry({ shouldContinue: () => true, wait: async () => { waits++; }, attempt: async () => {
    if (++attempts < 3) throw Object.assign(new Error('Explorer starting'), { code: 'DESKTOP_NOT_READY' });
  } });
  assert.equal(attempts, 3); assert.equal(waits, 2);
});
test('user stopping or changing wallpaper cancels a pending startup retry', async () => {
  let allowed = true, attempts = 0;
  await restoreWithRetry({ shouldContinue: () => allowed, wait: async () => { allowed = false; }, attempt: async () => {
    attempts++; throw Object.assign(new Error('not ready'), { code: 'DESKTOP_NOT_READY' });
  } });
  assert.equal(attempts, 1);
});
test('startup retry is bounded and shader errors fail immediately', async () => {
  let attempts = 0;
  await assert.rejects(restoreWithRetry({ shouldContinue: () => true, attempts: 2, wait: async () => {}, attempt: async () => {
    attempts++; throw Object.assign(new Error('not ready'), { code: 'DESKTOP_NOT_READY' });
  } }), /not ready/);
  assert.equal(attempts, 2);
  await assert.rejects(restoreWithRetry({ shouldContinue: () => true, wait: () => assert.fail('must not retry shader errors'), attempt: async () => { throw new Error('GLSL'); } }), /GLSL/);
});
