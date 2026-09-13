// Requires the actual release installed by NativeReleaseTests and anonymous-provider availability.
const {chromium,expect}=require(process.env.HVO_PLAYWRIGHT || '@playwright/test');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),{execFileSync}=require('node:child_process');
const root=path.resolve(__dirname,'../..'),base=process.env.HVO_BASE_URL||'http://127.0.0.1:5054';
(async()=>{
  const suffix=crypto.randomBytes(5).toString('hex'), directory='/home/agent/workspaces/native-browser-'+suffix;
  execFileSync('docker',['exec','-u','agent','hvo-agentcontrol-fixture-b','mkdir','-p',directory]);
  execFileSync('docker',['exec','-u','agent','hvo-agentcontrol-fixture-b','git','-C',directory,'init','-q']);
  execFileSync('docker',['exec','-i','-u','agent','hvo-agentcontrol-fixture-b','python3','-c','import sys;open(sys.argv[1],"w").write(sys.stdin.read())',directory+'/opencode.json'],{input:JSON.stringify({permission:{bash:'ask'}})});
  const browser=await chromium.launch({args:['--no-sandbox']});
  const context=await browser.newContext({viewport:{width:1440,height:1100}}),page=await context.newPage();
  await page.goto(base+'/login');
  await page.getByLabel('Owner password').fill(fs.readFileSync(path.join(root,'.fixture/secrets/owner-password'),'utf8').trim());
  await page.getByRole('button',{name:'Sign in',exact:true}).click();
  await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  const csrf=(await (await context.request.get(base+'/api/v1/csrf')).json()).token;
  async function post(route,data){const response=await context.request.post(base+'/api/v1'+route,{data,headers:{'X-CSRF-TOKEN':csrf}});if(!response.ok())throw new Error(route+': '+await response.text());return response.json();}
  async function snapshot(){return (await context.request.get(base+'/api/v1/snapshot')).json();}
  async function wait(predicate,description,seconds=120){const end=Date.now()+seconds*1000;while(!await predicate()){if(Date.now()>end)throw new Error(description);await page.waitForTimeout(500);}}
  const fixture=JSON.parse(fs.readFileSync(path.join(root,'.fixture/runtime-b.json')));
  const runtime=await post('/runtimes',{id:crypto.randomUUID().replaceAll('-',''),name:'Native browser '+suffix,host:fixture.host,port:22,username:'agent',hostKeySha256:fixture.fingerprint,credentialReference:'fixture-key',serverPasswordReference:'server-password',stateDirectory:'/home/agent/native-browser-'+suffix,allowedRoots:'/home/agent/workspaces',executable:'/home/agent/native-release-evidence/bin/opencode',apiPort:53000+Math.floor(Math.random()*7000),capacity:2,installIfMissing:false});
  await post('/runtimes/'+runtime.id+'/connect',{id:crypto.randomUUID()});
  await wait(async()=> (await snapshot()).runtimes.find(x=>x.id===runtime.id).health==='Healthy','Native browser runtime did not connect');
  await post('/workers',{id:crypto.randomUUID(),runtimeId:runtime.id,name:'Native browser worker '+suffix,project:'Live browser validation',directory,providerId:'opencode',modelId:'big-pickle'});
  let worker;
  await wait(async()=>{worker=(await snapshot()).workers.find(x=>x.runtimeId===runtime.id);return worker&&!worker.stale;},'Native browser worker not created');
  await page.goto(base+'/?worker='+worker.id);
  await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  const conversation=page.getByRole('region',{name:'Worker conversation'});
  const token='BROWSER_NATIVE_'+suffix;
  await conversation.getByLabel('Task or follow-up').fill('In this disposable workspace use bash to run exactly `printf "'+token+'\\n" > browser-evidence.txt; cat browser-evidence.txt`. Report the output and stop. Do not commit or access other directories.');
  await conversation.getByRole('button',{name:'Send instruction',exact:true}).click();
  let approvals=0,questions=0;
  async function finish(count,answerQuestion=false){await wait(async()=>{
    const permission=conversation.getByRole('button',{name:'Allow once',exact:true});
    if(await permission.count()&&await permission.first().isEnabled()){await permission.first().click();approvals++;}
    const question=conversation.getByRole('heading',{name:'Question from worker'});
    if(answerQuestion&&await question.count()){
      const input=conversation.locator('.request textarea').first();
      if(await input.count()&&await conversation.getByRole('button',{name:'Send answers',exact:true}).isEnabled()){
        await input.fill('Append');await conversation.getByRole('button',{name:'Send answers',exact:true}).click();questions++;
      }
    }
    const snap=await snapshot();return snap.commands.filter(c=>c.workerId===worker.id&&c.kind==='Prompt'&&c.state==='Finished').length>=count;
  },'Native browser task did not finish',150);}
  await finish(1);
  await expect(conversation.locator('.transcript')).toContainText(token);
  await conversation.getByLabel('Task or follow-up').fill('Use the question tool to ask whether to append FOLLOWUP to browser-evidence.txt, offering Append and Cancel. Wait for the answer. If Append, use bash to append FOLLOWUP on a new line, then cat the file. Report both lines and stop. Do not commit or access other directories.');
  await conversation.getByRole('button',{name:'Send instruction',exact:true}).click();
  await finish(2,true);
  if(approvals<1||questions<1)throw new Error('Native permission/question controls were not both exercised');
  const output=execFileSync('docker',['exec','-u','agent','hvo-agentcontrol-fixture-b','cat',directory+'/browser-evidence.txt'],{encoding:'utf8'});
  if(!output.includes(token)||!output.includes('FOLLOWUP'))throw new Error('Native browser output did not match');
  await page.reload();await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await page.goto(base+'/?worker='+worker.id);
  await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
  await expect(page.locator('.transcript')).toContainText('FOLLOWUP');
  fs.mkdirSync(path.join(root,'artifacts/browser'),{recursive:true});
  await page.screenshot({path:path.join(root,'artifacts/browser/native.png'),fullPage:true});
  fs.writeFileSync(path.join(root,'.fixture/native-browser-evidence.json'),JSON.stringify({version:'1.18.29',provider:'opencode',model:'big-pickle',runtimeId:runtime.id,nativeSessionId:worker.nativeSessionId,directory,approvals,questions,output},null,2));
  await post('/runtimes/'+runtime.id+'/stop',{id:crypto.randomUUID()});
  console.log('PASS: real OpenCode + opencode/big-pickle browser task/follow-up, native permission/question replies, file validation and reload. '+JSON.stringify({approvals,questions,output}));
  await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
