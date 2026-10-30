import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';

const descendant = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {
  stdio: ['ignore', 'inherit', 'inherit'],
});
writeFileSync(process.argv[2], String(descendant.pid));
descendant.unref();
