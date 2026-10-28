// Launch-only bridge: C# owns the browser and all capture behavior.
import ObsidianLauncher from 'obsidian-launcher';
import { rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { once } from 'node:events';
const options = JSON.parse(process.argv[2]);
const server = createServer();
await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
const port = server.address().port;
await new Promise(resolve => server.close(resolve));
let instance;
let stopping;
let launching;
async function stop() {
  stopping ??= (async () => {
    try { instance ??= await launching; } catch {}
    if (instance?.proc && instance.proc.exitCode === null) {
      instance.proc.kill('SIGTERM');
      await Promise.race([once(instance.proc, 'exit'), new Promise(resolve => setTimeout(resolve, 3000))]);
      if (instance.proc.exitCode === null) instance.proc.kill('SIGKILL');
    }
    if (instance?.configDir) await rm(instance.configDir, { recursive: true, force: true });
  })();
  await stopping;
}
for (const signal of ['SIGTERM', 'SIGINT']) process.once(signal, () => stop().finally(() => process.exit()));
process.stdin.resume();
process.stdin.once('end', () => stop().finally(() => process.exit()));
try {
  launching = new ObsidianLauncher({ cacheDir: options.cache }).launch({
    appVersion: options.appVersion, installerVersion: options.installerVersion,
    localStorage: options.localStorage,
    vault: options.vault, copy: false, plugins: [options.plugin],
    args: [`--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1', '--no-sandbox', '--disable-gpu'],
    spawnOptions: { stdio: 'ignore', env: { ...process.env, ELECTRON_DISABLE_SECURITY_WARNINGS: 'true' } },
  });
  instance = await launching;
  if (stopping) { await stop(); process.exit(); }
  process.stdout.write(JSON.stringify({ endpoint: `http://127.0.0.1:${port}`, pid: instance.proc.pid, configDir: instance.configDir }) + '\n');
  instance.proc.once('exit', () => stop().finally(() => process.exit()));
} catch (error) {
  console.error(error.stack ?? error);
  await stop(); process.exit(1);
}
