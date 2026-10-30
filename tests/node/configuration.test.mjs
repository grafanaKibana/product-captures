import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fixture, run } from './helpers/fixtures.mjs';

const invalidConfigurations = {
  unknown: { name: 'Example', density: 2 },
  traversal: { name: 'Example', output: '../escape' },
  root: { name: 'Example', output: '.' },
  badlayout: { name: 'Example', appearance: { layout: 'matrix' } },
  zero: { name: 'Example', appearance: { width: 0 } },
  nullappearance: { name: 'Example', appearance: null },
  badcapture: { name: 'Example', capture: { type: 'fallback' } },
  badstorage: { name: 'Example', capture: { storage: { theme: { light: 'light' } } } },
  badaction: { name: 'Example', capture: { actions: [{ type: 'eval', selector: 'body' }] } },
  badbuild: { name: 'Example', build: { command: 'touch no', extra: true } },
  badserver: { name: 'Example', server: { command: 'echo ok' } },
  badcolor: { name: 'Example', appearance: { background: 'url(bad)' } },
  badzoom: { name: 'Example', appearance: { imageZoom: 0 } },
  badfade: { name: 'Example', appearance: { fadeStart: 0.8, fadeEnd: 0.2 } },
  badversion: { name: 'Example', capture: { type: 'obsidian', appVersion: 'latest' } },
  internalkey: { name: 'Example', root: '/tmp' },
  badurl: { name: 'Example', capture: null },
};

for (const [name, config] of Object.entries(invalidConfigurations)) {
  test(`rejects ${name} before consumer writes`, t => {
    const root = fixture(t, config);
    const before = readdirSync(root);
    const args = ['--dry-run'];
    if (name === 'badurl') args.push('--url', 'file:///etc/passwd');

    const result = run(root, args);

    assert.notEqual(result.status, 0);
    assert.deepEqual(readdirSync(root), before);
  });
}

const firstErrors = [
  {
    name: 'required name before appearance',
    config: { name: '', appearance: { layout: 'matrix', width: 0 }, capture: { url: 'file:///invalid' } },
    error: 'name is required',
  },
  {
    name: 'nullable output before required name',
    config: { name: '', output: null, appearance: { layout: 'top', width: 0 } },
    error: 'subtitle and output cannot be null',
  },
  {
    name: 'layout before dimensions',
    config: { appearance: { layout: 'matrix', width: 0 }, capture: { url: 'file:///invalid' } },
    error: 'Unknown appearance.layout',
  },
  {
    name: 'dimensions before colors',
    config: { appearance: { width: 0, background: 'invalid' }, capture: { url: 'file:///invalid' } },
    error: 'Invalid artwork dimensions',
  },
  {
    name: 'colors before image settings',
    config: { appearance: { background: 'invalid', imageZoom: 0 }, capture: { url: 'file:///invalid' } },
    error: 'Appearance colors must be hexadecimal CSS colors',
  },
  {
    name: 'effective fade default before capture',
    config: { appearance: { fadeEnd: 0.2 }, capture: { url: 'file:///invalid', type: 'unknown' } },
    error: 'fadeStart must not exceed fadeEnd',
  },
  {
    name: 'capture URL before provider',
    config: { capture: { url: 'file:///invalid', type: 'unknown', viewport: { width: 0 } } },
    error: 'capture.url must be relative or HTTP',
  },
  {
    name: 'provider before viewport',
    config: { capture: { type: 'unknown', viewport: { width: 0 }, storage: { mode: { light: 'day' } } } },
    error: 'Unknown capture.type',
  },
  {
    name: 'viewport before storage',
    config: { capture: { viewport: { width: 0 }, storage: { mode: { light: 'day' } } } },
    error: 'Invalid capture.viewport',
  },
  {
    name: 'storage before actions',
    config: { capture: { storage: { mode: { light: 'day' } }, actions: [{ type: 'eval' }] } },
    error: 'storage values must be strings or {light,dark}',
  },
  {
    name: 'build before server',
    config: { capture: null, build: { command: 'touch should-not-build', timeoutMs: 0 }, server: { directory: '.', port: 0 } },
    error: 'Invalid build command or timeout',
  },
];

for (const entry of firstErrors) {
  test(`configuration preserves first error: ${entry.name}`, t => {
    const root = fixture(t, { name: 'Order', build: 'touch should-not-build', ...entry.config });
    const before = readdirSync(root);

    const result = run(root);

    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), entry.error);
    assert.equal(result.stdout, '');
    assert.deepEqual(readdirSync(root), before);
  });
}

test('rejects symlink output before writes', t => {
  const root = fixture(t, { name: 'Example', output: 'images' });
  symlinkSync(tmpdir(), path.join(root, 'images'));

  assert.notEqual(run(root, ['--dry-run']).status, 0);
});

test('reuse requires both valid existing PNGs', t => {
  const root = fixture(t);
  writeFileSync(path.join(root, 'product-captures.json'), JSON.stringify({ name: 'Example' }));

  assert.notEqual(run(root, ['--dry-run', '--reuse-screenshots']).status, 0);
});

for (const appearance of [{ fadeEnd: 0.2 }, { backdropPosition: 'diagonal' }]) {
  test(`validates appearance before commands: ${JSON.stringify(appearance)}`, t => {
    const root = fixture(t, { name: 'Invalid appearance', appearance, build: 'touch should-not-build' });
    const before = readdirSync(root);

    const result = run(root, ['--dry-run']);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /fadeStart|backdropPosition/);
    assert.deepEqual(readdirSync(root), before);
  });
}
