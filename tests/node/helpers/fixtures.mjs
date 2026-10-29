import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync,
  readFileSync, readdirSync, rmSync, writeFileSync,
} from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

export const toolRoot = fileURLToPath(new URL('../../../', import.meta.url));
export const dll = path.join(toolRoot, 'bin/Debug/net10.0/ProductCaptures.dll');
export const pixel = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4N0AAAAASUVORK5CYII=',
  'base64',
);

const cleanupTasks = new WeakMap();

export function cleanupAfter(t, cleanup) {
  let tasks = cleanupTasks.get(t);
  if (!tasks) {
    tasks = [];
    cleanupTasks.set(t, tasks);
    t.after(async () => {
      const failures = [];
      for (let index = tasks.length - 1; index >= 0; index--) {
        try {
          await tasks[index]();
        } catch (error) {
          failures.push(error);
        }
      }
      if (failures.length) throw new AggregateError(failures, 'Test fixture cleanup failed');
    });
  }
  tasks.push(cleanup);
}

export function cleanupPidFile(t, file) {
  cleanupAfter(t, () => {
    if (!existsSync(file)) return;
    const pid = recordedPid(file);
    assert.notEqual(pid, null, 'Owned process PID must be valid');
    try {
      process.kill(pid, 'SIGKILL');
    } catch (error) {
      if (error.code !== 'ESRCH') throw error;
    }
  });
}

export function recordedPid(file) {
  if (!existsSync(file)) return null;
  const value = readFileSync(file, 'utf8').trim();
  const pid = Number(value);
  return /^[1-9]\d*$/.test(value) && Number.isSafeInteger(pid) && pid <= 2_147_483_647
    ? pid : null;
}

export function fixture(t, config = { name: 'Example', capture: null }) {
  const root = mkdtempSync(path.join(tmpdir(), 'product-captures-test-'));
  cleanupAfter(t, () => rmSync(root, { recursive: true, force: true }));
  writeFileSync(path.join(root, 'product-captures.json'), JSON.stringify(config));
  return root;
}

export function environment(overrides = {}) {
  return {
    ...process.env,
    CAPTURE_CONFIG: '',
    CAPTURE_DRY_RUN: '',
    CAPTURE_REUSE_SCREENSHOTS: '',
    CAPTURE_URL: '',
    CAPTURE_RELEASE_TAG: '',
    CAPTURE_UPDATE_PR: '',
    ...overrides,
  };
}

export function command(executable, args, options = {}) {
  const result = spawnSync(executable, args, {
    encoding: 'utf8',
    timeout: 45_000,
    killSignal: 'SIGKILL',
    maxBuffer: 16 * 1024 * 1024,
    ...options,
  });
  assert.ifError(result.error);
  return result;
}

export function run(root, args = [], overrides = {}) {
  return command('dotnet', [
    dll, '--config', path.join(root, 'product-captures.json'), ...args,
  ], { cwd: tmpdir(), env: environment(overrides) });
}

export function action(root, operation, overrides = {}) {
  return command('dotnet', [dll, 'action', operation], {
    cwd: root,
    env: environment({
      CAPTURE_CONFIG: path.join(root, 'product-captures.json'),
      GITHUB_WORKSPACE: root,
      ...overrides,
    }),
  });
}

export function pngs(root) {
  return readdirSync(root, { recursive: true })
    .filter(file => file.endsWith('.png'))
    .sort();
}

export function git(root, args) {
  const result = command('git', [
    '-c', 'core.hooksPath=/dev/null',
    '-c', 'commit.gpgsign=false',
    '-c', 'user.name=Product Captures Test',
    '-c', 'user.email=test@example.invalid',
    ...args,
  ], { cwd: root });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}

export async function within(promise, milliseconds, message) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

export async function waitFor(predicate, message, milliseconds = 10_000) {
  const deadline = Date.now() + milliseconds;
  while (!predicate()) {
    assert(Date.now() < deadline, message);
    await delay(20);
  }
}

export function childProcess(t, executable, args, options = {}) {
  const child = spawn(executable, args, { stdio: ['pipe', 'pipe', 'pipe'], ...options });
  let output = '';
  let errors = '';
  let spawnError;
  child.stdout?.on('data', data => { output += data; });
  child.stderr?.on('data', data => { errors += data; });
  child.once('error', error => { spawnError = error; });
  const completed = new Promise(resolve => {
    child.once('close', (code, signal) => resolve({ code, signal, error: spawnError }));
  });
  const dispose = async () => {
    const running = child.exitCode === null && child.signalCode === null;
    if (running) child.kill('SIGTERM');
    try {
      const result = await within(completed, 5_000, running
        ? 'Owned test process ignored SIGTERM'
        : 'Owned test process output pipes did not close');
      assert.ifError(result.error);
    } catch (error) {
      if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
      child.stdin?.destroy();
      child.stdout?.destroy();
      child.stderr?.destroy();
      try {
        await within(completed, 5_000, 'Owned test process did not close after forced cleanup');
      } catch (cleanupError) {
        throw new AggregateError([error, cleanupError], 'Owned test process cleanup failed');
      }
      throw error;
    }
  };
  cleanupAfter(t, dispose);
  return {
    child,
    completed,
    dispose,
    get exited() { return child.exitCode !== null || child.signalCode !== null || spawnError !== undefined; },
    get output() { return output; },
    get errors() { return errors; },
  };
}

export async function remote(t, html) {
  const script = fileURLToPath(new URL('../fixtures/http-server.mjs', import.meta.url));
  const server = childProcess(t, process.execPath, [script]);
  server.child.stdin.end(JSON.stringify({ html }));
  await waitFor(
    () => server.output.includes('\n') || server.exited,
    'HTTP fixture did not report its listening address',
  );
  assert(server.output.includes('\n'), server.errors || 'HTTP fixture exited before listening');
  return `http://127.0.0.1:${JSON.parse(server.output).port}`;
}

export async function availablePort() {
  const server = createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const { port } = server.address();
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return port;
}

export function publication(t, toolVersion, mode = '') {
  const root = fixture(t, { name: 'Example' });
  const bin = path.join(root, 'bin');
  mkdirSync(bin);
  writeFileSync(path.join(bin, 'package.json'), JSON.stringify({ type: 'module' }));
  git(root, ['init', '-q']);
  git(root, ['commit', '--allow-empty', '-qm', 'Fixture']);
  const source = git(root, ['rev-parse', 'HEAD']).trim();
  const files = [
    'captures/overview-light.png', 'captures/overview-dark.png',
    'hero.png', 'hero-dark.png',
  ];
  const out = path.join(root, 'docs/assets/product-captures');
  mkdirSync(path.join(out, 'captures'), { recursive: true });
  for (const file of files) writeFileSync(path.join(out, file), pixel);
  writeFileSync(path.join(out, 'manifest.json'), JSON.stringify({
    version: 2,
    tool: toolVersion,
    configHash: createHash('sha256')
      .update(readFileSync(path.join(root, 'product-captures.json'))).digest('hex'),
    mode: 'fresh',
    source,
    outputs: files,
  }));
  const fakeGitHub = path.join(bin, 'gh');
  copyFileSync(new URL('../fixtures/github.mjs', import.meta.url), fakeGitHub);
  chmodSync(fakeGitHub, 0o755);
  return {
    root, out, files,
    env: environment({
      PATH: bin + path.delimiter + process.env.PATH,
      CAPTURE_CONFIG: path.join(root, 'product-captures.json'),
      CAPTURE_DRY_RUN: 'false',
      GITHUB_WORKSPACE: root,
      GITHUB_REPOSITORY: 'owner/repo',
      FAKE_GH_LOG: path.join(root, 'gh.log'),
      FAKE_GH_MODE: mode,
      FAKE_GH_BLOCKED: path.join(root, 'blocked-gh'),
    }),
  };
}

export function githubCalls(fixture_) {
  assert(existsSync(fixture_.env.FAKE_GH_LOG), 'GitHub fixture was never called');
  return readFileSync(fixture_.env.FAKE_GH_LOG, 'utf8').trim().split('\n').map(JSON.parse);
}
