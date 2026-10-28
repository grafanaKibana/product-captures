#!/usr/bin/env node
import fs from 'node:fs/promises';
import {realpathSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {assertInsideReal,validateUrl} from './lib/config.mjs';

const valueFlags=new Set(['repo','id','title','url','server','prepare','revision']);

/** Parse explicit bootstrap inputs; planning is the default and overwrites are unsupported. */
export function parseInstallOptions(args) {
  const options={};
  for(let index=0;index<args.length;index++) {
    const match=/^--([a-z-]+)(?:=(.*))?$/.exec(args[index]);
    if(!match)throw new Error(`Unexpected bootstrap argument: ${args[index]}`);
    const [,name,inline]=match;
    if(Object.hasOwn(options,name))throw new Error(`Repeated bootstrap option: --${name}`);
    if(['apply','dry-run','help'].includes(name)) {
      if(inline!==undefined)throw new Error(`--${name} takes no value`);
      options[name]=true;
    } else if(valueFlags.has(name)) {
      const value=inline??args[++index];
      if(!value||value.startsWith('--'))throw new Error(`Missing value for --${name}`);
      options[name]=value;
    } else throw new Error(`Unknown bootstrap option: --${name}`);
  }
  if(options.help)return options;
  if(options.apply&&options['dry-run'])throw new Error('Choose --apply or --dry-run, not both');
  for(const key of ['repo','id','title','url'])if(!options[key])throw new Error(`Bootstrap requires --${key}`);
  if(!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(options.id))throw new Error('Project id must use lowercase words separated by hyphens');
  if(!options.title.trim()||options.title.length>300)throw new Error('Project title must contain 1–300 characters');
  const url=validateUrl(options.url,'capture URL');
  if(options.server&&!['localhost','127.0.0.1','[::1]'].includes(url.hostname))throw new Error('--server requires a loopback --url');
  options.revision??='v1';
  if(!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(options.revision)||options.revision.includes('..')||options.revision.endsWith('/')||options.revision.includes('//'))throw new Error('Invalid static toolkit revision');
  return options;
}

/** Create a validated web scene from literal caller inputs, without running supplied commands. */
export function createExampleConfig(options) {
  const config={
    version:1,
    project:{id:options.id,title:options.title,subtitle:'',backgroundStyle:'solid',artDirection:'porcelain',layout:'site-top',siteBackdrop:true,siteTint:'#e1e8e5',edgeBleed:.06,accent:'#718d77',background:'#f3f3ef'},
    outputDir:'docs/assets/product-captures',
    captures:['light','dark'].map(theme=>({id:`overview-${theme}`,provider:'web',url:options.url,viewport:{width:1440,height:900},colorScheme:theme,ready:{selector:'body'}})),
    outputs:[['hero','showcase'],['feature','feature'],['showcase','showcase']].flatMap(([id,template])=>['light','dark'].map(theme=>({id:`${id}${theme==='dark'?'-dark':''}`,template,theme,captures:[`overview-${theme}`],capturesByTheme:{light:['overview-light'],dark:['overview-dark']},width:1600,height:1000}))),
  };
  if(options.prepare)config.prepare={command:options.prepare,cwd:'.'};
  if(options.server)config.server={command:options.server,cwd:'.',url:options.url,timeoutMs:120000};
  return config;
}

/** Supply release and manual entry points while passing manual inputs as data to the shared workflow. */
export function exampleWorkflow(revision='v1') {
  return `name: Product captures

# Releases created with GITHUB_TOKEN do not trigger release workflows.
# In that case, also invoke the remote composite action inside the existing release workflow.
on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      config:
        description: Configuration path relative to the repository.
        type: string
        default: product-captures.json
      dry_run:
        description: Validate without installing, building, capturing, or publishing.
        type: boolean
        default: true
      capture_mode:
        description: Fresh product capture or explicit configured image inputs.
        type: choice
        options: [fresh, existing]
        default: fresh
      only:
        description: Comma-separated output IDs; empty renders all.
        type: string
        default: ''
      template:
        description: Optional editorial, feature, or showcase override.
        type: string
        default: ''
      theme:
        description: Optional light or dark override.
        type: string
        default: ''
      width:
        description: Optional output width.
        type: string
        default: ''
      height:
        description: Optional output height.
        type: string
        default: ''
      scale:
        description: Optional pixel density from 1 to 3.
        type: string
        default: ''
      output_dir:
        description: Optional output directory relative to the config.
        type: string
        default: ''
      skip_prepare:
        description: Reuse an explicitly prepared product.
        type: boolean
        default: false
      base_url:
        description: Optional running preview origin; skips the configured product build.
        type: string
        default: ''
      release_tag:
        description: Optional existing release to capture and attach artwork to.
        type: string
        default: ''
      update_pr:
        description: Open a review-only image update PR; never merges.
        type: boolean
        default: false

permissions:
  contents: write
  pull-requests: write

jobs:
  captures:
    uses: grafanaKibana/product-captures/.github/workflows/capture.yml@${revision}
    with:
      config: \${{ inputs.config || 'product-captures.json' }}
      dry-run: \${{ github.event_name == 'workflow_dispatch' && inputs.dry_run || false }}
      capture-mode: \${{ inputs.capture_mode || 'fresh' }}
      only: \${{ inputs.only || '' }}
      template: \${{ inputs.template || '' }}
      theme: \${{ inputs.theme || '' }}
      width: \${{ inputs.width || '' }}
      height: \${{ inputs.height || '' }}
      scale: \${{ inputs.scale || '' }}
      base-url: \${{ inputs.base_url || '' }}
      output-dir: \${{ inputs.output_dir || '' }}
      skip-prepare: \${{ inputs.skip_prepare || inputs.base_url != '' }}
      release-tag: \${{ github.event.release.tag_name || inputs.release_tag || '' }}
      update-pr: \${{ github.event_name == 'release' || inputs.update_pr || false }}
`;
}

/** Read-only PR validation keeps execution and SDK ownership in the shared repository. */
export function exampleValidationWorkflow(revision='v1') {
  return `name: Validate product captures
on:
  pull_request:
    paths:
      - product-captures.json
      - .github/workflows/product-captures*.yml
  workflow_dispatch:
permissions:
  contents: read
jobs:
  validate:
    uses: grafanaKibana/product-captures/.github/workflows/validate.yml@${revision}
    with:
      config: product-captures.json
`;
}

/** Consumer-owned instructions contain no copied runtime. */
export function exampleDocumentation(revision='v1') {
  return `# Product captures

Scenes and output selections live in product-captures.json. Review configuration commands as source code.

Clone https://github.com/grafanaKibana/product-captures once, then plan with Node 22:

\`\`\`sh
node /absolute/path/to/product-captures/cli.mjs --config /absolute/path/to/this-repo/product-captures.json --dry-run
\`\`\`

Planning needs no installed SDK. Install dependencies and Chromium once in the shared checkout for real capture. See the shared README for scene, composition, fixture, and preparation options.

The local workflow metadata calls shared capture and validation workflows at ${revision}. Manual runs default to dry-run; review output PNGs before merging generated-image PRs. Pin workflow and direct-action references to an immutable version or full SHA to freeze behavior; compatible ${revision} updates apply on rerun.

Releases created with GITHUB_TOKEN do not trigger another release workflow. In that release job, call grafanaKibana/product-captures/actions/capture@${revision} directly after successful release creation, with dry-run: 'false' and the exact release-tag. Keep only one capture invocation per release. Publishing requires contents: write; image update PRs also require pull-requests: write. Never execute untrusted PR configuration with a write token.
`;
}

/** Preflight every destination, then optionally create thin consumer configuration and remote workflow entry points. */
export async function installToolkit(args,cwd=process.cwd()) {
  const options=parseInstallOptions(args);
  if(options.help)return {help:'Usage: node install.mjs --repo PATH --id my-project --title "My project" --url URL [--server COMMAND] [--prepare COMMAND] [--revision v1] [--dry-run | --apply]\nDefault: dry-run. Identical files are preserved; conflicting config, workflow, and docs are never overwritten.'};
  const repo=await fs.realpath(path.resolve(cwd,options.repo));
  if(!(await fs.stat(repo)).isDirectory())throw new Error('--repo must be an existing directory');
  const writes=[
    {relative:'product-captures.json',content:Buffer.from(JSON.stringify(createExampleConfig(options),null,2)+'\n')},
    {relative:'.github/workflows/product-captures.yml',content:Buffer.from(exampleWorkflow(options.revision))},
    {relative:'.github/workflows/product-captures-check.yml',content:Buffer.from(exampleValidationWorkflow(options.revision))},
    {relative:'docs/product-captures.md',content:Buffer.from(exampleDocumentation(options.revision))},
  ].map(file=>({...file,mode:0o644}));
  const existing=[];
  for(const file of writes) {
    const destination=path.join(repo,file.relative);
    await assertInsideReal(repo,destination,'bootstrap destination');
    for(let ancestor=path.dirname(destination);ancestor!==repo;ancestor=path.dirname(ancestor)) {
      try {
        const stat=await fs.lstat(ancestor);
        if(stat.isSymbolicLink())throw new Error(`Refusing symlink destination ancestor: ${ancestor}`);
        if(!stat.isDirectory())throw new Error(`Destination ancestor is not a directory: ${ancestor}`);
      }catch(error){if(error.code!=='ENOENT')throw error;}
    }
    try {
      const stat=await fs.lstat(destination);
      if(!stat.isFile()||stat.isSymbolicLink()||!(await fs.readFile(destination)).equals(file.content))existing.push(file.relative);
      else file.identical=true;
    }catch(error){if(error.code!=='ENOENT')throw error;}
  }
  if(existing.length)throw new Error(`Refusing to overwrite existing paths: ${existing.join(', ')}. Review and update those files explicitly instead.`);
  const plan={version:1,dryRun:!options.apply,repo,project:options.id,files:writes.map(file=>file.relative.split(path.sep).join('/'))};
  if(!options.apply)return plan;
  const createdFiles=[];
  const createdDirectories=[];
  /** Create only missing directory components and record each for bounded rollback. */
  async function directory(absolute) {
    if(absolute===repo)return;
    await directory(path.dirname(absolute));
    try{await fs.mkdir(absolute);createdDirectories.push(absolute);}catch(error){if(error.code!=='EEXIST')throw error;if(!(await fs.stat(absolute)).isDirectory())throw new Error(`Destination ancestor is not a directory: ${absolute}`);}
    await assertInsideReal(repo,absolute,'bootstrap directory');
  }
  try {
    for(const file of writes) {
      if(file.identical)continue;
      const destination=path.join(repo,file.relative);
      await assertInsideReal(repo,destination,'bootstrap destination');
      await directory(path.dirname(destination));
      await fs.writeFile(destination,file.content,{flag:'wx',mode:file.mode});
      createdFiles.push({...file,destination});
    }
    return plan;
  } catch(error) {
    // Roll back only bytes written by this invocation; preserve anything changed concurrently.
    for(const file of createdFiles.reverse())try{if((await fs.readFile(file.destination)).equals(file.content))await fs.unlink(file.destination);}catch{}
    for(const dir of createdDirectories.reverse())try{await fs.rmdir(dir);}catch{}
    throw error;
  }
}

if(process.argv[1]&&realpathSync(process.argv[1])===fileURLToPath(import.meta.url))installToolkit(process.argv.slice(2)).then(result=>console.log(result.help??JSON.stringify(result,null,2))).catch(error=>{console.error(`product-captures bootstrap: ${error.message}`);process.exitCode=1;});
