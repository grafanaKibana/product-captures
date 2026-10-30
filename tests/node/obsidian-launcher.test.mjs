import test from 'node:test';
import assert from 'node:assert/strict';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { childProcess, fixture, toolRoot, waitFor, within } from './helpers/fixtures.mjs';

for (const mode of ['EOF after launch', 'EOF during launch', 'SIGTERM after launch']) {
  test(`Obsidian launcher shuts down on ${mode}`, async t => {
    const root = fixture(t);
    const module = path.join(root, 'node_modules/obsidian-launcher');
    mkdirSync(module, { recursive: true });
    const configDirectory = path.join(root, 'config');
    mkdirSync(configDirectory);
    writeFileSync(path.join(module, 'package.json'), JSON.stringify({
      type: 'module', exports: './index.js',
    }));
    copyFileSync(new URL('./fixtures/obsidian-launcher.mjs', import.meta.url), path.join(module, 'index.js'));
    copyFileSync(path.join(toolRoot, 'adapters/obsidian-launcher.mjs'), path.join(root, 'adapter.mjs'));
    const process_ = childProcess(t, process.execPath, [
      path.join(root, 'adapter.mjs'), JSON.stringify({ localStorage: { theme: 'dark' } }),
    ], { env: { ...process.env, FAKE_OBSIDIAN_ROOT: root } });

    if (mode === 'EOF during launch') {
      process_.child.stdin.end();
    } else {
      await waitFor(() => process_.output.includes('\n') || process_.exited,
        'Obsidian launcher never reported readiness');
      assert(process_.output.includes('\n'), process_.errors);
      const launch = JSON.parse(process_.output);
      assert.equal(launch.pid, 12345);
      assert.match(launch.endpoint, /^http:\/\/127\.0\.0\.1:\d+$/);
      assert.equal(launch.configDir, configDirectory);
      if (mode.startsWith('SIGTERM')) process_.child.kill('SIGTERM');
      else process_.child.stdin.end();
    }

    const result = await within(process_.completed, 5_000, 'Obsidian launcher shutdown timed out');

    assert.ifError(result.error);
    assert.equal(result.code, 0, process_.errors);
    assert.equal(readFileSync(path.join(root, 'killed'), 'utf8'), 'SIGTERM');
    await assert.rejects(stat(configDirectory), { code: 'ENOENT' });
  });
}
