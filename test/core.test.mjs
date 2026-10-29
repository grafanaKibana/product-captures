import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,readFileSync,readdirSync,mkdirSync,symlinkSync,rmSync,existsSync,chmodSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {once} from 'node:events';
import {spawn,spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const tool=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dll=path.join(tool,'bin/Debug/net10.0/ProductCaptures.dll');
function fixture(config={name:'Example',capture:null}){const root=mkdtempSync(path.join(tmpdir(),'capture-core-'));writeFileSync(path.join(root,'product-captures.json'),JSON.stringify(config));return root;}
function run(root,args=[],env={}){return spawnSync('dotnet',[dll,'--config',path.join(root,'product-captures.json'),...args],{cwd:tmpdir(),encoding:'utf8',env:{...process.env,...env}});}
function cleanup(t,root){t.after(()=>rmSync(root,{recursive:true,force:true}));}
test('dry-run validates absolute config from unrelated cwd without consumer writes',t=>{const root=fixture();cleanup(t,root);const before=readdirSync(root);const r=run(root,['--dry-run']);assert.equal(r.status,0,r.stderr);assert.deepEqual(readdirSync(root),before);assert.deepEqual(JSON.parse(r.stdout).files,['hero.png','hero-dark.png']);});
for(const [label,config] of Object.entries({unknown:{name:'Example',density:2},traversal:{name:'Example',output:'../escape'},root:{name:'Example',output:'.'},badlayout:{name:'Example',appearance:{layout:'matrix'}},zero:{name:'Example',appearance:{width:0}},nullappearance:{name:'Example',appearance:null},badcapture:{name:'Example',capture:{type:'fallback'}},badstorage:{name:'Example',capture:{storage:{theme:{light:'light'}}}},badaction:{name:'Example',capture:{actions:[{type:'eval',selector:'body'}]}},badbuild:{name:'Example',build:{command:'touch no',extra:true}},badserver:{name:'Example',server:{command:'echo ok'}},badcolor:{name:'Example',appearance:{background:'url(bad)'}},badzoom:{name:'Example',appearance:{imageZoom:0}},badfade:{name:'Example',appearance:{fadeStart:0.8,fadeEnd:0.2}},badversion:{name:'Example',capture:{type:'obsidian',appVersion:'latest'}},internalkey:{name:'Example',root:'/tmp'},badurl:{name:'Example',capture:null}}))test('rejects '+label+' before consumer writes',t=>{const root=fixture(config);cleanup(t,root);const before=readdirSync(root);const r=run(root,['--dry-run',...(label==='badurl'?['--url','file:///etc/passwd']:[])]);assert.notEqual(r.status,0);assert.deepEqual(readdirSync(root),before);});
test('rejects unknown and duplicate CLI flags',t=>{const root=fixture();cleanup(t,root);for(const args of [['--scale','2'],['--dry-run','--dry-run'],['--url']])assert.notEqual(run(root,args).status,0);});
test('configuration reports the established first error before commands or consumer writes', t => {
  const cases = [
    {
      name:'required name before appearance',
      config:{name:'', appearance:{layout:'matrix',width:0}, capture:{url:'file:///invalid'}},
      error:'name is required',
    },
    {
      name:'nullable output before required name',
      config:{name:'', output:null, appearance:{layout:'top',width:0}},
      error:'subtitle and output cannot be null',
    },
    {
      name:'layout before dimensions',
      config:{appearance:{layout:'matrix',width:0}, capture:{url:'file:///invalid'}},
      error:'Unknown appearance.layout',
    },
    {
      name:'dimensions before colors',
      config:{appearance:{width:0,background:'invalid'}, capture:{url:'file:///invalid'}},
      error:'Invalid artwork dimensions',
    },
    {
      name:'colors before image settings',
      config:{appearance:{background:'invalid',imageZoom:0}, capture:{url:'file:///invalid'}},
      error:'Appearance colors must be hexadecimal CSS colors',
    },
    {
      name:'effective fade default before capture',
      config:{appearance:{fadeEnd:0.2}, capture:{url:'file:///invalid',type:'unknown'}},
      error:'fadeStart must not exceed fadeEnd',
    },
    {
      name:'capture URL before provider',
      config:{capture:{url:'file:///invalid',type:'unknown',viewport:{width:0}}},
      error:'capture.url must be relative or HTTP',
    },
    {
      name:'provider before viewport',
      config:{capture:{type:'unknown',viewport:{width:0},storage:{mode:{light:'day'}}}},
      error:'Unknown capture.type',
    },
    {
      name:'viewport before storage',
      config:{capture:{viewport:{width:0},storage:{mode:{light:'day'}}}},
      error:'Invalid capture.viewport',
    },
    {
      name:'storage before actions',
      config:{capture:{storage:{mode:{light:'day'}},actions:[{type:'eval'}]}},
      error:'storage values must be strings or {light,dark}',
    },
    {
      name:'build before server',
      config:{capture:null,build:{command:'touch should-not-build',timeoutMs:0},server:{directory:'.',port:0}},
      error:'Invalid build command or timeout',
    },
  ];
  for (const entry of cases) {
    const root = fixture({name:'Order', build:'touch should-not-build', ...entry.config});
    cleanup(t, root);
    const before = readdirSync(root);
    const result = run(root);
    assert.equal(result.status, 1, entry.name);
    assert.equal(result.stderr.trim(), entry.error, entry.name);
    assert.equal(result.stdout, '', entry.name);
    assert.deepEqual(readdirSync(root), before, entry.name);
  }
});
test('CLI option diagnostics keep their established precedence before configuration validation', t => {
  const root = fixture({name:'',build:'touch should-not-build'});
  cleanup(t, root);
  const before = readdirSync(root);
  for (const [arguments_, expected] of [
    [['--unknown','--unknown'], 'Unknown option: --unknown'],
    [['--dry-run','--dry-run'], 'Duplicate option: --dry-run'],
    [['--url','--dry-run'], 'Missing value for --url'],
    [['--config','--dry-run'], 'Duplicate option: --config'],
  ]) {
    const result = run(root, arguments_);
    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), expected);
    assert.equal(result.stdout, '');
    assert.deepEqual(readdirSync(root), before);
  }
});
test('rejects symlink output before writes',t=>{const root=fixture({name:'Example',output:'images'});cleanup(t,root);symlinkSync(tmpdir(),path.join(root,'images'));assert.notEqual(run(root,['--dry-run']).status,0);});
test('fresh build failure preserves saved PNGs and does not silently reuse',t=>{const root=fixture({name:'Example',build:'exit 23'});cleanup(t,root);const out=path.join(root,'docs/assets/product-captures/captures');mkdirSync(out,{recursive:true});for(const theme of ['light','dark'])writeFileSync(path.join(out,'overview-'+theme+'.png'),'previous');const r=run(root);assert.notEqual(r.status,0);assert.match(r.stderr,/exited 23/);assert.equal(readFileSync(path.join(out,'overview-light.png'),'utf8'),'previous');assert(!existsSync(path.join(out,'../manifest.json')));});
test('dry-run never starts configured product commands',t=>{const root=fixture({name:'Example',build:'touch should-not-exist',server:{command:'touch server-started',url:'http://127.0.0.1:4180'}});cleanup(t,root);const r=run(root,['--dry-run']);assert.equal(r.status,0,r.stderr);assert(!existsSync(path.join(root,'should-not-exist')));assert(!existsSync(path.join(root,'server-started')));assert.equal(JSON.parse(r.stdout).files.length,4);});
test('reuse requires both valid existing PNGs',t=>{const root=fixture();cleanup(t,root);writeFileSync(path.join(root,'product-captures.json'),JSON.stringify({name:'Example'}));assert.notEqual(run(root,['--dry-run','--reuse-screenshots']).status,0);});
test('action plan emits six-input outputs while remaining dry',t=>{const root=fixture();cleanup(t,root);const output=path.join(root,'output');const r=spawnSync('dotnet',[dll,'action','plan'],{cwd:root,encoding:'utf8',env:{...process.env,CAPTURE_CONFIG:path.join(root,'product-captures.json'),GITHUB_WORKSPACE:root,GITHUB_OUTPUT:output}});assert.equal(r.status,0,r.stderr);assert.match(readFileSync(output,'utf8'),/needs-obsidian=false\nneeds-node=false\ndry-run=true/);assert(!existsSync(path.join(root,'docs')));});
test('action boolean values are strict',t=>{const root=fixture();cleanup(t,root);const r=spawnSync('dotnet',[dll,'action','plan'],{cwd:root,encoding:'utf8',env:{...process.env,CAPTURE_CONFIG:path.join(root,'product-captures.json'),CAPTURE_DRY_RUN:'yes'}});assert.notEqual(r.status,0);assert.match(r.stderr,/true or false/);});
const pixel=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4N0AAAAASUVORK5CYII=','base64');
test('action plan preserves exact output and prerequisites for fresh, preview, reuse and source-free runs', t => {
  const cases = [
    {name:'source-free', config:{capture:null}, obsidian:false, node:false},
    {name:'web', config:{}, obsidian:false, node:false},
    {name:'web build', config:{build:'touch should-not-build'}, obsidian:false, node:true},
    {name:'preview build', config:{build:'touch should-not-build'}, env:{CAPTURE_URL:'https://preview.example'}, obsidian:false, node:false},
    {name:'obsidian', config:{capture:{type:'obsidian'}}, obsidian:true, node:true},
    {name:'reused obsidian', config:{capture:{type:'obsidian'}}, env:{CAPTURE_REUSE_SCREENSHOTS:'true'}, obsidian:false, node:false},
    {name:'explicit capture', config:{}, env:{CAPTURE_DRY_RUN:'false'}, obsidian:false, node:false, dryRun:false},
  ];
  for (const entry of cases) {
    const root = fixture({name:entry.name, ...entry.config});
    cleanup(t, root);
    const captures = path.join(root, 'docs/assets/product-captures/captures');
    if (entry.env?.CAPTURE_REUSE_SCREENSHOTS === 'true') {
      mkdirSync(captures, {recursive:true});
      for (const theme of ['light', 'dark']) writeFileSync(path.join(captures, `overview-${theme}.png`), pixel);
    }
    const output = path.join(root, 'action-output');
    writeFileSync(output, 'existing=value\n');
    const result = spawnSync('dotnet', [dll, 'action', 'plan'], {
      cwd:root, encoding:'utf8', env:{
        ...process.env, CAPTURE_CONFIG:path.join(root, 'product-captures.json'),
        CAPTURE_DRY_RUN:'', CAPTURE_REUSE_SCREENSHOTS:'', CAPTURE_URL:'',
        CAPTURE_RELEASE_TAG:'', CAPTURE_UPDATE_PR:'', ...entry.env,
        GITHUB_WORKSPACE:root, GITHUB_OUTPUT:output,
      },
    });
    assert.equal(result.status, 0, `${entry.name}: ${result.stderr}`);
    assert.equal(readFileSync(output, 'utf8'), 'existing=value\n' +
      'artifact-path=docs/assets/product-captures\n' +
      `needs-obsidian=${entry.obsidian}\nneeds-node=${entry.node}\ndry-run=${entry.dryRun ?? true}\n`);
    assert.equal(JSON.parse(result.stdout).dryRun, true);
    assert(!existsSync(path.join(root, 'should-not-build')));
    assert(!existsSync(path.join(root, 'docs/assets/product-captures/hero.png')));
  }
});
test('action environment rejects invalid booleans in the established order before config access', t => {
  const root = fixture();
  cleanup(t, root);
  for (const [dryRun, reuse, expected] of [['invalid','invalid','DRY_RUN'], ['true','invalid','REUSE_SCREENSHOTS'], ['true','false','UPDATE_PR']]) {
    const result = spawnSync('dotnet', [dll, 'action', 'plan'], {
      cwd:root, encoding:'utf8', env:{
        ...process.env, CAPTURE_CONFIG:path.join(root, 'missing.json'),
        CAPTURE_DRY_RUN:dryRun, CAPTURE_REUSE_SCREENSHOTS:reuse,
        CAPTURE_URL:'https://preview.example', CAPTURE_RELEASE_TAG:'v1.0.0', CAPTURE_UPDATE_PR:'invalid',
      },
    });
    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), `CAPTURE_${expected} must be true or false`);
  }
});
function pngs(root){return readdirSync(root,{recursive:true}).filter(p=>p.endsWith('.png')).sort();}
test('real Chromium creates exactly four fixed1x PNGs; explicit reuse preserves raw bytes',t=>{const root=fixture({name:'Example',appearance:{width:400,height:300},capture:{type:'image',images:{light:'light.png',dark:'dark.png'}}});cleanup(t,root);for(const theme of ['light','dark'])writeFileSync(path.join(root,theme+'.png'),pixel);let r=run(root);assert.equal(r.status,0,r.stderr);const out=path.join(root,'docs/assets/product-captures');assert.deepEqual(pngs(out),['captures/overview-dark.png','captures/overview-light.png','hero-dark.png','hero.png']);assert.equal(readFileSync(path.join(out,'hero.png')).readUInt32BE(16),400);assert.equal(readFileSync(path.join(out,'hero.png')).readUInt32BE(20),300);assert.deepEqual(readFileSync(path.join(out,'captures/overview-light.png')),pixel);rmSync(path.join(root,'light.png'));rmSync(path.join(root,'dark.png'));r=run(root,['--reuse-screenshots']);assert.equal(r.status,0,r.stderr);assert.deepEqual(readFileSync(path.join(out,'captures/overview-light.png')),pixel);});
test('real source-free render creates only finished light/dark pair',t=>{const root=fixture({name:'Example',capture:null,appearance:{width:400,height:300}});cleanup(t,root);const r=run(root);assert.equal(r.status,0,r.stderr);assert.deepEqual(pngs(path.join(root,'docs/assets/product-captures')),['hero-dark.png','hero.png']);});
const versionResult=spawnSync('dotnet',[dll,'--version'],{encoding:'utf8'});
assert.equal(versionResult.status,0,versionResult.stderr);
const toolVersion=versionResult.stdout.trim();
assert.match(toolVersion,/^\d+\.\d+\.\d+$/);
function publication(t){const root=fixture({name:'Example'});cleanup(t,root);const bin=path.join(root,'bin');mkdirSync(bin);const git=spawnSync('git',['init','-q'],{cwd:root});assert.equal(git.status,0);assert.equal(spawnSync('git',['-c','user.name=Test','-c','user.email=test@example.invalid','commit','--allow-empty','-qm','test'],{cwd:root}).status,0);const source=spawnSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).stdout.trim();const files=['captures/overview-light.png','captures/overview-dark.png','hero.png','hero-dark.png'];const out=path.join(root,'docs/assets/product-captures');mkdirSync(path.join(out,'captures'),{recursive:true});for(const file of files)writeFileSync(path.join(out,file),pixel);writeFileSync(path.join(out,'manifest.json'),JSON.stringify({version:2,tool:toolVersion,configHash:createHash('sha256').update(readFileSync(path.join(root,'product-captures.json'))).digest('hex'),mode:'fresh',source,outputs:files}));writeFileSync(path.join(bin,'gh'),`#!/usr/bin/env node
const fs=require('fs');const args=process.argv.slice(2);const input=args.includes('--input')?JSON.parse(fs.readFileSync(0,'utf8')):null;fs.appendFileSync(process.env.FAKE_GH_LOG,JSON.stringify({args,input})+'\\n');if(args[0]==='release')process.exit(0);const e=args[3].replace('repos/owner/repo/','');let r;if(e==='')r={default_branch:'main'};else if(e==='git/ref/heads/main')r={object:{sha:'parent'}};else if(e.startsWith('git/ref/')||e.startsWith('contents/')){console.error('HTTP 404');process.exit(1)}else if(e==='git/commits/parent')r={message:'initial',tree:{sha:'base'}};else if(e==='git/blobs')r={sha:'blob'};else if(e==='git/trees')r={sha:'tree'};else if(e==='git/commits')r={sha:'next'};else if(e==='git/refs')r={};else if(e.startsWith('pulls?'))r=[];else if(e==='pulls')r={html_url:'https://example.invalid/pr/1'};else{console.error('Unexpected '+e);process.exit(1)}console.log(JSON.stringify(r));
`);chmodSync(path.join(bin,'gh'),0o755);return {root,out,files,env:{...process.env,PATH:bin+path.delimiter+process.env.PATH,CAPTURE_CONFIG:path.join(root,'product-captures.json'),CAPTURE_DRY_RUN:'false',GITHUB_WORKSPACE:root,GITHUB_REPOSITORY:'owner/repo',FAKE_GH_LOG:path.join(root,'gh.log')}};}
test('fake GitHub publication PR contains exactly four owned PNGs without touching index or checkout',t=>{const f=publication(t);writeFileSync(path.join(f.root,'unrelated.txt'),'preserve');spawnSync('git',['add','--','unrelated.txt'],{cwd:f.root});const indexBefore=readFileSync(path.join(f.root,'.git/index'));const before=spawnSync('git',['status','--porcelain'],{cwd:f.root,encoding:'utf8'}).stdout;const r=spawnSync('dotnet',[dll,'action','pr'],{cwd:f.root,encoding:'utf8',env:{...f.env,CAPTURE_UPDATE_PR:'true'}});assert.equal(r.status,0,r.stderr);const calls=readFileSync(f.env.FAKE_GH_LOG,'utf8').trim().split('\n').map(JSON.parse);const tree=calls.find(x=>x.args[3]?.endsWith('/git/trees'));assert.deepEqual(tree.input.tree.map(x=>x.path),f.files.map(x=>'docs/assets/product-captures/'+x));assert(!calls.some(x=>x.args[0]!=='api'));const after=spawnSync('git',['status','--porcelain'],{cwd:f.root,encoding:'utf8'}).stdout;assert.deepEqual(after.trim().split('\n').sort(),(before+'?? gh.log\n').trim().split('\n').sort());assert.deepEqual(readFileSync(path.join(f.root,'.git/index')),indexBefore);assert.equal(readFileSync(path.join(f.root,'unrelated.txt'),'utf8'),'preserve');});
test('fake release upload contains only the finished pair',t=>{const f=publication(t);const r=spawnSync('dotnet',[dll,'action','release'],{cwd:f.root,encoding:'utf8',env:{...f.env,CAPTURE_RELEASE_TAG:'v1.0.0'}});assert.equal(r.status,0,r.stderr);const call=JSON.parse(readFileSync(f.env.FAKE_GH_LOG,'utf8').trim());assert.deepEqual(call.args.slice(3,5),[path.join(f.out,'hero.png'),path.join(f.out,'hero-dark.png')]);assert.equal(call.args.filter(x=>x.endsWith('.png')).length,2);assert(!call.args.some(x=>x.endsWith('manifest.json')));});
test('publication rejects mismatched tool version before invoking GitHub',t=>{const f=publication(t);const manifestPath=path.join(f.out,'manifest.json');const manifest=JSON.parse(readFileSync(manifestPath,'utf8'));manifest.tool=toolVersion+'-mismatch';writeFileSync(manifestPath,JSON.stringify(manifest));const r=spawnSync('dotnet',[dll,'action','pr'],{cwd:f.root,encoding:'utf8',env:{...f.env,CAPTURE_UPDATE_PR:'true'}});assert.notEqual(r.status,0);assert(!existsSync(f.env.FAKE_GH_LOG));});
test('publication rejects stale configuration before invoking GitHub',t=>{const f=publication(t);writeFileSync(path.join(f.root,'product-captures.json'),JSON.stringify({name:'Changed'}));const r=spawnSync('dotnet',[dll,'action','pr'],{cwd:f.root,encoding:'utf8',env:{...f.env,CAPTURE_UPDATE_PR:'true'}});assert.notEqual(r.status,0);assert(!existsSync(f.env.FAKE_GH_LOG));});
test('real web host captures both theme storage states at fixed viewport size',t=>{const root=fixture({name:'Web',appearance:{width:400,height:300},capture:{viewport:{width:320,height:240},storage:{theme:'$theme'},ready:'#ready',waitFor:'() => document.documentElement.dataset.theme === localStorage.theme'},server:{directory:'.',port:30000+process.pid%20000}});cleanup(t,root);writeFileSync(path.join(root,'index.html'),'<html><body><div id="ready">Ready</div><script>document.documentElement.dataset.theme=localStorage.theme;document.body.style.background=localStorage.theme==="dark"?"black":"white"</script></body></html>');const r=run(root);assert.equal(r.status,0,r.stderr);const out=path.join(root,'docs/assets/product-captures');for(const theme of ['light','dark']){const png=readFileSync(path.join(out,'captures/overview-'+theme+'.png'));assert.equal(png.readUInt32BE(16),320);assert.equal(png.readUInt32BE(20),240);}assert.notDeepEqual(readFileSync(path.join(out,'captures/overview-light.png')),readFileSync(path.join(out,'captures/overview-dark.png')));});
test('theme storage replaces string placeholders and selects object values without rewriting them', async t => {
  const url = await remote(t, '<body><script>document.body.style.background=localStorage.mode.startsWith("night")?"black":"white"</script></body>');
  const root = fixture({
    name:'Theme storage',
    appearance:{width:400,height:300},
    capture:{
      url,
      viewport:{width:160,height:120},
      storage:{
        theme:'prefix-$theme-$theme',
        mode:{light:'day-$theme',dark:'night-$theme'},
        literal:'unchanged-$Theme',
      },
      waitFor:`() => {
        const theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        return localStorage.theme === 'prefix-' + theme + '-' + theme
          && localStorage.mode === (theme === 'dark' ? 'night-$theme' : 'day-$theme')
          && localStorage.literal === 'unchanged-$Theme';
      }`,
    },
  });
  cleanup(t, root);
  const result = run(root);
  assert.equal(result.status, 0, result.stderr);
  const output = path.join(root, 'docs/assets/product-captures');
  assert.equal(pngs(output).length, 4);
  assert.notDeepEqual(readFileSync(path.join(output, 'captures/overview-light.png')),
                     readFileSync(path.join(output, 'captures/overview-dark.png')));
});
test('SIGTERM cancels owned build process and leaves no output',async t=>{const root=fixture({name:'Example',build:"sh -c 'echo $$ > owned-pid; exec sleep 60'"});cleanup(t,root);const child=spawn('dotnet',[dll,'--config',path.join(root,'product-captures.json')],{cwd:tmpdir(),stdio:'pipe'});const completed=new Promise(resolve=>child.on('close',resolve));const deadline=Date.now()+5000;while(!existsSync(path.join(root,'owned-pid'))&&Date.now()<deadline)await new Promise(r=>setTimeout(r,20));assert(existsSync(path.join(root,'owned-pid')));const pid=Number(readFileSync(path.join(root,'owned-pid'),'utf8').trim());child.kill('SIGTERM');const status=await Promise.race([completed,new Promise((_,reject)=>{setTimeout(()=>{child.kill('SIGKILL');reject(Error('Cancellation timeout'))},5000).unref()})]);assert.notEqual(status,0);assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});assert(!existsSync(path.join(root,'docs')));});
async function remote(t,html){const script=`const http=require('http');const s=http.createServer((q,r)=>{if(q.url.startsWith('/missing')){r.writeHead(404);r.end();return}r.setHeader('Content-Type','text/html');r.end(${JSON.stringify(html)})});s.listen(0,'127.0.0.1',()=>console.log(s.address().port));`;const child=spawn(process.execPath,['-e',script],{stdio:['ignore','pipe','pipe']});t.after(()=>child.kill());const [data]=await once(child.stdout,'data');return 'http://127.0.0.1:'+data.toString().trim();}
test('absolute capture URL works without a local server and actions can reveal ready selector',async t=>{const url=await remote(t,'<button onclick="document.querySelector(\'#ready\').hidden=false">Open</button><div id="ready" hidden>Ready</div>');const root=fixture({name:'Remote',appearance:{width:400,height:300},capture:{url:url+'/scene?theme=yes',viewport:{width:320,height:240},actions:[{type:'click',selector:'button'},{type:'scroll',y:0}],ready:'#ready'}});cleanup(t,root);const r=run(root);assert.equal(r.status,0,r.stderr);assert.equal(pngs(path.join(root,'docs/assets/product-captures')).length,4);});
test('preview URL replaces configured absolute origin preserving path/query and skips build',async t=>{const url=await remote(t,'<body><script>if(location.pathname!=="/scene"||location.search!=="?x=1")throw Error("Wrong URL");document.body.id="ready"</script>Ready</body>');const root=fixture({name:'Preview',appearance:{width:400,height:300},capture:{url:'https://original.invalid/scene?x=1',ready:'#ready',waitFor:'() => location.pathname === "/scene" && location.search === "?x=1"'},build:'touch should-not-build; exit 99'});cleanup(t,root);const r=run(root,['--url',url]);assert.equal(r.status,0,r.stderr);assert(!existsSync(path.join(root,'should-not-build')));});
test('visible broken image rejects fresh capture and preserves saved artwork',async t=>{const url=await remote(t,'<img src="/missing.png" width="100" height="100">');const root=fixture({name:'Broken',capture:{url},appearance:{width:400,height:300}});cleanup(t,root);const out=path.join(root,'docs/assets/product-captures');mkdirSync(out,{recursive:true});writeFileSync(path.join(out,'hero.png'),'old');const r=run(root);assert.notEqual(r.status,0);assert.equal(readFileSync(path.join(out,'hero.png'),'utf8'),'old');assert(!existsSync(path.join(out,'manifest.json')));});
test('offscreen lazy broken image does not prevent viewport capture',async t=>{const url=await remote(t,'<p>Visible scene</p><img loading="lazy" src="/missing.png" width="100" height="100" style="position:absolute;top:5000px">');const root=fixture({name:'Lazy',capture:{url},appearance:{width:400,height:300}});cleanup(t,root);const r=run(root);assert.equal(r.status,0,r.stderr);});
test('occupied command-server listener is preserved and command never starts',async t=>{const url=await remote(t,'<p>Existing service</p>');const root=fixture({name:'Occupied',server:{command:'touch server-started',url}});cleanup(t,root);const r=run(root);assert.notEqual(r.status,0);assert.match(r.stderr,/occupied/);assert(!existsSync(path.join(root,'server-started')));assert.equal((await fetch(url)).status,200);});
test('switching capture to null deletes only two owned raw PNGs after successful render',t=>{const config={name:'Transition',appearance:{width:400,height:300},capture:{type:'image',images:{light:'light.png',dark:'dark.png'}}};const root=fixture(config);cleanup(t,root);for(const theme of ['light','dark'])writeFileSync(path.join(root,theme+'.png'),pixel);let r=run(root);assert.equal(r.status,0,r.stderr);const out=path.join(root,'docs/assets/product-captures');writeFileSync(path.join(out,'captures/user-owned.txt'),'preserve');config.capture=null;writeFileSync(path.join(root,'product-captures.json'),JSON.stringify(config));r=run(root);assert.equal(r.status,0,r.stderr);assert.deepEqual(pngs(out),['hero-dark.png','hero.png']);assert.equal(readFileSync(path.join(out,'captures/user-owned.txt'),'utf8'),'preserve');});

test('web product builds request the pinned Node runtime in action planning',t=>{const root=fixture({name:'Build',build:'npm run build'});cleanup(t,root);const output=path.join(root,'output');const r=spawnSync('dotnet',[dll,'action','plan'],{cwd:root,encoding:'utf8',env:{...process.env,CAPTURE_CONFIG:path.join(root,'product-captures.json'),GITHUB_WORKSPACE:root,GITHUB_OUTPUT:output}});assert.equal(r.status,0,r.stderr);assert.match(readFileSync(output,'utf8'),/needs-node=true/);});
test('preview URL cannot suppress a build for a non-web capture',t=>{for(const capture of [null,{type:'obsidian'}]){const root=fixture({name:'Example',capture,build:'touch should-not-exist'});cleanup(t,root);const r=run(root,['--dry-run','--url','https://preview.example']);assert.notEqual(r.status,0);assert.match(r.stderr,/only available for web/);assert(!existsSync(path.join(root,'should-not-exist')));}});
test('validates effective fade defaults and known backdrop positions before commands',t=>{for(const appearance of [{fadeEnd:0.2},{backdropPosition:'diagonal'}]){const root=fixture({name:'Invalid appearance',appearance,build:'touch should-not-build'});cleanup(t,root);const r=run(root,['--dry-run']);assert.notEqual(r.status,0);assert.match(r.stderr,/fadeStart|backdropPosition/);assert(!existsSync(path.join(root,'should-not-build')));}});
test('failed build diagnostics retain standard output',t=>{const root=fixture({name:'Diagnostic',build:'echo REQUIRED_BUILD_DIAGNOSTIC; exit 7'});cleanup(t,root);const r=run(root);assert.notEqual(r.status,0);assert.match(r.stderr,/exited 7/);assert.match(r.stderr,/REQUIRED_BUILD_DIAGNOSTIC/);});
for(const operation of ['pr','release'])test('SIGTERM cancels fake GitHub '+operation+' without later publication writes',async t=>{const f=publication(t);const fake=path.join(f.root,'bin/gh');let script=readFileSync(fake,'utf8');const blocked=path.join(f.root,'blocked-gh');const wait=`fs.writeFileSync(${JSON.stringify(blocked)},String(process.pid));Atomics.wait(new Int32Array(new SharedArrayBuffer(4)),0,0,60000);`;
script=operation==='release'?script.replace("if(args[0]==='release')process.exit(0);",`if(args[0]==='release'){${wait}process.exit(0);}`):script.replace('let r;if(e',`if(e==='git/trees'){${wait}}let r;if(e`);writeFileSync(fake,script);
const child=spawn('dotnet',[dll,'action',operation],{cwd:f.root,env:{...f.env,CAPTURE_UPDATE_PR:'true',CAPTURE_RELEASE_TAG:'v1.0.0'},stdio:'pipe'});t.after(()=>child.kill('SIGKILL'));const completed=new Promise(resolve=>child.on('close',resolve));const deadline=Date.now()+10000;while(!existsSync(blocked)&&Date.now()<deadline)await new Promise(r=>setTimeout(r,20));assert(existsSync(blocked),'fake GitHub reached blocking boundary');const pid=Number(readFileSync(blocked,'utf8'));child.kill('SIGTERM');const status=await Promise.race([completed,new Promise((_,reject)=>{setTimeout(()=>reject(Error('Publication cancellation timeout')),5000).unref()})]);assert.notEqual(status,0);assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});const calls=readFileSync(f.env.FAKE_GH_LOG,'utf8').trim().split('\n').map(JSON.parse);assert(!calls.some(c=>c.args[3]?.endsWith('/git/refs')||c.args[3]?.includes('/git/refs/heads/')||c.args[3]?.endsWith('/pulls')));if(operation==='pr')assert(calls.at(-1).args[3].endsWith('/git/trees'));else assert.equal(calls.length,1);});
test('whole-operation timeout kills descendants after the shell already exited',async t=>{const url=await remote(t,'<p>Unrelated listener</p>');const root=fixture({name:'Background child',build:{command:'sleep 10 & echo $! > background-pid; exit 0',timeoutMs:1500}});cleanup(t,root);const started=Date.now();const r=run(root);assert.notEqual(r.status,0);assert(Date.now()-started<7000,'timeout also bounds stdout/stderr drain');const pidFile=path.join(root,'background-pid');assert(existsSync(pidFile),'shell started its background child');const pid=Number(readFileSync(pidFile,'utf8').trim());assert.throws(()=>process.kill(pid,0),{code:'ESRCH'});assert.equal((await fetch(url)).status,200);assert(!existsSync(path.join(root,'docs')));});
test('command-server startup bounds inherited pipes and retains both diagnostic streams',async t=>{const url=await remote(t,'<p>Unrelated listener</p>');const root=fixture({name:'Failed server',server:{command:'echo SERVER_DIAGNOSTIC; echo SERVER_STDERR >&2; sleep 60 & echo $! > server-child-pid; exit 7',url:'http://127.0.0.1:'+(30000+(process.pid+1)%20000),timeoutMs:1500}});cleanup(t,root);t.after(()=>{const pidFile=path.join(root,'server-child-pid');if(existsSync(pidFile)){try{process.kill(Number(readFileSync(pidFile,'utf8').trim()),'SIGKILL')}catch{}}});const started=Date.now();const r=run(root);assert.notEqual(r.status,0);assert(Date.now()-started<7000,'startup failure cannot wait for descendant pipes');assert.match(r.stderr,/SERVER_DIAGNOSTIC/);assert.match(r.stderr,/SERVER_STDERR/);const pidFile=path.join(root,'server-child-pid');assert(existsSync(pidFile));assert.throws(()=>process.kill(Number(readFileSync(pidFile,'utf8').trim()),0),{code:'ESRCH'});assert.equal((await fetch(url)).status,200);assert(!existsSync(path.join(root,'docs')));});
