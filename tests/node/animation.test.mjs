import { before, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import {
  action, command, dll, fixture, githubCalls, pixel, publication, remote, run,
} from './helpers/fixtures.mjs';

const gif = Buffer.from('R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==', 'base64');
const files = [
  'captures/overview-light.png', 'captures/overview-dark.png',
  'hero.png', 'hero-dark.png', 'showcase.gif', 'showcase-dark.gif',
];
const animation = { steps: [{ text: 'See the note', type: 'focus', zoom: 1, durationMs: 400 }] };
let toolVersion;

before(() => {
  const result = command('dotnet', [dll, '--version']);
  assert.equal(result.status, 0, result.stderr);
  toolVersion = result.stdout.trim();
});

function savedMedia(root) {
  const output = path.join(root, 'docs/assets/product-captures');
  mkdirSync(path.join(output, 'captures'), { recursive: true });
  for (const file of files) writeFileSync(path.join(output, file), file.endsWith('.gif') ? gif : pixel);
  return output;
}

test('explicit reuse preserves both GIFs without starting a product or encoder', t => {
  const root = fixture(t, { name: 'Reuse', animation, build: 'exit 71', capture: { url: 'https://unused.invalid' } });
  const output = savedMedia(root);
  const bin = path.join(root, 'bin');
  mkdirSync(bin);
  const encoder = path.join(bin, 'ffmpeg');
  writeFileSync(encoder, '#!/usr/bin/env node\nprocess.exit(91);\n');
  chmodSync(encoder, 0o755);

  const result = run(root, ['--reuse-screenshots'], { PATH: bin + path.delimiter + process.env.PATH });

  assert.equal(result.status, 0, result.stderr);
  for (const file of files.filter(file => file.endsWith('.gif'))) {
    assert.deepEqual(readFileSync(path.join(output, file)), gif);
  }
  assert.deepEqual(JSON.parse(readFileSync(path.join(output, 'manifest.json'))).outputs, files);
});

test('an encoder failure leaves the entire previous light and dark output set intact', async t => {
  const url = await remote(t, '<p>A real browser scene</p>');
  const root = fixture(t, {
    name: 'Atomic animation', animation,
    capture: { url, viewport: { width: 320, height: 240 } },
    appearance: { width: 400, height: 300 },
  });
  const output = savedMedia(root);
  const bin = path.join(root, 'bin');
  mkdirSync(bin);
  const encoder = path.join(bin, 'ffmpeg');
  writeFileSync(encoder, '#!/usr/bin/env node\nprocess.exit(process.argv.includes("-version") ? 0 : 17);\n');
  chmodSync(encoder, 0o755);

  const result = run(root, [], { PATH: bin + path.delimiter + process.env.PATH });

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ffmpeg exited 17/);
  for (const file of files) {
    assert.deepEqual(readFileSync(path.join(output, file)), file.endsWith('.gif') ? gif : pixel);
  }
  assert(!existsSync(path.join(output, 'manifest.json')));
});

for (const operation of ['pr', 'release']) {
  test(`animation ${operation} publishes only the configured PNG and GIF files`, t => {
    const fixture_ = publication(t, toolVersion);
    const config = { name: 'Example', animation };
    const bytes = Buffer.from(JSON.stringify(config));
    writeFileSync(path.join(fixture_.root, 'product-captures.json'), bytes);
    savedMedia(fixture_.root);
    const manifestPath = path.join(fixture_.out, 'manifest.json');
    const manifest = JSON.parse(readFileSync(manifestPath));
    manifest.outputs = files;
    manifest.configHash = createHash('sha256').update(bytes).digest('hex');
    writeFileSync(manifestPath, JSON.stringify(manifest));

    const result = action(fixture_.root, operation, {
      ...fixture_.env, CAPTURE_UPDATE_PR: 'true', CAPTURE_RELEASE_TAG: 'v1.0.0',
    });

    assert.equal(result.status, 0, result.stderr);
    const calls = githubCalls(fixture_);
    if (operation === 'pr') {
      const tree = calls.find(call => call.args[3]?.endsWith('/git/trees'));
      assert.deepEqual(tree.input.tree.map(entry => entry.path),
        files.map(file => `docs/assets/product-captures/${file}`));
    } else {
      assert.deepEqual(calls[0].args.filter(argument => /\.(png|gif)$/.test(argument)),
        files.filter(file => !file.startsWith('captures/')).map(file => path.join(fixture_.out, file)));
    }
  });
}

test('reuse rejects a truncated GIF before replacing any outputs', t => {
  const root = fixture(t, { name: 'Invalid reuse', animation });
  const output = savedMedia(root);
  writeFileSync(path.join(output, 'showcase-dark.gif'), gif.subarray(0, -1));

  const result = run(root, ['--reuse-screenshots']);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Expected complete GIF/);
  assert.deepEqual(readFileSync(path.join(output, 'hero.png')), pixel);
  assert(!existsSync(path.join(output, 'manifest.json')));
});
