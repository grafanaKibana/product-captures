#!/usr/bin/env node
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';

const args = process.argv.slice(2);
const input = args.includes('--input') ? JSON.parse(readFileSync(0, 'utf8')) : null;
appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify({ args, input }) + '\n');

function blockAtBoundary() {
  writeFileSync(process.env.FAKE_GH_BLOCKED, String(process.pid));
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60_000);
}

if (args[0] === 'release') {
  if (process.env.FAKE_GH_MODE === 'block-release') blockAtBoundary();
  process.exit(0);
}

const endpoint = args[3].replace('repos/owner/repo/', '');
if (process.env.FAKE_GH_MODE === 'block-pr' && endpoint === 'git/trees') {
  blockAtBoundary();
}

let response;
if (endpoint === '') response = { default_branch: 'main' };
else if (endpoint === 'git/ref/heads/main') response = { object: { sha: 'parent' } };
else if (endpoint.startsWith('git/ref/') || endpoint.startsWith('contents/')) {
  console.error('HTTP 404');
  process.exit(1);
} else if (endpoint === 'git/commits/parent') {
  response = { message: 'initial', tree: { sha: 'base' } };
} else if (endpoint === 'git/blobs') response = { sha: 'blob' };
else if (endpoint === 'git/trees') response = { sha: 'tree' };
else if (endpoint === 'git/commits') response = { sha: 'next' };
else if (endpoint === 'git/refs') response = {};
else if (endpoint.startsWith('pulls?')) response = [];
else if (endpoint === 'pulls') response = { html_url: 'https://example.invalid/pr/1' };
else {
  console.error('Unexpected ' + endpoint);
  process.exit(1);
}
console.log(JSON.stringify(response));
