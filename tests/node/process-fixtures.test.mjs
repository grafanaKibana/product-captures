import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  childProcess, cleanupPidFile, fixture, recordedPid, waitFor, within,
} from './helpers/fixtures.mjs';

const orphanFixture = fileURLToPath(new URL('./fixtures/orphan-pipes.mjs', import.meta.url));

test('process cleanup bounds inherited pipes after the parent exits', async t => {
  const root = fixture(t);
  const pidFile = path.join(root, 'descendant-pid');
  const parent = childProcess(t, process.execPath, [orphanFixture, pidFile]);
  cleanupPidFile(t, pidFile);
  await waitFor(() => parent.exited && recordedPid(pidFile) !== null,
    'Orphan fixture did not exit after starting its descendant');
  const descendantPid = recordedPid(pidFile);
  assert.equal(parent.child.exitCode, 0, parent.errors);
  process.kill(descendantPid, 0);

  await assert.rejects(
    within(parent.dispose(), 7_000, 'Process fixture cleanup exceeded its deadline'),
    /Owned test process output pipes did not close/,
  );
  await within(parent.completed, 1_000, 'Destroyed fixture pipes did not close');
  t.after(async () => {
    await waitFor(() => {
      try { process.kill(descendantPid, 0); return false; }
      catch (error) { if (error.code === 'ESRCH') return true; throw error; }
    }, 'Owned descendant survived test cleanup');
  });
});

test('process cleanup kills its known descendant before draining inherited pipes', async t => {
  const root = fixture(t);
  const pidFile = path.join(root, 'descendant-pid');
  const parent = childProcess(t, process.execPath, [orphanFixture, pidFile]);
  cleanupPidFile(t, pidFile);
  await waitFor(() => parent.exited && recordedPid(pidFile) !== null,
    'Orphan fixture did not exit after starting its descendant');
  const descendantPid = recordedPid(pidFile);

  t.after(async () => {
    await within(parent.completed, 1_000, 'Known descendant kept fixture pipes open');
    assert.throws(() => process.kill(descendantPid, 0), { code: 'ESRCH' });
  });
});
