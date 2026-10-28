import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, copyFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
for (const mode of ['EOF after launch', 'EOF during launch', 'SIGTERM after launch']) test(mode, async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'obsidian-bridge-test-'));
  try {
    const module = path.join(root, 'node_modules/obsidian-launcher'); await mkdir(module, { recursive: true });
    const config = path.join(root, 'config'); await mkdir(config);
    await writeFile(path.join(module, 'package.json'), JSON.stringify({ type: 'module', exports: './index.js' }));
    await writeFile(path.join(module, 'index.js'), `import {EventEmitter} from 'node:events';import {writeFile} from 'node:fs/promises';export default class {async launch(options){if(options.localStorage.theme!=='dark')throw Error('Launch storage was not forwarded');await new Promise(r=>setTimeout(r,150));const proc=new EventEmitter();proc.pid=12345;proc.exitCode=null;proc.kill=signal=>{writeFile(${JSON.stringify(path.join(root, 'killed'))},signal);setImmediate(()=>{proc.exitCode=0;proc.emit('exit',0)});};return {proc,configDir:${JSON.stringify(config)}};}}`);
    await copyFile(new URL('../adapters/obsidian-launcher.mjs', import.meta.url), path.join(root, 'adapter.mjs'));
    const proc = spawn(process.execPath, [path.join(root, 'adapter.mjs'), JSON.stringify({ localStorage: { theme: 'dark' } })], { stdio: ['pipe', 'pipe', 'pipe'] });
    const exited = once(proc, 'exit'); let output = ''; let errors = '';
    proc.stdout.on('data', data => { output += data; }); proc.stderr.on('data', data => { errors += data; });
    if (mode === 'EOF during launch') proc.stdin.end();
    else {
      while (!output.includes('\n') && proc.exitCode === null) await new Promise(r => setTimeout(r,20));
      assert.equal(JSON.parse(output).pid, 12345);
      assert.match(JSON.parse(output).endpoint, /^http:\/\/127\.0\.0\.1:\d+$/);
      assert.equal(JSON.parse(output).configDir, config);
      if (mode.startsWith('SIGTERM')) proc.kill('SIGTERM'); else proc.stdin.end();
    }
    const [code] = await exited; assert.equal(code, 0, errors);
    assert.equal(await readFile(path.join(root, 'killed'), 'utf8'), 'SIGTERM');
    await assert.rejects(stat(config), { code: 'ENOENT' });
  } finally { await rm(root, { recursive: true, force: true }); }
});
