#!/usr/bin/env node
import {pathToFileURL} from 'node:url';
import {loadPlan,parseOptions} from './lib/config.mjs';
import {prepareQuartzHost} from './lib/quartz-host.mjs';

export async function main(args=process.argv.slice(2)) {
  const options=parseOptions(args);
  const plan=await loadPlan(options);
  if(!plan.config.quartzHost) throw Error('Config requires quartzHost');
  if(plan.dryRun) {console.log(JSON.stringify({quartzHost:plan.config.quartzHost,root:plan.root,dryRun:true},null,2));return;}
  const receipt=await prepareQuartzHost(plan.root,plan.config.quartzHost);
  console.log(`Capture fixture built from current local source: ${receipt.publicDirectory}`);
}
if(process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) main().catch(error=>{console.error(error.message);process.exitCode=1;});
