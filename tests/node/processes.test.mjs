import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import {
  availablePort, childProcess, cleanupPidFile, dll, environment, fixture, recordedPid,
  remote, run, waitFor, within,
} from './helpers/fixtures.mjs';

test('SIGTERM cancels the owned build process and leaves no output', async t => {
  const root = fixture(t, {
    name: 'Example', build: "sh -c 'echo $$ > owned-pid; exec sleep 60'",
  });
  const process_ = childProcess(t, 'dotnet', [
    dll, '--config', path.join(root, 'product-captures.json'),
  ], { cwd: tmpdir(), env: environment() });
  const pidFile = path.join(root, 'owned-pid');
  cleanupPidFile(t, pidFile);
  await waitFor(() => recordedPid(pidFile) !== null || process_.exited,
    'Build process never reported its PID', 5_000);
  const buildPid = recordedPid(pidFile);
  assert.notEqual(buildPid, null, process_.errors);

  process_.child.kill('SIGTERM');
  const result = await within(process_.completed, 5_000, 'Build cancellation timed out');

  assert.ifError(result.error);
  assert.notEqual(result.code, 0);
  assert.throws(() => process.kill(buildPid, 0), { code: 'ESRCH' });
  assert(!existsSync(path.join(root, 'docs')));
});

test('an occupied command-server listener is preserved and the command never starts', async t => {
  const url = await remote(t, '<p>Existing service</p>');
  const root = fixture(t, { name: 'Occupied', server: { command: 'touch server-started', url } });

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /occupied/);
  assert(!existsSync(path.join(root, 'server-started')));
  assert.equal((await fetch(url, { signal: AbortSignal.timeout(5_000) })).status, 200);
});

test('failed build diagnostics retain standard output', t => {
  const root = fixture(t, { name: 'Diagnostic', build: 'echo REQUIRED_BUILD_DIAGNOSTIC; exit 7' });

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /exited 7/);
  assert.match(result.stderr, /REQUIRED_BUILD_DIAGNOSTIC/);
});

test('the whole-operation timeout kills descendants after the shell has exited', async t => {
  const url = await remote(t, '<p>Unrelated listener</p>');
  const root = fixture(t, {
    name: 'Background child',
    build: { command: 'sleep 10 & echo $! > background-pid; exit 0', timeoutMs: 1500 },
  });
  cleanupPidFile(t, path.join(root, 'background-pid'));
  const started = Date.now();

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert(Date.now() - started < 7_000, 'Timeout must also bound stdout/stderr draining');
  const pidFile = path.join(root, 'background-pid');
  assert(existsSync(pidFile), 'Shell must have started its background child');
  const pid = Number(readFileSync(pidFile, 'utf8').trim());
  assert.throws(() => process.kill(pid, 0), { code: 'ESRCH' });
  assert.equal((await fetch(url, { signal: AbortSignal.timeout(5_000) })).status, 200);
  assert(!existsSync(path.join(root, 'docs')));
});

test('command-server startup bounds inherited pipes and retains both diagnostic streams', async t => {
  const url = await remote(t, '<p>Unrelated listener</p>');
  const root = fixture(t, {
    name: 'Failed server',
    server: {
      command: 'echo SERVER_DIAGNOSTIC; echo SERVER_STDERR >&2; sleep 60 & echo $! > server-child-pid; exit 7',
      url: `http://127.0.0.1:${await availablePort()}`,
      timeoutMs: 1500,
    },
  });
  const pidFile = path.join(root, 'server-child-pid');
  cleanupPidFile(t, pidFile);
  const started = Date.now();

  const result = run(root);

  assert.notEqual(result.status, 0);
  assert(Date.now() - started < 7_000, 'Startup failure must not wait for descendant pipes');
  assert.match(result.stderr, /SERVER_DIAGNOSTIC/);
  assert.match(result.stderr, /SERVER_STDERR/);
  assert(existsSync(pidFile));
  assert.throws(() => process.kill(Number(readFileSync(pidFile, 'utf8').trim()), 0), { code: 'ESRCH' });
  assert.equal((await fetch(url, { signal: AbortSignal.timeout(5_000) })).status, 200);
  assert(!existsSync(path.join(root, 'docs')));
});
