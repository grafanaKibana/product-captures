import fs from 'node:fs/promises';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {createRequire} from 'node:module';
import {createHash} from 'node:crypto';

function object(value,label) {
  if (!value || typeof value!=='object' || Array.isArray(value)) throw Error(`${label} must be an object`);
  for (const [key,item] of Object.entries(value)) {
    if (['__proto__','prototype','constructor'].includes(key)) throw Error(`Unsafe ${label} key`);
    if (item && typeof item==='object') {
      if (Array.isArray(item)) item.forEach(entry=>{if(entry && typeof entry==='object') object(entry,label);});
      else object(item,label);
    }
  }
}
function relative(root,value,label) {
  if (typeof value!=='string' || !value || value.includes('\0') || value.includes('\\') || path.isAbsolute(value) || value.split('/').some(part=>part==='..'||part==='.'||part==='') ) throw Error(`${label} must be a contained relative path`);
  return path.join(root,value);
}
function overlaps(a,b) { return a===b || a.startsWith(b+path.sep) || b.startsWith(a+path.sep); }
async function safePath(root,target,label) {
  let current=root;
  for (const part of path.relative(root,target).split(path.sep).filter(Boolean)) {
    current=path.join(current,part);
    try { if ((await fs.lstat(current)).isSymbolicLink()) throw Error(`${label} must not contain a symlink: ${current}`); }
    catch(error) {if(error.code==='ENOENT')return;throw error;}
  }
}
async function sourceTree(file) {
  const stat=await fs.lstat(file);
  if(stat.isSymbolicLink()) throw Error(`Quartz input must not contain a symlink: ${file}`);
  if(stat.isDirectory()) for(const entry of await fs.readdir(file)) await sourceTree(path.join(file,entry));
  else if(!stat.isFile()) throw Error(`Quartz input must be a regular file: ${file}`);
}

/** Validate authored data and path boundaries without importing YAML or running commands. */
export async function validateQuartzHost(root,config) {
  object(config,'quartzHost');
  if(typeof config.upstream!=='string' || !/^https:\/\/[a-z0-9.-]+\/[a-z0-9_./-]+\.git$/i.test(config.upstream)) throw Error('quartzHost upstream must be an HTTPS Git repository');
  if(!/^[a-f0-9]{40}$/.test(config.revision??'')) throw Error('quartzHost revision must be a pinned 40-character commit');
  const cache=relative(root,config.cacheDir,'Quartz cacheDir');
  if(config.cacheDir.split('/').some(part=>['.git','node_modules'].includes(part))) throw Error('Quartz cacheDir must not use repository metadata or dependencies');
  const host=relative(cache,config.hostDir,'Quartz hostDir');
  object(config.plugin,'quartzHost plugin');
  const plugin=relative(cache,config.plugin.directory,'Quartz plugin directory');
  if(overlaps(host,plugin)) throw Error('Quartz host and plugin cache directories must be separate');
  for(const reserved of ['npm-cache','fixture.json']) for(const target of [host,plugin]) if(overlaps(target,path.join(cache,reserved))) throw Error('Quartz host/plugin paths overlap reserved cache files');
  if(!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(config.plugin.name??'')) throw Error('Quartz plugin name must be an identifier');
  if(config.plugin.order!==undefined && !Number.isInteger(config.plugin.order)) throw Error('Quartz plugin order must be an integer');
  object(config.plugin.options,'Quartz plugin options');
  if(!Array.isArray(config.plugin.buildArgs) || !config.plugin.buildArgs.length || config.plugin.buildArgs.some(arg=>typeof arg!=='string'||!arg||arg.includes('\0'))) throw Error('Quartz plugin buildArgs must be string arguments');
  if(!Array.isArray(config.plugin.inputs)||!config.plugin.inputs.length) throw Error('Quartz plugin inputs required');
  for(const required of ['package.json','package-lock.json']) if(!config.plugin.inputs.includes(required)) throw Error(`Quartz plugin inputs require ${required}`);
  const inputs=config.plugin.inputs.map(input=>relative(root,input,'Quartz plugin input'));
  for(const input of inputs) {
    if(overlaps(cache,input)) throw Error('Quartz inputs must be separate from cache');
    await safePath(root,input,'Quartz plugin input'); await sourceTree(input);
  }
  if(!Array.isArray(config.fixtures)||!config.fixtures.length) throw Error('Quartz fixtures required');
  const destinations=new Set();
  for(const fixture of config.fixtures) {
    object(fixture,'Quartz fixture');
    const source=relative(root,fixture.source,'Quartz fixture source');
    if(overlaps(cache,source)) throw Error('Quartz fixtures must be separate from cache');
    relative(path.join(host,'content'),fixture.destination,'Quartz fixture destination');
    if(destinations.has(fixture.destination)) throw Error('Duplicate Quartz fixture destination');
    destinations.add(fixture.destination);
    await safePath(root,source,'Quartz fixture source');await sourceTree(source);
    if(!(await fs.stat(source)).isFile()) throw Error('Quartz fixtures must be regular files');
  }
  object(config.configuration,'Quartz configuration');
  for(const key of ['pageTitle','baseUrl']) if(config.configuration[key]!==undefined && typeof config.configuration[key]!=='string') throw Error(`Quartz configuration ${key} must be a string`);
  if(config.configuration.enablePopovers!==undefined && typeof config.configuration.enablePopovers!=='boolean') throw Error('Quartz configuration enablePopovers must be a boolean');
  if(config.configuration.theme!==undefined) object(config.configuration.theme,'Quartz theme');
  object(config.assertions,'Quartz assertions');
  relative(host,config.assertions.file,'Quartz assertion file');
  if(!config.assertions.file.startsWith('public/')) throw Error('Quartz assertion file must be inside public');
  for(const key of ['includes','excludes']) if(!Array.isArray(config.assertions[key]) || (key==='includes'&&!config.assertions[key].length) || config.assertions[key].some(item=>typeof item!=='string'||!item)) throw Error(`Quartz assertions ${key} must be literal strings`);
  for(const key of ['route','readySelector']) if(config[key]!==undefined && (typeof config[key]!=='string'||!config[key])) throw Error(`Invalid Quartz ${key}`);
  await safePath(root,cache,'Quartz cache');
  for(const target of [host,plugin,path.join(cache,'npm-cache'),path.join(cache,'fixture.json')]) await safePath(cache,target,'Quartz cache');
  return {cache,host,plugin,inputs};
}

function run(command,args,cwd,cache,capture=false) {
  return new Promise((resolve,reject)=>{
    const child=spawn(command,args,{cwd,env:{...process.env,npm_config_cache:path.join(cache,'npm-cache')},stdio:capture?['ignore','pipe','inherit']:'inherit',shell:false});
    let output='';child.stdout?.on('data',chunk=>{output+=chunk;});
    child.once('error',reject);child.once('exit',(code,signal)=>code===0?resolve(output.trim()):reject(Error(`${command} ${args.join(' ')} failed (${signal??code})`)));
  });
}
async function exists(file) {try{await fs.lstat(file);return true;}catch(error){if(error.code==='ENOENT')return false;throw error;}}
function merge(target,overrides) {
  for(const [key,value] of Object.entries(overrides)) {
    if(value && typeof value==='object'&&!Array.isArray(value)) {if(!target[key]||typeof target[key]!=='object')target[key]={};merge(target[key],value);}
    else target[key]=value;
  }
}
const fontStyles=`
@use "./variables.scss" as *;
@font-face {
  font-family: "Capture Geist";
  src: url("/static/capture-font.ttf") format("truetype");
  font-display: block;
}
:root { --headerFont: "Capture Geist", sans-serif; --bodyFont: "Capture Geist", sans-serif; }
`;

/** Build an isolated current-source fixture using the pinned host's own locked YAML dependency. */
export async function prepareQuartzHost(root,config,runtime={}) {
  const {cache,host,plugin,inputs}=await validateQuartzHost(root,config);
  const execute=(command,args,cwd,capture=false)=>(runtime.run??run)(command,args,cwd,cache,capture);
  const npm=process.platform==='win32'?'npm.cmd':'npm';
  await fs.mkdir(cache,{recursive:true});
  await fs.rm(path.join(cache,'fixture.json'),{force:true});
  await safePath(cache,path.join(host,'.git'),'Quartz Git metadata');
  if(!await exists(path.join(host,'.git/HEAD'))) {
    await fs.mkdir(host,{recursive:true});
    if((await fs.readdir(host)).length) throw Error(`Refusing non-empty Quartz cache: ${host}`);
    await execute('git',['init','-q'],host);
    await execute('git',['remote','add','origin',config.upstream],host);
    await execute('git',['fetch','--depth','1','origin',config.revision],host);
    await execute('git',['checkout','--detach','FETCH_HEAD'],host);
  }
  if(await execute('git',['rev-parse','HEAD'],host,true)!==config.revision) throw Error('Quartz cache has a different revision');
  if(await execute('git',['remote','get-url','origin'],host,true)!==config.upstream) throw Error('Quartz cache has an unexpected origin');
  for(const item of ['node_modules','package.json','package-lock.json','quartz.config.default.yaml','quartz.config.yaml','content','public','quartz/static/capture-font.ttf','quartz/styles/custom.scss','quartz/bootstrap-cli.mjs','.quartz','quartz.lock.json']) await safePath(cache,path.join(host,item),'Quartz host');
  if(!await exists(path.join(host,'node_modules/.package-lock.json'))) await execute(npm,['ci','--no-audit','--no-fund'],host);
  const yaml=runtime.loadYaml?await runtime.loadYaml(host):createRequire(path.join(host,'package.json'))('yaml');
  await fs.rm(plugin,{recursive:true,force:true});await fs.mkdir(plugin,{recursive:true});
  const sourceHash=createHash('sha256');
  async function hashInput(file,label) {
    const stat=await fs.stat(file);sourceHash.update(label+'\0');
    if(stat.isDirectory()) for(const entry of (await fs.readdir(file)).sort()) await hashInput(path.join(file,entry),label+'/'+entry);
    else sourceHash.update(await fs.readFile(file));
  }
  for(let index=0;index<inputs.length;index++) {
    const destination=path.join(plugin,config.plugin.inputs[index]);
    await fs.mkdir(path.dirname(destination),{recursive:true});
    await fs.cp(inputs[index],destination,{recursive:true});await hashInput(inputs[index],config.plugin.inputs[index]);
  }
  await execute(npm,['ci','--no-audit','--no-fund'],plugin);
  await execute(npm,config.plugin.buildArgs,plugin);
  const settings=yaml.parse(await fs.readFile(path.join(host,'quartz.config.default.yaml'),'utf8'));
  merge(settings.configuration,config.configuration);
  settings.plugins.unshift({source:{repo:plugin,name:config.plugin.name},enabled:true,order:config.plugin.order??10,options:config.plugin.options});
  await fs.writeFile(path.join(host,'quartz.config.yaml'),yaml.stringify(settings));
  await fs.rm(path.join(host,'content'),{recursive:true,force:true});await fs.mkdir(path.join(host,'content'),{recursive:true});
  await fs.rm(path.join(host,'public'),{recursive:true,force:true});
  for(const fixture of config.fixtures) {
    const destination=path.join(host,'content',fixture.destination);
    await fs.mkdir(path.dirname(destination),{recursive:true});await fs.copyFile(path.join(root,fixture.source),destination);
  }
  await fs.mkdir(path.join(host,'quartz/static'),{recursive:true});await fs.mkdir(path.join(host,'quartz/styles'),{recursive:true});
  await fs.copyFile(new URL('../assets/Geist.ttf',import.meta.url),path.join(host,'quartz/static/capture-font.ttf'));
  await fs.writeFile(path.join(host,'quartz/styles/custom.scss'),fontStyles);
  // Recreate plugin links and lock metadata against this exact source snapshot.
  await fs.rm(path.join(host,'.quartz'),{recursive:true,force:true});
  await fs.rm(path.join(host,'quartz.lock.json'),{force:true});
  await execute(process.execPath,['quartz/bootstrap-cli.mjs','plugin','install','--from-config'],host);
  await execute(process.execPath,['quartz/bootstrap-cli.mjs','build'],host);
  const assertionFile=path.join(host,config.assertions.file);
  await safePath(cache,assertionFile,'Quartz assertion');
  const html=await fs.readFile(assertionFile,'utf8');
  for(const text of config.assertions.includes) if(!html.includes(text)) throw Error(`Quartz fixture missing expected markup: ${text}`);
  for(const text of config.assertions.excludes) if(html.includes(text)) throw Error(`Quartz fixture contains forbidden markup: ${text}`);
  const receipt={quartzRevision:config.revision,pluginSource:sourceHash.digest('hex'),publicDirectory:path.relative(root,path.join(host,'public')),route:config.route??'/',readySelector:config.readySelector};
  await fs.writeFile(path.join(cache,'fixture.json'),JSON.stringify(receipt,null,2)+'\n');
  return receipt;
}
