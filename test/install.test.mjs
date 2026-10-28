import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
import {createExampleConfig,installToolkit,parseInstallOptions} from '../install.mjs';
import {loadPlan} from '../lib/config.mjs';

const exec=promisify(execFile);
const installer=fileURLToPath(new URL('../install.mjs',import.meta.url));

/** Make an isolated repository-shaped directory without touching any real checkout. */
async function repository(t) {
  const root=await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(),'capture-install-')));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  return root;
}

/** Supply complete bootstrap flags to focus tests on one changed contract. */
function flags(root){return ['--repo',root,'--id','new-project','--title','New project','--url','https://example.com/demo'];}

test('bootstrap defaults to a zero-write plan with thin consumer files only',async t=>{
  const root=await repository(t);
  const result=await installToolkit(flags(root));
  assert.equal(result.dryRun,true);
  assert.deepEqual(await fs.readdir(root),[]);
  assert.deepEqual(result.files,['product-captures.json','.github/workflows/product-captures.yml','.github/workflows/product-captures-check.yml','docs/product-captures.md']);
  assert.ok(result.files.includes('product-captures.json'));
  assert.ok(result.files.includes('.github/workflows/product-captures.yml'));
  assert.equal(result.files.some(file=>file.includes('node_modules')),false);
  const {stdout}=await exec(process.execPath,[installer,...flags(root),'--dry-run'],{timeout:5000});
  assert.equal(JSON.parse(stdout).dryRun,true);
  assert.deepEqual(await fs.readdir(root),[]);
});

test('bootstrap preflights all destinations and refuses to overwrite before creating anything',async t=>{
  for(const existing of ['product-captures.json','.github/workflows/product-captures-check.yml','.github/workflows/product-captures.yml','docs/product-captures.md']) {
    const root=await repository(t);
    const filename=path.join(root,existing);
    await fs.mkdir(path.dirname(filename),{recursive:true});
    if(existing.endsWith('product-captures'))await fs.mkdir(filename);else await fs.writeFile(filename,'preserve me');
    await assert.rejects(installToolkit([...flags(root),'--apply']),/Refusing to overwrite/);
    if(!existing.endsWith('product-captures'))assert.equal(await fs.readFile(filename,'utf8'),'preserve me');
    if(existing!=='product-captures.json')await assert.rejects(fs.stat(path.join(root,'product-captures.json')),error=>error.code==='ENOENT');
    if(existing!=='.github/actions/product-captures')await assert.rejects(fs.stat(path.join(root,'.github/actions/product-captures')),error=>error.code==='ENOENT');
  }
});

test('applied bootstrap works from a moved path with spaces and keeps caller strings literal',async t=>{
  const root=await repository(t);
  const target=path.join(root,'a new repo');
  await fs.mkdir(target);
  const title='A "quoted" title\n$(touch SHOULD-NOT-EXIST)';
  const args=flags(target);
  args[args.indexOf('--title')+1]=title;
  const command='node -e "console.log(\'$(literal)\')"';
  await installToolkit([...args,'--prepare',command,'--apply']);
  const config=JSON.parse(await fs.readFile(path.join(target,'product-captures.json'),'utf8'));
  assert.equal(config.project.title,title);
  assert.equal(config.prepare.command,command);
  const moved=path.join(root,'moved repo');
  await fs.rename(target,moved);
  const cli=fileURLToPath(new URL('../cli.mjs',import.meta.url));
  const {stdout}=await exec(process.execPath,[cli,'--dry-run'],{cwd:moved,timeout:5000});
  const plan=JSON.parse(stdout);
  assert.equal(plan.project,'new-project');
  assert.equal(plan.outputs.length,6);
  assert.equal(plan.outputDirectory,path.join(moved,'docs/assets/product-captures'));
  assert.deepEqual(new Set(plan.outputs.map(output=>output.theme)),new Set(['light','dark']));
  assert.equal(plan.captures.length,2);
  await assert.rejects(fs.stat(path.join(moved,'.github/actions')),error=>error.code==='ENOENT');
  const workflow=await fs.readFile(path.join(moved,'.github/workflows/product-captures.yml'),'utf8');
  assert.match(workflow,/default: true/);
  assert.match(workflow,/types: \[published\]/);
  assert.match(workflow,/uses: grafanaKibana\/product-captures\/\.github\/workflows\/capture.yml@v1/);
  assert.equal(workflow.includes(title),false);
  assert.equal((await fs.readdir(moved)).includes('SHOULD-NOT-EXIST'),false);
});

test('bootstrap validates required values and rejects symlinked destination ancestors',async t=>{
  for(const args of [[],['--force'],['--apply','--dry-run'],['--id','../bad']])assert.throws(()=>parseInstallOptions(args));
  const root=await repository(t);
  const outside=await repository(t);
  await fs.symlink(outside,path.join(root,'.github'));
  await assert.rejects(installToolkit([...flags(root),'--apply']),/symlink must stay inside/);
  assert.deepEqual(await fs.readdir(outside),[]);
  assert.deepEqual(await fs.readdir(root),['.github']);
  assert.throws(()=>parseInstallOptions([...flags(root),'--server','npm run dev']),/loopback/);
  assert.throws(()=>parseInstallOptions(flags(root).map(value=>value==='https://example.com/demo'?'file:///tmp':value)),/HTTP/);
});

test('example config and supplied localhost server commands satisfy core planning',async t=>{
  const root=await repository(t);
  const options=parseInstallOptions(['--repo',root,'--id','site','--title','Site','--url','http://127.0.0.1:4179','--server','npm run dev -- --port 4179','--prepare','npm ci && npm run build']);
  const config=createExampleConfig(options);
  await fs.writeFile(path.join(root,'product-captures.json'),JSON.stringify(config));
  const plan=await loadPlan({'dry-run':true},root);
  assert.equal(plan.captures[0].resolvedUrl,'http://127.0.0.1:4179/');
  assert.equal(plan.config.server.command,options.server);
  const example=JSON.parse(await fs.readFile(new URL('../example.product-captures.json',import.meta.url),'utf8'));
  await fs.writeFile(path.join(root,'product-captures.json'),JSON.stringify(example));
  assert.equal((await loadPlan({'dry-run':true},root)).outputs.length,6);
});

test('CLI, installer, and action entrypoints run through symlinked paths and emit actual plans',async t=>{
  const root=await repository(t);
  const alias=path.join(root,'toolkit link');
  await fs.symlink(fileURLToPath(new URL('..',import.meta.url)),alias,process.platform==='win32'?'junction':'dir');
  const target=path.join(root,'new repository');
  await fs.mkdir(target);
  const config=createExampleConfig(parseInstallOptions(flags(root)));
  await fs.writeFile(path.join(root,'product-captures.json'),JSON.stringify(config));
  const {stdout:cliOutput}=await exec(process.execPath,[path.join(alias,'cli.mjs'),'--dry-run'],{cwd:root,timeout:5000});
  assert.equal(JSON.parse(cliOutput).project,'new-project');
  assert.equal(JSON.parse(cliOutput).outputs.length,6);
  const {stdout:installOutput}=await exec(process.execPath,[path.join(alias,'install.mjs'),...flags(target)],{cwd:root,timeout:5000});
  assert.equal(JSON.parse(installOutput).dryRun,true);
  assert.ok(JSON.parse(installOutput).files.includes('product-captures.json'));
  assert.deepEqual(await fs.readdir(target),[]);
  const {stdout:actionOutput}=await exec(process.execPath,[path.join(alias,'workflow.mjs'),'plan'],{cwd:root,timeout:5000,env:{...process.env,CAPTURE_CONFIG:'product-captures.json',CAPTURE_DRY_RUN:'true',GITHUB_OUTPUT:''}});
  assert.equal(JSON.parse(actionOutput).project,'new-project');
  assert.equal(JSON.parse(actionOutput).dryRun,true);
});


test('repeated installs preserve identical files and preflight a later conflict before writes',async t=>{
  const root=await repository(t);
  const first=await installToolkit([...flags(root),'--apply']);
  const before=await Promise.all(first.files.map(file=>fs.readFile(path.join(root,file),'utf8')));
  const second=await installToolkit([...flags(root),'--apply']);
  assert.deepEqual(second.files,first.files);
  assert.deepEqual(await Promise.all(first.files.map(file=>fs.readFile(path.join(root,file),'utf8'))),before);
  await fs.unlink(path.join(root,'product-captures.json'));
  await fs.writeFile(path.join(root,'docs/product-captures.md'),'authored content');
  await assert.rejects(installToolkit([...flags(root),'--apply']),/Refusing to overwrite/);
  await assert.rejects(fs.stat(path.join(root,'product-captures.json')),error=>error.code==='ENOENT');
});

test('installer validates static remote revisions and exposes all manual controls',async t=>{
  const root=await repository(t);
  for(const revision of ['main\n  jobs: bad','${{ github.ref }}','v1:bad','../main'])assert.throws(()=>parseInstallOptions([...flags(root),'--revision',revision]),/revision/);
  await installToolkit([...flags(root),'--revision','v1.0.0','--apply']);
  const workflow=await fs.readFile(path.join(root,'.github/workflows/product-captures.yml'),'utf8');
  assert.match(workflow,/capture.yml@v1.0.0/);
  for(const name of ['config','scale','output_dir','skip_prepare','dry_run','capture_mode','only','template','theme','width','height','base_url','release_tag','update_pr'])assert.ok(workflow.includes(`      ${name}:`),name);
  assert.equal(workflow.includes('runs-on:'),false);
  assert.equal(workflow.includes('steps:'),false);
  const validation=await fs.readFile(path.join(root,'.github/workflows/product-captures-check.yml'),'utf8');
  assert.match(validation,/validate.yml@v1.0.0/);
  assert.match(validation,/contents: read/);
});


test('preflight rejects dangling symlink and non-directory ancestors before creating config',async t=>{
  for(const kind of ['dangling','file','symlink-file']) {
    const root=await repository(t);
    if(kind==='dangling')await fs.symlink(path.join(root,'missing'),path.join(root,'.github'));
    if(kind==='file')await fs.writeFile(path.join(root,'.github'),'keep');
    if(kind==='symlink-file') {
      const target=path.join(root,'authored.json');
      await fs.writeFile(target,'keep');
      await fs.symlink(target,path.join(root,'product-captures.json'));
    }
    await assert.rejects(installToolkit([...flags(root),'--apply']));
    if(kind!=='symlink-file')await assert.rejects(fs.lstat(path.join(root,'product-captures.json')),error=>error.code==='ENOENT');
    else assert.equal(await fs.readFile(path.join(root,'authored.json'),'utf8'),'keep');
  }
});
