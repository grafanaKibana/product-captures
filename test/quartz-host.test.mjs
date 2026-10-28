import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {validateQuartzHost, prepareQuartzHost} from '../lib/quartz-host.mjs';

async function fixture(t) {
  const root=await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(),'quartz-host-')));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  await fs.mkdir(path.join(root,'src'));
  for (const file of ['src/index.ts','package.json','package-lock.json','note.md']) await fs.writeFile(path.join(root,file),'{}');
  return {root,config:{upstream:'https://github.com/jackyzha0/quartz.git',revision:'a'.repeat(40),cacheDir:'.cache',hostDir:'quartz',plugin:{directory:'plugin',name:'example',inputs:['src','package.json','package-lock.json'],buildArgs:['run','build'],options:{}},fixtures:[{source:'note.md',destination:'index.md'}],configuration:{pageTitle:'Example'},assertions:{file:'public/index.html',includes:['example-tab'],excludes:['diagnostic']}}};
}

test('valid declaration plans without commands or installed YAML',async t=>{
  const {root,config}=await fixture(t);
  const result=await validateQuartzHost(root,config);
  assert.equal(result.host,path.join(root,'.cache/quartz'));
  assert.deepEqual(await fs.readdir(root),['note.md','package-lock.json','package.json','src']);
});

test('reject malformed declarations, traversal and overlapping inputs',async t=>{
  const {root,config}=await fixture(t);
  for (const mutate of [c=>c.revision='main',c=>c.upstream='file:///tmp/host',c=>c.cacheDir='.',c=>c.cacheDir='../cache',c=>c.cacheDir='.git/cache',c=>c.plugin.directory='npm-cache',c=>c.hostDir='../host',c=>c.plugin.directory=c.hostDir,c=>c.plugin.inputs.push('.'),c=>c.plugin.inputs.push('.cache'),c=>c.fixtures[0].destination='../escape',c=>c.configuration=[],c=>c.configuration.theme='invalid',c=>c.configuration.enablePopovers='yes',c=>c.plugin.options=[],c=>c.assertions.includes=[],c=>c.plugin.buildArgs='build',c=>c.fixtures[0].source='missing.md']) {
    const value=structuredClone(config);mutate(value);
    await assert.rejects(()=>validateQuartzHost(root,value));
  }
});

test('reject source and cache symlinks before side effects',async t=>{
  const {root,config}=await fixture(t);
  await fs.symlink(os.tmpdir(),path.join(root,'src/escape'));
  await assert.rejects(()=>validateQuartzHost(root,config),/symlink/);
  await fs.rm(path.join(root,'src/escape'));
  await fs.symlink(os.tmpdir(),path.join(root,'.cache'));
  await assert.rejects(()=>validateQuartzHost(root,config),/symlink/);
});

async function fakeHost(t, overrides={}) {
  const data=await fixture(t), {root,config}=data;
  const host=path.join(root,'.cache/quartz');
  await fs.mkdir(path.join(host,'.git'),{recursive:true});
  await fs.writeFile(path.join(host,'.git/HEAD'),'mock');
  await fs.mkdir(path.join(host,'node_modules'),{recursive:true});
  await fs.writeFile(path.join(host,'node_modules/.package-lock.json'),'{}');
  const calls=[];
  const run=async(command,args,cwd)=>{
    calls.push({command,args,cwd});
    if(args[0]==='rev-parse') return overrides.revision??config.revision;
    if(args[0]==='remote') return overrides.upstream??config.upstream;
    if(args[0]==='ci' && overrides.failInstall) throw Error('install failed');
    if(args[0]==='quartz/bootstrap-cli.mjs' && args[1]==='build') {
      if(overrides.failBuild) throw Error('build failed');
      await fs.mkdir(path.join(host,'public'),{recursive:true});
      await fs.writeFile(path.join(host,'public/index.html'),overrides.html??'example-tab');
    }
    return '';
  };
  await fs.writeFile(path.join(host,'quartz.config.default.yaml'),'{}');
  return {...data,host,calls,runtime:{run,loadYaml:()=>({parse:()=>({configuration:{theme:{}} ,plugins:[]}),stringify:JSON.stringify})}};
}

test('reject wrong cached origin/revision before installing or staging',async t=>{
  for (const overrides of [{revision:'b'.repeat(40)},{upstream:'https://github.com/other/quartz.git'}]) {
    const data=await fakeHost(t,overrides);
    await assert.rejects(()=>prepareQuartzHost(data.root,data.config,data.runtime),/cache/);
    assert.ok(data.calls.every(call=>call.command==='git'));
  }
});

test('fresh staging removes deleted sources/content and isolates installs',async t=>{
  const data=await fakeHost(t),{root,host,config,runtime,calls}=data;
  await fs.mkdir(path.join(root,'.cache/plugin/stale'),{recursive:true});
  await fs.mkdir(path.join(host,'content'),{recursive:true});
  await fs.writeFile(path.join(host,'content/stale.md'),'stale');
  await fs.mkdir(path.join(host,'.quartz/plugins/stale'),{recursive:true});
  await fs.writeFile(path.join(host,'quartz.lock.json'),'stale lock');
  const before=await fs.readFile(path.join(root,'package-lock.json'));
  await prepareQuartzHost(root,config,runtime);
  assert.deepEqual(await fs.readdir(path.join(host,'content')),['index.md']);
  await assert.rejects(fs.stat(path.join(host,'.quartz/plugins/stale')),/ENOENT/);
  await assert.rejects(fs.stat(path.join(host,'quartz.lock.json')),/ENOENT/);
  await assert.rejects(fs.stat(path.join(root,'.cache/plugin/stale')),/ENOENT/);
  assert.deepEqual(await fs.readFile(path.join(root,'package-lock.json')),before);
  assert.ok(calls.filter(c=>c.args[0]==='ci').every(c=>c.cwd!==root));
  assert.deepEqual(await fs.readFile(path.join(host,'quartz/static/capture-font.ttf')),await fs.readFile(new URL('../assets/Geist.ttf',import.meta.url)));
});

test('install, build and markup failures propagate without success receipt',async t=>{
  for (const overrides of [{failInstall:true},{failBuild:true},{html:'missing'},{html:'example-tab diagnostic'}]) {
    const {root,config,runtime}=await fakeHost(t,overrides);
    await assert.rejects(()=>prepareQuartzHost(root,config,runtime));
    await assert.rejects(fs.stat(path.join(root,'.cache/fixture.json')),/ENOENT/);
  }
});

test('planning validates Quartz declarations through shared CLI without SDKs or writes',async t=>{
  const {root,config}=await fixture(t);
  const {execFile}=await import('node:child_process');
  const {promisify}=await import('node:util');
  const {fileURLToPath}=await import('node:url');
  const captureConfig={version:1,project:{id:'example',title:'Example'},captures:[],outputs:[{id:'hero',template:'showcase',showProduct:false,captures:[]}],quartzHost:config};
  await fs.writeFile(path.join(root,'product-captures.json'),JSON.stringify(captureConfig));
  const before=await fs.readdir(root);
  const cli=fileURLToPath(new URL('../prepare-quartz.mjs',import.meta.url));
  const {stdout}=await promisify(execFile)(process.execPath,[cli,'--dry-run'],{cwd:root,timeout:5000});
  assert.equal(JSON.parse(stdout).dryRun,true);
  assert.deepEqual(await fs.readdir(root),before);
  captureConfig.quartzHost.revision='main';
  await fs.writeFile(path.join(root,'product-captures.json'),JSON.stringify(captureConfig));
  await assert.rejects(promisify(execFile)(process.execPath,[cli,'--dry-run'],{cwd:root,timeout:5000}),/pinned/);
});

test('runtime rejects host write symlinks and nonempty uninitialized cache',async t=>{
  const data=await fakeHost(t);
  await fs.symlink(os.tmpdir(),path.join(data.host,'public'));
  await assert.rejects(()=>prepareQuartzHost(data.root,data.config,data.runtime),/symlink/);
  assert.ok(data.calls.every(call=>call.command==='git'));
  const fresh=await fixture(t);
  await fs.mkdir(path.join(fresh.root,'.cache/quartz'),{recursive:true});
  await fs.writeFile(path.join(fresh.root,'.cache/quartz/foreign'),'preserve');
  await assert.rejects(()=>prepareQuartzHost(fresh.root,fresh.config,{run:()=>{throw Error('must not run');}}),/non-empty/);
  assert.equal(await fs.readFile(path.join(fresh.root,'.cache/quartz/foreign'),'utf8'),'preserve');
});
