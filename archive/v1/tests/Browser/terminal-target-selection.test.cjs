const assert = require('node:assert/strict');
const { selectRuntime } = require('./terminal-target-selection.cjs');

const other = { id: 'other', transport: 'Connected' };
const first = { id: 'first', transport: 'Disconnected' };
assert.equal(selectRuntime([first, other], 'other'), other);
assert.equal(selectRuntime([first, other], 'missing'), null);
assert.equal(selectRuntime([first, other]), other);
assert.equal(selectRuntime([first]), first);
console.log('PASS: explicit terminal targets fail closed; omitted targets use documented fallback.');
