import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync, readdirSync, realpathSync, lstatSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

export const toolRoot=fileURLToPath(new URL('..',import.meta.url)).replace(/[\\/]$/u,'');
export const toolPackage=JSON.parse(readFileSync(new URL('../package.json',import.meta.url),'utf8'));

/** Read the actual checkout, never the caller event SHA or an enclosing repository. */
export function checkoutCommit(root,{exactRoot=false}={}) {
  const git=args=>spawnSync('git',['-C',root,...args],{encoding:'utf8',stdio:['ignore','pipe','ignore'],timeout:5000});
  if(exactRoot){
    const top=git(['rev-parse','--show-toplevel']);
    if(top.status!==0||realpathSync(top.stdout.trim())!==realpathSync(root))return null;
  }
  const result=git(['rev-parse','--verify','HEAD']);
  const commit=result.status===0?result.stdout.trim():'';
  return /^[a-f0-9]{40,64}$/iu.test(commit)?commit:null;
}

/** Fingerprint installed executable/asset bytes when an action archive has no Git checkout. */
export function toolMetadata() {
  const digest=createHash('sha256');
  function visit(directory){
    if(!lstatSync(directory).isDirectory())throw new Error(`Unsafe shipped runtime directory: ${directory}`);
    for(const item of readdirSync(directory,{withFileTypes:true}).sort((a,b)=>a.name<b.name?-1:a.name>b.name?1:0)){
      if(['.git','node_modules'].includes(item.name))continue;
      const filename=path.join(directory,item.name),relative=path.relative(toolRoot,filename).split(path.sep).join('/');
      if(item.isDirectory())visit(filename);
      else if(item.isFile()){digest.update(relative+'\0');digest.update(readFileSync(filename));digest.update('\0');}
    }
  }
  // Hash shipped runtime roots only; consumer outputs and local caches are never identity inputs.
  for(const relative of ['cli.mjs','workflow.mjs','install.mjs','serve.mjs','prepare-quartz.mjs','package.json','package-lock.json']){
    const filename=path.join(toolRoot,relative);
    if(!lstatSync(filename).isFile())throw new Error(`Unsafe shipped runtime file: ${relative}`);
    digest.update(relative+'\0');digest.update(readFileSync(filename));digest.update('\0');
  }
  for(const relative of ['lib','assets','actions','.github/workflows'])visit(path.join(toolRoot,relative));
  const contentHash=`sha256:${digest.digest('hex')}`;
  const commit=checkoutCommit(toolRoot,{exactRoot:true});
  return {name:toolPackage.name,version:toolPackage.version,revision:commit??contentHash,commit,contentHash,actionRepository:process.env.PRODUCT_CAPTURE_ACTION_REPOSITORY??null,actionRef:process.env.PRODUCT_CAPTURE_ACTION_REF??null,workflowRevision:process.env.PRODUCT_CAPTURE_WORKFLOW_REVISION??null,workflowRepository:process.env.PRODUCT_CAPTURE_WORKFLOW_REPOSITORY??null};
}
