const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const test = require('node:test');

test('pending terminal asset is shared and failed asset can retry', async () => {
  const source = fs.readFileSync(path.join(__dirname, '../../src/HVO.AgentControl/Components/Pages/Terminal.razor.js'), 'utf8')
    .replace('export async function open', 'async function open').replace('export function close', 'function close')
    .replace("window.addEventListener('pagehide', close);", 'globalThis.script = script;');
  const elements = [];
  const context = { window: {}, document: { head: { appendChild(element) { elements.push(element); } }, createElement() { return { remove() {} }; } }, clearInterval() {}, ResizeObserver() {} };
  vm.runInNewContext(source, context);
  const first = context.script('/vendor/xterm/xterm.js');
  const second = context.script('/vendor/xterm/xterm.js');
  assert.equal(elements.length, 1);
  elements[0].onerror();
  await assert.rejects(first); await assert.rejects(second);
  const retry = context.script('/vendor/xterm/xterm.js');
  assert.equal(elements.length, 2);
  context.window.Terminal = function () {};
  elements[1].onload();
  await retry;
});
