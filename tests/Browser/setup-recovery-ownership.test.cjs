const assert = require('node:assert/strict');
const test = require('node:test');
const { runSetupRecovery } = require('./setup-recovery.cjs');

const name = 'hvo-agentcontrol-setup-9900000000450';
const owned = 'a'.repeat(64);

function dockerWithOwnedFixture(calls) {
  return args => {
    calls.push(args);
    if(args[0] === 'run') return owned + '\n';
    if(args[0] === 'inspect') return JSON.stringify([{NetworkSettings:{Networks:{bridge:{IPAddress:'127.0.0.1'}}}}]);
    return '';
  };
}

test('production entrypoint leaves a name-collision container untouched', async () => {
  const calls = [];
  const docker = args => {
    calls.push(args);
    throw new Error('name is already in use');
  };

  await assert.rejects(() => runSetupRecovery({docker,fixtureName:name,launchBrowser:async()=>{throw new Error('must not launch');}}), /already in use/);
  assert.deepEqual(calls, [['run', '-d', '--name', name, 'hvo-agentcontrol-ssh-fixture:local']]);
  assert.equal(calls.some(args => args[0] === 'rm'), false);
});

test('production entrypoint removes only its ID after owned setup failure', async () => {
  const calls = [];
  const docker = dockerWithOwnedFixture(calls);

  await assert.rejects(() => runSetupRecovery({docker,fixtureName:name,launchBrowser:async()=>({close:async()=>{}}),exercise:async({ownedContainer})=>{
    assert.equal(ownedContainer, owned);
    throw new Error('simulated setup failure');
  }}), /simulated setup failure/);
  assert.equal(calls.some(args => args[0] === 'cp' && args[2].startsWith(owned + ':')), true);
  assert.equal(calls.filter(args => args[0] === 'exec').every(args => args[1] === owned), true);
  assert.deepEqual(calls.at(-1), ['rm', '-f', owned]);
  assert.equal(calls.some(args => args[0] === 'rm' && args[2] === name), false);
});

test('production entrypoint cleans the owned container when browser close fails', async () => {
  const calls = [];
  const docker = dockerWithOwnedFixture(calls);

  await assert.rejects(() => runSetupRecovery({docker,fixtureName:name,launchBrowser:async()=>({close:async()=>{throw new Error('simulated browser close failure');}}),exercise:async()=>{}}), /browser close failure/);
  assert.deepEqual(calls.at(-1), ['rm', '-f', owned]);
  assert.equal(calls.some(args => args[0] === 'rm' && args[2] === name), false);
});
