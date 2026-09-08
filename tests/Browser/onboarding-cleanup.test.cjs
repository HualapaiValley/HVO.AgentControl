const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const test = require('node:test');

test('restores fixture SSH authentication when browser launch fails', async () => {
  const source = fs.readFileSync(path.join(__dirname, 'onboarding.cjs'), 'utf8');
  const calls = [];
  let exitCode;
  const context = {
    Buffer, console: {error() {}}, process: {env: {}, exit(code) { exitCode = code; }},
    require(name) {
      if(name === '@playwright/test') return {chromium: {launch: async () => { throw new Error('launch failed'); }}};
      if(name === 'node:child_process') return {execFileSync(command, args, options) { calls.push({command, args, options}); }};
      if(name === 'node:crypto') return {randomBytes: () => Buffer.alloc(20), randomUUID: () => 'id'};
      if(name === 'node:fs') return fs;
      if(name === 'node:path') return path;
      throw new Error(`Unexpected module: ${name}`);
    },
    __dirname,
    setTimeout,
  };
  vm.runInNewContext(source, context, {filename: 'onboarding.cjs'});
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(exitCode, 1);
  assert.equal(calls.length, 3);
  assert.match(calls[0].args.at(-1), /chpasswd/);
  assert.match(calls[1].args.at(-1), /PasswordAuthentication yes/);
  assert.match(calls[2].args.at(-1), /rm -f.*passwd -d/);
});
