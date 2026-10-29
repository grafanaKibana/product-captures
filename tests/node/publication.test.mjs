import { before, test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import {
  action, childProcess, cleanupPidFile, command, dll, git, githubCalls, publication, recordedPid, waitFor, within,
} from './helpers/fixtures.mjs';

let toolVersion;
before(() => {
  const result = command('dotnet', [dll, '--version']);
  assert.equal(result.status, 0, result.stderr);
  toolVersion = result.stdout.trim();
  assert.match(toolVersion, /^\d+\.\d+\.\d+$/);
});

test('an image PR contains exactly the four owned PNGs without changing checkout or index', t => {
  const fixture_ = publication(t, toolVersion);
  writeFileSync(path.join(fixture_.root, 'unrelated.txt'), 'preserve');
  git(fixture_.root, ['add', '--', 'unrelated.txt']);
  const indexBefore = readFileSync(path.join(fixture_.root, '.git/index'));
  const statusBefore = git(fixture_.root, ['status', '--porcelain']);

  const result = action(fixture_.root, 'pr', { ...fixture_.env, CAPTURE_UPDATE_PR: 'true' });

  assert.equal(result.status, 0, result.stderr);
  const calls = githubCalls(fixture_);
  const tree = calls.find(call => call.args[3]?.endsWith('/git/trees'));
  assert.deepEqual(tree.input.tree.map(entry => entry.path),
    fixture_.files.map(file => `docs/assets/product-captures/${file}`));
  assert(!calls.some(call => call.args[0] !== 'api'));
  const statusAfter = git(fixture_.root, ['status', '--porcelain']);
  assert.deepEqual(statusAfter.trim().split('\n').sort(),
    (statusBefore + '?? gh.log\n').trim().split('\n').sort());
  assert.deepEqual(readFileSync(path.join(fixture_.root, '.git/index')), indexBefore);
  assert.equal(readFileSync(path.join(fixture_.root, 'unrelated.txt'), 'utf8'), 'preserve');
});

test('a release upload contains only the finished pair', t => {
  const fixture_ = publication(t, toolVersion);

  const result = action(fixture_.root, 'release', { ...fixture_.env, CAPTURE_RELEASE_TAG: 'v1.0.0' });

  assert.equal(result.status, 0, result.stderr);
  const calls = githubCalls(fixture_);
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0].args.slice(3, 5), [
    path.join(fixture_.out, 'hero.png'), path.join(fixture_.out, 'hero-dark.png'),
  ]);
  assert.equal(calls[0].args.filter(argument => argument.endsWith('.png')).length, 2);
  assert(!calls[0].args.some(argument => argument.endsWith('manifest.json')));
});

test('publication rejects a mismatched tool version before invoking GitHub', t => {
  const fixture_ = publication(t, toolVersion);
  const manifestPath = path.join(fixture_.out, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.tool = `${toolVersion}-mismatch`;
  writeFileSync(manifestPath, JSON.stringify(manifest));

  const result = action(fixture_.root, 'pr', { ...fixture_.env, CAPTURE_UPDATE_PR: 'true' });

  assert.notEqual(result.status, 0);
  assert(!existsSync(fixture_.env.FAKE_GH_LOG));
});

test('publication rejects stale configuration before invoking GitHub', t => {
  const fixture_ = publication(t, toolVersion);
  writeFileSync(path.join(fixture_.root, 'product-captures.json'), JSON.stringify({ name: 'Changed' }));

  const result = action(fixture_.root, 'pr', { ...fixture_.env, CAPTURE_UPDATE_PR: 'true' });

  assert.notEqual(result.status, 0);
  assert(!existsSync(fixture_.env.FAKE_GH_LOG));
});

for (const operation of ['pr', 'release']) {
  test(`SIGTERM cancels GitHub ${operation} without later publication writes`, async t => {
    const fixture_ = publication(t, toolVersion, `block-${operation}`);
    const process_ = childProcess(t, 'dotnet', [dll, 'action', operation], {
      cwd: fixture_.root,
      env: {
        ...fixture_.env,
        CAPTURE_UPDATE_PR: 'true',
        CAPTURE_RELEASE_TAG: 'v1.0.0',
      },
    });
    cleanupPidFile(t, fixture_.env.FAKE_GH_BLOCKED);
    await waitFor(() => recordedPid(fixture_.env.FAKE_GH_BLOCKED) !== null
      || process_.exited, 'GitHub fixture never reached its blocking boundary');
    const fixturePid = recordedPid(fixture_.env.FAKE_GH_BLOCKED);
    assert.notEqual(fixturePid, null, process_.errors);

    process_.child.kill('SIGTERM');
    const result = await within(process_.completed, 5_000, 'Publication cancellation timed out');

    assert.ifError(result.error);
    assert.notEqual(result.code, 0);
    assert.throws(() => process.kill(fixturePid, 0), { code: 'ESRCH' });
    const calls = githubCalls(fixture_);
    assert(!calls.some(call => call.args[3]?.endsWith('/git/refs')
      || call.args[3]?.includes('/git/refs/heads/') || call.args[3]?.endsWith('/pulls')));
    if (operation === 'pr') assert(calls.at(-1).args[3].endsWith('/git/trees'));
    else assert.equal(calls.length, 1);
  });
}
