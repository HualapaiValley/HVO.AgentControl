const { chromium, expect } = require('@playwright/test');
const fs = require('node:fs'); const path = require('node:path'); const {execFileSync} = require('node:child_process');
const root = path.resolve(__dirname,'../..'), base = process.env.HVO_BASE_URL || 'http://127.0.0.1:5056';
(async () => {
 const browser = await chromium.launch({headless:true,args:['--no-sandbox']});
 try {
  const context=await browser.newContext(), page=await context.newPage(); const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/login'); await page.getByLabel('Owner password').fill(fs.readFileSync(root+'/.fixture/secrets/owner-password','utf8').trim());
  await page.getByRole('button',{name:'Sign in',exact:true}).click(); await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  const csrf=await (await context.request.get(base+'/api/v1/csrf')).json();const headers={'X-CSRF-TOKEN':csrf.token};
  const get=async url=>(await context.request.get(base+'/api/v1'+url)).json();
  const post=async(url,data)=>{const r=await context.request.post(base+'/api/v1'+url,{headers,data});if(!r.ok())throw new Error(await r.text());return r.json();};
  const fixture=JSON.parse(fs.readFileSync(root+'/.fixture/runtime-a.json')); const runtime=(await get('/snapshot')).runtimes.find(x=>x.host===fixture.host);
  if(!runtime)throw new Error('Run browser smoke first to register a fixture runtime.');
  await post('/runtimes/'+runtime.id+'/connect',{id:crypto.randomUUID()});
  await expect.poll(async()=> (await get('/snapshot')).runtimes.find(x=>x.id===runtime.id).health,{timeout:30000}).toBe('Healthy');
  let worker, coordinator;
  for (const role of ['Worker','Coordinator']) {
   const directory='/home/agent/workspaces/capability-'+role.toLowerCase()+'-'+Date.now();
   execFileSync('docker',['exec','-u','agent','hvo-agentcontrol-fixture-a','mkdir','-p',directory]);
   execFileSync('docker',['exec','-u','agent','hvo-agentcontrol-fixture-a','git','-C',directory,'init','-q']);
   const command=await post('/workers',{id:crypto.randomUUID(),runtimeId:runtime.id,name:'Capability '+role+' '+directory.split('-').at(-1),project:'Validation',directory,providerId:'fixture',modelId:'deterministic',role,discoverCapabilities:true});
   await expect.poll(async()=> (await get('/commands/'+command.id)).state,{timeout:30000}).toBe('Finished');
   const detail=await get('/workers/'+(await get('/commands/'+command.id)).resultId);
   if(role==='Worker')worker=detail.worker;else coordinator=detail.worker;
  }
  await expect.poll(async()=> (await get('/workers/'+worker.id)).worker.capabilityReport,{timeout:30000}).not.toBe('');
  worker=(await get('/workers/'+worker.id)).worker;
  const facts=JSON.parse(worker.capabilitiesJson);expect(facts.source).toBe('probe');expect(facts.scope).toBe(worker.directory);expect(facts.facts['tool.git']).toBe('present');expect(facts.facts.dockerDaemonAccess).toBe('unknown');
  expect((await get('/workers/'+coordinator.id)).commands.some(x=>x.origin==='capability-report')).toBe(false);
  await page.goto(base+'/workers');await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await expect(page.locator('.worker-inventory')).not.toContainText('Capability Coordinator');
  const card=page.locator('.worker-card').filter({has:page.getByRole('heading',{name:worker.name,exact:true})});
  await card.getByText(/Capabilities ·/).click();await expect(card).toContainText('Workspace free disk (KiB)');
  await page.goto(base+'/coordination');await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await page.getByLabel('Coordinator',{exact:true}).selectOption(coordinator.id);
  expect(await page.getByLabel('Coordinator',{exact:true}).locator('option').evaluateAll(items=>items.map(x=>x.value))).not.toContain(worker.id);
  await expect(page.getByRole('group',{name:'Agents it can contact'})).not.toContainText('Capability Coordinator');
  await page.goto(base+'/?worker='+worker.id);await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await page.getByLabel('Include coordination guidance',{exact:true}).check();await page.getByLabel('Progress interval in minutes').fill('3');
   await page.getByLabel('Task risk').selectOption('low');await page.getByLabel('Task or follow-up').fill('Reply with a short acknowledgement.');await page.getByRole('button',{name:'Send instruction',exact:true}).click();
  await expect.poll(async()=> (await get('/workers/'+worker.id)).commands.filter(x=>x.origin==='owner'&&x.kind==='Prompt').length).toBe(1);
  const submitted=(await get('/workers/'+worker.id)).commands.find(x=>x.origin==='owner'&&x.kind==='Prompt');
  expect(JSON.parse(submitted.executionPayload).text).toContain('every 3 minutes');expect(JSON.parse(submitted.payload).text).toBe('Reply with a short acknowledgement.');
  await page.goto(base+'/?worker='+coordinator.id);await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await expect(page.getByLabel('Task or follow-up')).toHaveCount(0);await expect(page.getByText(/routing-only conversation/)).toBeVisible();
  await page.goto(base+'/coordination');await page.setViewportSize({width:390,height:844});await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.screenshot({path:root+'/artifacts/browser/coordinator-roles-mobile.png',fullPage:true});
  expect(errors).toEqual([]);console.log('PASS: initial worker handshake, measured workspace facts, coordinator exclusion, guidance capture, separate settings/conversation and mobile layout.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
