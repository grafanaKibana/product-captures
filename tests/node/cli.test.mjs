import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { action, fixture, pixel, run } from './helpers/fixtures.mjs';

test('dry-run validates an absolute config from an unrelated cwd without consumer writes', t => {
  const root = fixture(t);
  const before = readdirSync(root);

  const result = run(root, ['--dry-run']);

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readdirSync(root), before);
  assert.deepEqual(JSON.parse(result.stdout).files, ['hero.png', 'hero-dark.png']);
});

for (const args of [['--scale', '2'], ['--dry-run', '--dry-run'], ['--url']]) {
  test(`rejects invalid CLI flags: ${args.join(' ')}`, t => {
    const root = fixture(t);
    assert.notEqual(run(root, args).status, 0);
  });
}

for (const [args, expected] of [
  [['--unknown', '--unknown'], 'Unknown option: --unknown'],
  [['--dry-run', '--dry-run'], 'Duplicate option: --dry-run'],
  [['--url', '--dry-run'], 'Missing value for --url'],
  [['--config', '--dry-run'], 'Duplicate option: --config'],
]) {
  test(`CLI diagnostics precede config validation: ${args.join(' ')}`, t => {
    const root = fixture(t, { name: '', build: 'touch should-not-build' });
    const before = readdirSync(root);

    const result = run(root, args);

    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), expected);
    assert.equal(result.stdout, '');
    assert.deepEqual(readdirSync(root), before);
  });
}

test('dry-run never starts configured product commands', t => {
  const root = fixture(t, {
    name: 'Example',
    build: 'touch should-not-exist',
    server: { command: 'touch server-started', url: 'http://127.0.0.1:4180' },
  });

  const result = run(root, ['--dry-run']);

  assert.equal(result.status, 0, result.stderr);
  assert(!existsSync(path.join(root, 'should-not-exist')));
  assert(!existsSync(path.join(root, 'server-started')));
  assert.equal(JSON.parse(result.stdout).files.length, 4);
});

test('Action plan emits the expected outputs while remaining dry', t => {
  const root = fixture(t);
  const output = path.join(root, 'output');

  const result = action(root, 'plan', { GITHUB_OUTPUT: output });

  assert.equal(result.status, 0, result.stderr);
  assert.match(readFileSync(output, 'utf8'), /needs-obsidian=false\nneeds-node=false\ndry-run=true/);
  assert(!existsSync(path.join(root, 'docs')));
});

test('Action boolean values are strict', t => {
  const root = fixture(t);

  const result = action(root, 'plan', { CAPTURE_DRY_RUN: 'yes' });

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /true or false/);
});

const plans = [
  { name: 'source-free', config: { capture: null }, obsidian: false, node: false },
  { name: 'web', config: {}, obsidian: false, node: false },
  { name: 'web build', config: { build: 'touch should-not-build' }, obsidian: false, node: true },
  { name: 'preview build', config: { build: 'touch should-not-build' }, env: { CAPTURE_URL: 'https://preview.example' }, obsidian: false, node: false },
  { name: 'obsidian', config: { capture: { type: 'obsidian' } }, obsidian: true, node: true },
  { name: 'reused obsidian', config: { capture: { type: 'obsidian' } }, env: { CAPTURE_REUSE_SCREENSHOTS: 'true' }, obsidian: false, node: false },
  { name: 'explicit capture', config: {}, env: { CAPTURE_DRY_RUN: 'false' }, obsidian: false, node: false, dryRun: false },
];

for (const entry of plans) {
  test(`Action plan preserves exact output and prerequisites: ${entry.name}`, t => {
    const root = fixture(t, { name: entry.name, ...entry.config });
    if (entry.env?.CAPTURE_REUSE_SCREENSHOTS === 'true') {
      const captures = path.join(root, 'docs/assets/product-captures/captures');
      mkdirSync(captures, { recursive: true });
      for (const theme of ['light', 'dark']) {
        writeFileSync(path.join(captures, `overview-${theme}.png`), pixel);
      }
    }
    const output = path.join(root, 'action-output');
    writeFileSync(output, 'existing=value\n');

    const result = action(root, 'plan', { ...entry.env, GITHUB_OUTPUT: output });

    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(output, 'utf8'), 'existing=value\n'
      + 'artifact-path=docs/assets/product-captures\n'
      + `needs-obsidian=${entry.obsidian}\nneeds-node=${entry.node}\ndry-run=${entry.dryRun ?? true}\n`);
    assert.equal(JSON.parse(result.stdout).dryRun, true);
    assert(!existsSync(path.join(root, 'should-not-build')));
    assert(!existsSync(path.join(root, 'docs/assets/product-captures/hero.png')));
  });
}

for (const [dryRun, reuse, expected] of [
  ['invalid', 'invalid', 'DRY_RUN'],
  ['true', 'invalid', 'REUSE_SCREENSHOTS'],
  ['true', 'false', 'UPDATE_PR'],
]) {
  test(`Action rejects invalid ${expected} before config access`, t => {
    const root = fixture(t);

    const result = action(root, 'plan', {
      CAPTURE_CONFIG: path.join(root, 'missing.json'),
      CAPTURE_DRY_RUN: dryRun,
      CAPTURE_REUSE_SCREENSHOTS: reuse,
      CAPTURE_URL: 'https://preview.example',
      CAPTURE_RELEASE_TAG: 'v1.0.0',
      CAPTURE_UPDATE_PR: 'invalid',
    });

    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), `CAPTURE_${expected} must be true or false`);
  });
}

test('web product builds request the pinned Node runtime in Action planning', t => {
  const root = fixture(t, { name: 'Build', build: 'npm run build' });
  const output = path.join(root, 'output');

  const result = action(root, 'plan', { GITHUB_OUTPUT: output });

  assert.equal(result.status, 0, result.stderr);
  assert.match(readFileSync(output, 'utf8'), /needs-node=true/);
});

for (const capture of [null, { type: 'obsidian' }]) {
  test(`preview URL cannot suppress a non-web build: ${capture?.type ?? 'source-free'}`, t => {
    const root = fixture(t, { name: 'Example', capture, build: 'touch should-not-exist' });

    const result = run(root, ['--dry-run', '--url', 'https://preview.example']);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /only available for web/);
    assert(!existsSync(path.join(root, 'should-not-exist')));
  });
}
