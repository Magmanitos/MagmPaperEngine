// Uses real WebGL and the real Store; Win32 desktop integration remains a Windows check.
const { chromium } = require('playwright-core');
const { Store } = require('../src/store');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
(async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'magmpaper-ui-'));
  const store = new Store(dir);
  let startupEnabled = false, startupWriteFails = false;
  let mouseWriteFails = false;
  let settingsWriteFails = false;
  store.save({ name: 'Северное сияние', code: require('../src/example') });
  const root = path.resolve(__dirname, '../src/ui');
  const server = http.createServer((req, res) => {
    const file = path.resolve(root, '.' + (req.url === '/' ? '/index.html' : req.url));
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', /\.m?js$/.test(file) ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
    res.end(fs.readFileSync(file));
  });
  let browser;
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true,
      args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
    const page = await browser.newPage({ viewport: { width: 1160, height: 850 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.exposeFunction('testBridge', (method, arg) => {
      if (method === 'setSettings') {
        if (settingsWriteFails) throw new Error('Settings write failed');
        return store.setSettings(arg);
      }
      if (method === 'setMouseEnabled') {
        if (mouseWriteFails) throw new Error('Mouse setting write failed');
        return store.setMouseEnabled(arg);
      }
      if (method === 'startup') return { supported: true, enabled: startupEnabled };
      if (method === 'setStartup') {
        if (startupWriteFails) throw new Error('Startup write failed');
        startupEnabled = arg; return { supported: true, enabled: startupEnabled };
      }
      if (method === 'list') return { ...store.list(), runningId: store.data.activeId, platform: 'win32' };
      if (method === 'save') return store.save(arg);
      if (method === 'remove') return store.remove(arg);
      if (method === 'apply') return store.activate(arg);
      if (method === 'stop') return store.activate(null);
    });
    await page.addInitScript(() => {
      window.magm = Object.fromEntries(['list','save','remove','apply','stop','startup','setStartup','setMouseEnabled','setSettings'].map(method => [method, arg => window.testBridge(method, arg)]));
      window.magm.onStatus = () => {};
      window.magm.onPlayback = callback => { window.testPlayback = callback; };
      window.magm.onOpenSettings = callback => { window.testOpenSettings = callback; };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Предпросмотр готов'));
    assert.equal(await page.isVisible('#mouse-enabled'), false);
    await page.click('#open-settings');
    await page.uncheck('#mouse-enabled');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Реакция обоев на мышь выключена'));
    assert.equal(new Store(dir).list().settings.mouseEnabled, false);
    await page.reload();
    await page.click('#open-settings');
    await page.waitForFunction(() => !document.getElementById('mouse-enabled').disabled);
    assert.equal(await page.isChecked('#mouse-enabled'), false);
    await page.check('#mouse-enabled');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Реакция обоев на мышь включена'));
    mouseWriteFails = true; await page.click('#mouse-enabled');
    await page.waitForSelector('#status.error');
    assert.equal(await page.isChecked('#mouse-enabled'), true);
    mouseWriteFails = false;
    await page.check('#startup');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Автозапуск включён'));
    assert.equal(startupEnabled, true);
    await page.reload();
    await page.click('#open-settings');
    await page.waitForFunction(() => document.getElementById('startup').checked && !document.getElementById('startup').disabled);
    startupWriteFails = true;
    await page.click('#startup');
    await page.waitForSelector('#status.error');
    assert.equal(await page.isChecked('#startup'), true, 'failed writes restore the checkbox');
    startupWriteFails = false;
    await page.uncheck('#startup');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Автозапуск выключен'));
    assert.equal(startupEnabled, false);
    await page.selectOption('#fps', '30');
    await page.waitForFunction(() => !document.getElementById('fps').disabled);
    await page.selectOption('#render-scale', '0.5');
    await page.waitForFunction(() => !document.getElementById('render-scale').disabled);
    await page.uncheck('#pause-covered');
    await page.waitForFunction(() => !document.getElementById('pause-covered').disabled);
    await page.check('#pause-battery');
    await page.waitForFunction(() => !document.getElementById('pause-battery').disabled);
    assert.equal(new Store(dir).list().settings.renderScale, 0.5);
    await page.reload(); await page.click('#open-settings');
    assert.equal(await page.inputValue('#fps'), '30');
    assert.equal(await page.inputValue('#render-scale'), '0.5');
    assert.equal(await page.isChecked('#pause-covered'), false);
    assert.equal(await page.isChecked('#pause-battery'), true);
    settingsWriteFails = true;
    await page.selectOption('#fps', '15');
    await page.waitForSelector('#settings-status.error');
    assert.equal(await page.inputValue('#fps'), '30');
    settingsWriteFails = false;
    await page.screenshot({ path: '/tmp/magmpaper-settings.png' });
    await page.keyboard.press('Escape');
    assert.equal(await page.isVisible('#settings-dialog'), false);
    await page.evaluate(() => window.testOpenSettings());
    await page.click('#close-settings');
    await page.evaluate(() => window.testPlayback({ paused: true, reason: 'covered' }));
    assert.ok((await page.textContent('#playback-state')).includes('рабочий стол закрыт'));
    await page.evaluate(() => window.testPlayback(null));
    assert.equal(await page.isVisible('#playback-state'), false);
    // Check actual color output and previous-frame feedback, not merely successful compilation.
    const rendered = await page.evaluate(async () => {
      const { ShaderPlayer } = await import('./shader.js');
      const canvas = document.createElement('canvas'); canvas.style.width = '64px'; canvas.style.height = '64px'; document.body.append(canvas);
      const player = new ShaderPlayer(canvas);
      player.load('precision mediump float; uniform sampler2D backbuffer; uniform vec2 resolution; void main(){gl_FragColor=texture2D(backbuffer,gl_FragCoord.xy/resolution)+vec4(0.1,0.0,0.0,1.0);}');
      cancelAnimationFrame(player.frame);
      const red = [];
      for (let i = 1; i <= 3; i++) {
        player.tick(performance.now() + i * 100); cancelAnimationFrame(player.frame);
        const pixels = new Uint8Array(4); player.gl.readPixels(32, 32, 1, 1, player.gl.RGBA, player.gl.UNSIGNED_BYTE, pixels); red.push(pixels[0]);
      }
      let rejected = false;
      try { player.load('not valid GLSL'); } catch { rejected = true; }
      player.load('#extension GL_OES_standard_derivatives : enable\nprecision mediump float; void main(){gl_FragColor=vec4(dFdx(gl_FragCoord.x),0.0,0.0,1.0);}');
      player.load('precision mediump float; uniform vec2 mouse; void main(){gl_FragColor=vec4(mouse,0.0,1.0);}');
      cancelAnimationFrame(player.frame);
      const colors = [];
      for (const position of [[0.25,0.75], [0.8,0.2]]) {
        player.setMouse(...position);
        player.tick(performance.now() + colors.length * 100 + 100); cancelAnimationFrame(player.frame);
        const pixels = new Uint8Array(4); player.gl.readPixels(32,32,1,1,player.gl.RGBA,player.gl.UNSIGNED_BYTE,pixels);
        colors.push(Array.from(pixels));
      }
      const directBuffers = player.buffers.length;
      player.dispose();
      const smooth = new ShaderPlayer(canvas, () => {}, { smoothMouse: true });
      smooth.load('precision mediump float; uniform vec2 mouse; void main(){gl_FragColor=vec4(mouse,0.0,1.0);}');
      cancelAnimationFrame(smooth.frame);
      smooth.setMouse(1, 0);
      const now = performance.now(), transition = [];
      for (let i = 0; i < 5; i++) {
        smooth.tick(now + i * 17); cancelAnimationFrame(smooth.frame);
        const pixels = new Uint8Array(4); smooth.gl.readPixels(32,32,1,1,smooth.gl.RGBA,smooth.gl.UNSIGNED_BYTE,pixels);
        transition.push(pixels[0]);
      }
      smooth.setMouseEnabled(false); smooth.setMouse(0,1);
      smooth.tick(now + 100); cancelAnimationFrame(smooth.frame);
      const disabled = new Uint8Array(4); smooth.gl.readPixels(32,32,1,1,smooth.gl.RGBA,smooth.gl.UNSIGNED_BYTE,disabled);
      smooth.dispose();
      const pausedPlayer = new ShaderPlayer(canvas);
      pausedPlayer.configure({ fps: 30, renderScale: 0.5 });
      pausedPlayer.load('precision mediump float; uniform float time; void main(){gl_FragColor=vec4(fract(time),0.0,0.0,1.0);}');
      let draws = 0;
      const draw = pausedPlayer.draw.bind(pausedPlayer);
      pausedPlayer.draw = program => { draws++; draw(program); };
      await new Promise(resolve => setTimeout(resolve, 100));
      const originalProgram = pausedPlayer.shader;
      const width = pausedPlayer.width;
      pausedPlayer.paused = true;
      const beforePause = draws, timeBefore = pausedPlayer.gl.getUniform(pausedPlayer.shader, pausedPlayer.uniforms.time);
      await new Promise(resolve => setTimeout(resolve, 300));
      const pause = { before: beforePause, after: draws, frame: pausedPlayer.frame };
      pausedPlayer.configure({ fps: 15, renderScale: 1 });
      pausedPlayer.paused = false;
      await new Promise(resolve => setTimeout(resolve, 100));
      const resume = { draws, sameProgram: originalProgram === pausedPlayer.shader, width: pausedPlayer.width, timeDelta: pausedPlayer.gl.getUniform(pausedPlayer.shader, pausedPlayer.uniforms.time) - timeBefore };
      pausedPlayer.dispose(); canvas.remove(); return { red, rejected, colors, transition, disabled: Array.from(disabled), directBuffers, width, pause, resume };
    });
    assert.ok(rendered.red[0] > 20 && rendered.red[1] > rendered.red[0] && rendered.red[2] > rendered.red[1], JSON.stringify(rendered));
    assert.equal(rendered.rejected, true);
    assert.equal(rendered.directBuffers, 0, 'ordinary shaders avoid backbuffer allocations and copying');
    assert.ok(rendered.pause.before > 0);
    assert.equal(rendered.pause.after, rendered.pause.before, 'no GPU draws while paused');
    assert.equal(rendered.pause.frame, 0, 'no RAF loop while paused');
    assert.ok(rendered.resume.draws > rendered.pause.after && rendered.resume.sameProgram);
    assert.equal(rendered.resume.width, rendered.width * 2);
    assert.ok(rendered.resume.timeDelta >= 0 && rendered.resume.timeDelta < 0.25, 'pause duration is excluded from shader time');
    assert.ok(rendered.transition[0] > 128 && rendered.transition[0] < 250);
    assert.ok(rendered.transition[4] > rendered.transition[0] && rendered.transition[4] >= 250);
    assert.ok(Math.abs(rendered.disabled[0] - 128) <= 1 && Math.abs(rendered.disabled[1] - 128) <= 1);
    for (const [index, expected] of [[0,[64,191]], [1,[204,51]]]) {
      assert.ok(Math.abs(rendered.colors[index][0] - expected[0]) <= 1);
      assert.ok(Math.abs(rendered.colors[index][1] - expected[1]) <= 1);
    }
    await page.click('#add'); await page.fill('#name', 'Мой шейдер');
    await page.fill('#code', 'broken shader'); await page.click('#save');
    await page.waitForSelector('#status.error'); assert.equal(store.list().items.length, 1);
    await page.fill('#code', require('../src/example')); await page.click('#save');
    await page.waitForFunction(() => document.getElementById('count').textContent === '2');
    assert.equal(new Store(dir).list().items.length, 2);
    await page.click('#apply'); await page.waitForFunction(() => document.getElementById('status').textContent.includes('Обои установлены'));
    assert.ok(store.data.activeId);
    await page.click('#stop'); await page.waitForFunction(() => document.getElementById('status').textContent.includes('Обои остановлены'));
    assert.equal(store.data.activeId, null);
    await page.click('#edit'); await page.fill('#name', 'Новое имя'); await page.click('#save');
    await page.waitForFunction(() => document.getElementById('title').textContent === 'Новое имя');
    await page.reload(); await page.waitForSelector('#list button');
    assert.ok((await page.locator('#list').textContent()).includes('Новое имя'));
    await page.getByRole('button', { name: 'Новое имя', exact: true }).click();
    await page.click('#delete'); await page.click('#confirm');
    await page.waitForFunction(() => document.getElementById('count').textContent === '1');
    await page.screenshot({ path: path.join(dir, 'preview.png') });
    assert.deepEqual(errors, []);
    console.log('PASS: real WebGL pixels, backbuffer feedback, invalid GLSL, UI create/edit/reload/delete, activation state.');
    console.log('Windows WorkerW is not exercised by this browser test.');
  } finally { if (browser) await browser.close(); server.close(); fs.rmSync(dir, { recursive: true, force: true }); }
})().catch(error => { console.error(error); process.exitCode = 1; });
