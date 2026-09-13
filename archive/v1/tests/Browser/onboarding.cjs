// Guided onboarding against a disposable SSH fixture and real installed OpenCode.
const {chromium, expect} = require(process.env.HVO_PLAYWRIGHT || '@playwright/test');
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const {execFileSync} = require('node:child_process');
const root = path.resolve(__dirname, '../..'), base = process.env.HVO_BASE_URL || 'http://127.0.0.1:5056';
const container = 'hvo-agentcontrol-fixture-a';
function docker(args, input) { return execFileSync('docker', ['exec', '-i', container, ...args], {input, encoding:'utf8'}); }
(async () => {
  const password = 'browser-onboarding-' + crypto.randomBytes(20).toString('hex');
  let browser, context, runtime, csrf;
  try {
    docker(['chpasswd'], 'agent:' + password + '\n');
    docker(['sh','-c', "printf 'PasswordAuthentication yes\\n' > /etc/ssh/sshd_config.d/00-hvo-onboarding.conf; kill -HUP $(cat /run/sshd.pid)"]);
    browser = await chromium.launch({args:['--no-sandbox']});
    context = await browser.newContext({viewport:{width:1440,height:1100}});
    const page = await context.newPage(), errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(base + '/login');
    await page.getByLabel('Owner password').fill(fs.readFileSync(path.join(root,'.fixture/secrets/owner-password'),'utf8').trim());
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
    csrf = (await (await context.request.get(base+'/api/v1/csrf')).json()).token;
    const fixture = JSON.parse(fs.readFileSync(path.join(root,'.fixture/runtime-a.json')));
    const name = 'Guided onboarding ' + Date.now();
    await page.getByRole('navigation',{name:'Administration'}).getByRole('link',{name:'Runtimes',exact:true}).click();
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
    await page.getByRole('button',{name:'Add runtime',exact:true}).click();
    const profile = page.getByRole('region',{name:'Runtime profile'});
    await profile.getByLabel('Name',{exact:true}).fill(name);
    await profile.getByLabel('SSH host',{exact:true}).fill(fixture.host);
    await profile.getByLabel('SSH username',{exact:true}).fill('agent');
    await profile.getByText('OpenCode startup options',{exact:true}).click();
    await profile.getByLabel('Disable external plugins').check();
    await profile.getByLabel('Write diagnostic logs').check();
    await profile.getByLabel('Log level').selectOption('WARN');
    await profile.getByRole('button',{name:'Verify',exact:true}).click();
    await expect(profile.getByRole('status')).toContainText(fixture.fingerprint,{timeout:20000});
    await profile.getByRole('button',{name:'Trust this host and verify',exact:true}).click();
    await expect(profile.getByRole('status')).toContainText('Enter the SSH password');
    await profile.getByLabel(/^SSH password/).fill('incorrect-password');
    await profile.getByRole('button',{name:'Verify',exact:true}).click();
    await expect(profile.getByRole('status')).toContainText('SSH authentication failed',{timeout:20000});
    await profile.getByLabel(/^SSH password/).fill(password);
    await profile.getByRole('button',{name:'Verify',exact:true}).click();
    await expect(profile.getByRole('button',{name:'Save and set up workers',exact:true})).toBeVisible({timeout:30000});
    await expect(profile.getByLabel(/^SSH password/)).toHaveValue('');
    await profile.getByLabel('Log level').selectOption('INFO');
    await expect(profile.getByRole('button',{name:'Save and set up workers',exact:true})).toHaveCount(0);
    await profile.getByRole('button',{name:'Verify',exact:true}).click();
    await expect(profile.getByRole('button',{name:'Save and set up workers',exact:true})).toBeVisible({timeout:30000});
    fs.mkdirSync(path.join(root,'artifacts/browser'),{recursive:true});
    await page.screenshot({path:path.join(root,'artifacts/browser/onboarding-verified.png'),fullPage:true});
    await profile.getByRole('button',{name:'Save and set up workers',exact:true}).click();
    await expect(page).toHaveURL(/\/workers\?runtime=.*new=true/,{timeout:240000});
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
    const worker = page.getByRole('region',{name:'New worker'});
    await expect(worker).toBeVisible({timeout:10000});
    const snap = await (await context.request.get(base+'/api/v1/snapshot')).json();
    runtime = snap.runtimes.find(r => r.name === name);
    if(JSON.stringify(snap).includes(password)) throw new Error('Plaintext credential leaked into snapshot');
    if(!runtime.credentialReference.startsWith('vault-') || !runtime.serverPasswordReference.startsWith('vault-')) throw new Error('Entered/generated credentials were not protected');
    const pid = docker(['tmux','-L',runtime.tmuxName,'display-message','-p','-t','managed','#{pane_pid}'].toSpliced(0,0,'runuser','-u','agent','--')).trim();
    const args = docker(['cat','/proc/'+pid+'/cmdline']).replaceAll('\0',' ');
    if(!args.includes('--pure --print-logs --log-level INFO')) throw new Error('Selected startup options were not applied to the native process');
    const dir = '/home/agent/workspaces/onboarding-' + crypto.randomBytes(5).toString('hex');
    docker(['runuser','-u','agent','--','mkdir','-p',dir]);
    await worker.getByLabel('Worker name').fill('Onboarding worker');
    await worker.getByLabel('Remote workspace directory').fill(dir);
    await worker.getByLabel('Provider / model').selectOption('opencode/big-pickle');
    await worker.getByRole('button',{name:'Create worker',exact:true}).click();
    await expect(page).toHaveURL(/\/\?worker=/,{timeout:30000});
    await expect(page.locator('.shell')).toHaveAttribute('data-interactive','true');
    await expect(page.getByRole('region',{name:'Worker conversation'})).toContainText(dir);
    if(errors.length) throw new Error(errors.join('\n'));
    console.log('PASS: guided first trust, missing/wrong password, encrypted saved credential reuse, verification invalidation, real startup flags and automatic worker setup.');
  } finally {
    if(runtime && context) {
      try {
        await context.request.post(base+'/api/v1/runtimes/'+runtime.id+'/stop',{data:{id:crypto.randomUUID()},headers:{'X-CSRF-TOKEN':csrf}});
        for(let i=0;i<40;i++) {
          const snap = await (await context.request.get(base+'/api/v1/snapshot')).json();
          if(snap.runtimes.find(r=>r.id===runtime.id)?.transport==='Disconnected') break;
          await new Promise(resolve=>setTimeout(resolve,250));
        }
      } finally {
        try {
          if(browser) await browser.close();
        } finally {
          docker(['sh','-c','rm -f /etc/ssh/sshd_config.d/00-hvo-onboarding.conf; passwd -d agent >/dev/null; kill -HUP $(cat /run/sshd.pid)']);
        }
      }
    } else {
      try {
        if(browser) await browser.close();
      } finally {
        docker(['sh','-c','rm -f /etc/ssh/sshd_config.d/00-hvo-onboarding.conf; passwd -d agent >/dev/null; kill -HUP $(cat /run/sshd.pid)']);
      }
    }
  }
})().catch(e=>{console.error(e);process.exit(1)});
