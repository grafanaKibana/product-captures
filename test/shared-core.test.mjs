import assert from 'node:assert/strict';
import {readFileSync, mkdtempSync, rmSync, writeFileSync, symlinkSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {fileURLToPath} from 'node:url';
import {runCommand} from '../lib/process.mjs';
const root=fileURLToPath(new URL('..',import.meta.url));
test('nested action anchors all executable paths at the shared root',()=>{
  const action=readFileSync(path.join(root,'actions/capture/action.yml'),'utf8');
  assert.match(action,/\$GITHUB_ACTION_PATH\/\.\.\/\.\.\/workflow\.mjs/);
  assert.match(action,/--prefix "\$GITHUB_ACTION_PATH\/\.\.\/\.\."/);
  assert.doesNotMatch(action,/\$GITHUB_ACTION_PATH\/workflow\.mjs/);
});
test('reusable workflows bind internal action revision and keep validation read only',()=>{
  for(const name of ['capture','validate']){
    const yaml=readFileSync(path.join(root,`.github/workflows/${name}.yml`),'utf8');
    assert.match(yaml,/uses: \$\/actions\/capture/);
    assert.match(yaml,/persist-credentials: false/);
    assert.match(yaml,/repository: \$\{\{ github.repository \}\}/);
    assert.doesNotMatch(yaml,/contents: write|pull-requests: write/);
  }
  const validate=readFileSync(path.join(root,'.github/workflows/validate.yml'),'utf8');
  assert.match(validate,/dry-run: true/);
  assert.doesNotMatch(validate,/release-tag:|update-pr:|npm ci/);
});
test('preparation receives trusted module root despite an inherited override',async()=>{
  const cwd=mkdtempSync(path.join(os.tmpdir(),'capture consumer with spaces '));
  const previous=process.env.PRODUCT_CAPTURE_TOOL_ROOT;
  process.env.PRODUCT_CAPTURE_TOOL_ROOT='/untrusted inherited root';
  try{
    await runCommand({command:`"${process.execPath}" -e 'require("node:fs").writeFileSync("root.txt",process.env.PRODUCT_CAPTURE_TOOL_ROOT)'`},cwd);
    assert.equal(readFileSync(path.join(cwd,'root.txt'),'utf8'),root.replace(/\/$/,''));
  }finally{
    if(previous===undefined)delete process.env.PRODUCT_CAPTURE_TOOL_ROOT;else process.env.PRODUCT_CAPTURE_TOOL_ROOT=previous;
    rmSync(cwd,{recursive:true,force:true});
  }
});
test('CLI version is read from shared package metadata',()=>{
  const pkg=JSON.parse(readFileSync(path.join(root,'package.json')));
  assert.equal(execFileSync(process.execPath,[path.join(root,'cli.mjs'),'--version'],{encoding:'utf8'}).trim(),pkg.version);
});

test('tool identity records actual bytes and never substitutes caller event SHA',async()=>{
  const {toolMetadata,checkoutCommit}=await import('../lib/identity.mjs');
  const previous=process.env.GITHUB_SHA;
  process.env.GITHUB_SHA='f'.repeat(40);
  try{
    const tool=toolMetadata();
    assert.match(tool.contentHash,/^sha256:[a-f0-9]{64}$/u);
    assert.equal(tool.version,JSON.parse(readFileSync(path.join(root,'package.json'))).version);
    assert.equal(tool.revision,tool.commit??tool.contentHash);
    assert.notEqual(tool.revision,process.env.GITHUB_SHA);
    const consumer=mkdtempSync(path.join(os.tmpdir(),'capture actual source '));
    try{
      execFileSync('git',['init','--quiet',consumer]);
      execFileSync('git',['-C',consumer,'-c','user.name=Fixture','-c','user.email=fixture@example.test','commit','--quiet','--allow-empty','-m','fixture']);
      const actual=execFileSync('git',['-C',consumer,'rev-parse','HEAD'],{encoding:'utf8'}).trim();
      assert.equal(checkoutCommit(consumer),actual);
      const {sourceMetadata}=await import('../cli.mjs');
      assert.equal((await sourceMetadata(consumer)).commit,actual);
    }finally{rmSync(consumer,{recursive:true,force:true});}
  }finally{if(previous===undefined)delete process.env.GITHUB_SHA;else process.env.GITHUB_SHA=previous;}
});

test('source checkout has release-tag precedence and dry-run skips runtime setup',()=>{
  const yaml=readFileSync(path.join(root,'.github/workflows/capture.yml'),'utf8');
  assert.match(yaml,/ref: \$\{\{ inputs.release-tag && format\('refs\/tags\/\{0\}', inputs.release-tag\) \|\| github.sha \}\}/u);
  assert.match(yaml,/if: \$\{\{ !inputs.dry-run \}\}/u);
  assert.match(yaml,/PRODUCT_CAPTURE_WORKFLOW_REVISION: \$\{\{ job.workflow_sha \}\}/u);
  assert.doesNotMatch(yaml,/source-ref:/u);
});


test('runtime fingerprint excludes generated files and does not follow directory-entry symlinks',async()=>{
  const {toolMetadata}=await import('../lib/identity.mjs');
  const before=toolMetadata().contentHash;
  const temporary=mkdtempSync(path.join(root,'.generated-identity-test-'));
  const link=path.join(root,'lib',`.identity-link-${process.pid}`);
  try{
    const secret=path.join(temporary,'unrelated-local-file');
    writeFileSync(secret,'excluded local content');
    symlinkSync(secret,link);
    assert.equal(toolMetadata().contentHash,before);
  }finally{rmSync(link,{force:true});rmSync(temporary,{recursive:true,force:true});}
});
