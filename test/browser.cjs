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
  store.save({ name: 'Северное сияние', code: require('../src/example') });
  const root = path.resolve(__dirname, '../src/ui');
  const server = http.createServer((req, res) => {
    const file = path.resolve(root, '.' + (req.url === '/' ? '/index.html' : req.url));
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
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
      if (method === 'list') return { ...store.list(), runningId: store.data.activeId, platform: 'win32' };
      if (method === 'save') return store.save(arg);
      if (method === 'remove') return store.remove(arg);
      if (method === 'apply') return store.activate(arg);
      if (method === 'stop') return store.activate(null);
    });
    await page.addInitScript(() => { window.magm = Object.fromEntries(['list','save','remove','apply','stop'].map(method => [method, arg => window.testBridge(method, arg)])); window.magm.onStatus = () => {}; });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('Предпросмотр готов'));
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
      player.dispose(); canvas.remove(); return { red, rejected };
    });
    assert.ok(rendered.red[0] > 20 && rendered.red[1] > rendered.red[0] && rendered.red[2] > rendered.red[1], JSON.stringify(rendered));
    assert.equal(rendered.rejected, true);
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
