const assert = require('node:assert/strict');
const test = require('node:test');
const { createFixture, cleanupFixture } = require('./setup-recovery-ownership.cjs');

const name = 'hvo-agentcontrol-setup-9900000000450';
const owned = 'a'.repeat(64);

function attempt(docker, afterCreate) {
  let id;
  try {
    id = createFixture(docker, name);
    afterCreate(id);
  } finally {
    cleanupFixture(docker, id);
  }
}

test('creation failure never removes the pre-existing named container', () => {
  const calls = [];
  const docker = args => {
    calls.push(args);
    throw new Error('name is already in use');
  };

  assert.throws(() => attempt(docker, () => {}), /already in use/);
  assert.deepEqual(calls, [['run', '-d', '--name', name, 'hvo-agentcontrol-ssh-fixture:local']]);
  assert.equal(calls.some(args => args[0] === 'rm'), false);
});

test('failure after creation removes only the returned owned container ID', () => {
  const calls = [];
  const docker = args => {
    calls.push(args);
    return args[0] === 'run' ? owned + '\n' : '';
  };

  assert.throws(() => attempt(docker, id => {
    assert.equal(id, owned);
    docker(['cp', 'fixture-key.pub', id + ':/home/agent/.ssh/authorized_keys']);
    throw new Error('simulated setup failure');
  }), /simulated setup failure/);
  assert.deepEqual(calls.at(-1), ['rm', '-f', owned]);
  assert.equal(calls.some(args => args[0] === 'rm' && args[2] === name), false);
});

test('cleanup rejects unproven output and never substitutes the fixture name', () => {
  const calls = [];
  const docker = args => {
    calls.push(args);
    return 'not-a-container-id';
  };

  assert.throws(() => attempt(docker, () => {}), /container ID/);
  assert.deepEqual(calls, [['run', '-d', '--name', name, 'hvo-agentcontrol-ssh-fixture:local']]);
  assert.equal(calls.some(args => args[0] === 'rm'), false);
});
