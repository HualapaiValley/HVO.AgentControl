import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const source = fs.readFileSync(new URL("../../src/HVO.AgentControl/wwwroot/js/terminal.js", import.meta.url), "utf8")
  .replace(/function boot\(\)[\s\S]*$/, "globalThis.TerminalPortal = TerminalPortal;");
class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;

  constructor(url) {
    this.url = url;
    this.readyState = FakeWebSocket.CONNECTING;
  }

  close() {
    this.readyState = 3;
  }
}

const context = {
  console,
  TextDecoder,
  Uint8Array,
  WebSocket: FakeWebSocket,
  window: { location: { protocol: "https:", host: "example.test" } },
  atob: value => Buffer.from(value, "base64").toString("binary"),
};
vm.createContext(context);
vm.runInContext(source, context);
const portal = Object.create(context.TerminalPortal.prototype);
const writes = [];
portal.term = { write: value => writes.push(value) };
portal.outputDecoder = null;
portal.writeNotice = value => writes.push(`[notice] ${value}`);

portal.handleMessage(JSON.stringify({ type: "output", encoding: "text", data: "\u001b[31mlocal\u001b[0m" }));
assert.equal(writes.shift(), "\u001b[31mlocal\u001b[0m");

const utf8 = Buffer.from("remote 🙂");
portal.handleMessage(JSON.stringify({ type: "output", encoding: "base64", data: utf8.subarray(0, utf8.length - 1).toString("base64") }));
portal.handleMessage(JSON.stringify({ type: "output", encoding: "base64", data: utf8.subarray(utf8.length - 1).toString("base64") }));
assert.equal(writes.join(""), "remote 🙂");
writes.length = 0;

portal.handleMessage(JSON.stringify({ type: "output", encoding: "binary", data: "AAAA" }));
assert.match(writes.shift(), /unsupported terminal output encoding/);

const emoji = Buffer.from("🙂");
portal.handleMessage(JSON.stringify({ type: "output", encoding: "base64", data: emoji.subarray(0, 3).toString("base64") }));
assert.equal(writes.join(""), "");
portal.socket = { close() {} };
portal.closeSocket(1000, "detach");
assert.equal(portal.outputDecoder, null);
portal.handleMessage(JSON.stringify({ type: "output", encoding: "base64", data: Buffer.from("fresh").toString("base64") }));
assert.equal(writes.join(""), "fresh");
assert.doesNotMatch(writes.join(""), /�/);
writes.length = 0;

portal.canAttach = () => true;
portal.renderConnection = () => {};
portal.setOverlay = () => {};
portal.scheduleReconnect = () => {};
portal.updateButtons = () => {};
portal.selectedTerminalUrl = "/terminal?employeeId=emp-fixed";
portal.outputDecoder = new TextDecoder("utf-8", { fatal: false });
portal.outputDecoder.decode(emoji.subarray(0, 3), { stream: true });
portal.connect();
assert.equal(portal.outputDecoder, null);
portal.handleMessage(JSON.stringify({ type: "output", encoding: "text", data: "local" }));
portal.handleMessage(JSON.stringify({ type: "output", encoding: "base64", data: Buffer.from(" remote").toString("base64") }));
assert.equal(writes.join(""), "local remote");
assert.doesNotMatch(writes.join(""), /�/);

console.log("terminal wire tests: 5 passed");
