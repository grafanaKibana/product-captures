import { EventEmitter } from 'node:events';
import { writeFileSync } from 'node:fs';
import path from 'node:path';
import { setTimeout } from 'node:timers/promises';

export default class FakeLauncher {
  async launch(options) {
    if (options.localStorage.theme !== 'dark') {
      throw new Error('Launch storage was not forwarded');
    }
    await setTimeout(150);
    const process_ = new EventEmitter();
    process_.pid = 12345;
    process_.exitCode = null;
    process_.kill = signal => {
      writeFileSync(path.join(process.env.FAKE_OBSIDIAN_ROOT, 'killed'), signal);
      setImmediate(() => {
        process_.exitCode = 0;
        process_.emit('exit', 0);
      });
    };
    return {
      proc: process_,
      configDir: path.join(process.env.FAKE_OBSIDIAN_ROOT, 'config'),
    };
  }
}
