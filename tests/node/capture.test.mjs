import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { availablePort, fixture, pixel, pngs, remote, run } from './helpers/fixtures.mjs';

test('fresh build failure preserves saved PNGs without silently reusing them', t => {
  const root = fixture(t, { name: 'Example', build: 'exit 23' });
  const captures = path.join(root, 'docs/assets/product-captures/captures');
  mkdirSync(captures, { recursive: true });
  for (const theme of ['light', 'dark']) {
    writeFileSync(path.join(captures, `overview-${theme}.png`), 'previous');
  }

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /exited 23/);
  assert.equal(readFileSync(path.join(captures, 'overview-light.png'), 'utf8'), 'previous');
  assert(!existsSync(path.join(captures, '../manifest.json')));
});

test('Chromium creates exactly four fixed-density PNGs and explicit reuse preserves raw bytes', t => {
  const root = fixture(t, {
    name: 'Example',
    appearance: { width: 400, height: 300 },
    capture: { type: 'image', images: { light: 'light.png', dark: 'dark.png' } },
  });
  for (const theme of ['light', 'dark']) {
    writeFileSync(path.join(root, `${theme}.png`), pixel);
  }

  const first = run(root);

  assert.equal(first.status, 0, first.stderr);
  const output = path.join(root, 'docs/assets/product-captures');
  assert.deepEqual(pngs(output), [
    'captures/overview-dark.png', 'captures/overview-light.png', 'hero-dark.png', 'hero.png',
  ]);
  const hero = readFileSync(path.join(output, 'hero.png'));
  assert.equal(hero.readUInt32BE(16), 400);
  assert.equal(hero.readUInt32BE(20), 300);
  assert.deepEqual(readFileSync(path.join(output, 'captures/overview-light.png')), pixel);

  rmSync(path.join(root, 'light.png'));
  rmSync(path.join(root, 'dark.png'));
  const reused = run(root, ['--reuse-screenshots']);

  assert.equal(reused.status, 0, reused.stderr);
  assert.deepEqual(readFileSync(path.join(output, 'captures/overview-light.png')), pixel);
});

test('source-free rendering creates only the finished light and dark pair', t => {
  const root = fixture(t, {
    name: 'Example', capture: null, appearance: { width: 400, height: 300 },
  });

  const result = run(root);

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(pngs(path.join(root, 'docs/assets/product-captures')), ['hero-dark.png', 'hero.png']);
});

test('a local web host captures both theme storage states at the configured viewport', async t => {
  const root = fixture(t, {
    name: 'Web',
    appearance: { width: 400, height: 300 },
    capture: {
      viewport: { width: 320, height: 240 },
      storage: { theme: '$theme' },
      ready: '#ready',
      waitFor: '() => document.documentElement.dataset.theme === localStorage.theme',
    },
    server: { directory: '.', port: await availablePort() },
  });
  writeFileSync(path.join(root, 'index.html'), `
    <html><body><div id="ready">Ready</div><script>
      document.documentElement.dataset.theme = localStorage.theme;
      document.body.style.background = localStorage.theme === "dark" ? "black" : "white";
    </script></body></html>
  `);

  const result = run(root);

  assert.equal(result.status, 0, result.stderr);
  const output = path.join(root, 'docs/assets/product-captures');
  for (const theme of ['light', 'dark']) {
    const image = readFileSync(path.join(output, `captures/overview-${theme}.png`));
    assert.equal(image.readUInt32BE(16), 320);
    assert.equal(image.readUInt32BE(20), 240);
  }
  assert.notDeepEqual(readFileSync(path.join(output, 'captures/overview-light.png')),
    readFileSync(path.join(output, 'captures/overview-dark.png')));
});

test('theme storage replaces string placeholders and preserves selected object values', async t => {
  const url = await remote(t, `
    <body><script>
      document.body.style.background = localStorage.mode.startsWith("night") ? "black" : "white";
    </script></body>
  `);
  const root = fixture(t, {
    name: 'Theme storage',
    appearance: { width: 400, height: 300 },
    capture: {
      url,
      viewport: { width: 160, height: 120 },
      storage: {
        theme: 'prefix-$theme-$theme',
        mode: { light: 'day-$theme', dark: 'night-$theme' },
        literal: 'unchanged-$Theme',
      },
      waitFor: `() => {
        const theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        return localStorage.theme === 'prefix-' + theme + '-' + theme
          && localStorage.mode === (theme === 'dark' ? 'night-$theme' : 'day-$theme')
          && localStorage.literal === 'unchanged-$Theme';
      }`,
    },
  });

  const result = run(root);

  assert.equal(result.status, 0, result.stderr);
  const output = path.join(root, 'docs/assets/product-captures');
  assert.equal(pngs(output).length, 4);
  assert.notDeepEqual(readFileSync(path.join(output, 'captures/overview-light.png')),
    readFileSync(path.join(output, 'captures/overview-dark.png')));
});

test('an absolute capture URL needs no local server and actions can reveal readiness', async t => {
  const url = await remote(t, `
    <button onclick="document.querySelector('#ready').hidden=false">Open</button>
    <div id="ready" hidden>Ready</div>
  `);
  const root = fixture(t, {
    name: 'Remote',
    appearance: { width: 400, height: 300 },
    capture: {
      url: `${url}/scene?theme=yes`,
      viewport: { width: 320, height: 240 },
      actions: [{ type: 'click', selector: 'button' }, { type: 'scroll', y: 0 }],
      ready: '#ready',
    },
  });

  const result = run(root);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(pngs(path.join(root, 'docs/assets/product-captures')).length, 4);
});

test('a preview replaces the configured origin, preserves path and query, and skips the build', async t => {
  const url = await remote(t, `
    <body><script>
      if (location.pathname !== "/scene" || location.search !== "?x=1") throw Error("Wrong URL");
      document.body.id = "ready";
    </script>Ready</body>
  `);
  const root = fixture(t, {
    name: 'Preview',
    appearance: { width: 400, height: 300 },
    capture: {
      url: 'https://original.invalid/scene?x=1',
      ready: '#ready',
      waitFor: '() => location.pathname === "/scene" && location.search === "?x=1"',
    },
    build: 'touch should-not-build; exit 99',
  });

  const result = run(root, ['--url', url]);

  assert.equal(result.status, 0, result.stderr);
  assert(!existsSync(path.join(root, 'should-not-build')));
});

test('a visible broken image rejects fresh capture and preserves saved artwork', async t => {
  const url = await remote(t, '<img src="/missing.png" width="100" height="100">');
  const root = fixture(t, { name: 'Broken', capture: { url }, appearance: { width: 400, height: 300 } });
  const output = path.join(root, 'docs/assets/product-captures');
  mkdirSync(output, { recursive: true });
  writeFileSync(path.join(output, 'hero.png'), 'old');

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert.equal(readFileSync(path.join(output, 'hero.png'), 'utf8'), 'old');
  assert(!existsSync(path.join(output, 'manifest.json')));
});

test('an offscreen lazy broken image does not prevent viewport capture', async t => {
  const url = await remote(t, `
    <p>Visible scene</p>
    <img loading="lazy" src="/missing.png" width="100" height="100" style="position:absolute;top:5000px">
  `);
  const root = fixture(t, { name: 'Lazy', capture: { url }, appearance: { width: 400, height: 300 } });

  const result = run(root);

  assert.equal(result.status, 0, result.stderr);
});

test('switching to source-free capture removes only the two owned raw PNGs', t => {
  const config = {
    name: 'Transition',
    appearance: { width: 400, height: 300 },
    capture: { type: 'image', images: { light: 'light.png', dark: 'dark.png' } },
  };
  const root = fixture(t, config);
  for (const theme of ['light', 'dark']) writeFileSync(path.join(root, `${theme}.png`), pixel);
  const first = run(root);
  assert.equal(first.status, 0, first.stderr);
  const output = path.join(root, 'docs/assets/product-captures');
  writeFileSync(path.join(output, 'captures/user-owned.txt'), 'preserve');
  config.capture = null;
  writeFileSync(path.join(root, 'product-captures.json'), JSON.stringify(config));

  const second = run(root);

  assert.equal(second.status, 0, second.stderr);
  assert.deepEqual(pngs(output), ['hero-dark.png', 'hero.png']);
  assert.equal(readFileSync(path.join(output, 'captures/user-owned.txt'), 'utf8'), 'preserve');
});
