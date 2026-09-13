let socket, terminal, observer, heartbeat, generation = 0;
const scripts = new Map();
async function script(src) {
    if (window[src.includes('addon-fit') ? 'FitAddon' : 'Terminal']) return;
    if (!scripts.has(src)) scripts.set(src, new Promise((resolve, reject) => {
        const element = document.createElement('script'); element.src = src;
        element.onload = resolve; element.onerror = () => { element.remove(); scripts.delete(src); reject(new Error('Terminal assets unavailable')); };
        document.head.appendChild(element);
    }));
    await scripts.get(src);
}
export function close() {
    generation++;
    clearInterval(heartbeat); observer?.disconnect();
    if (socket) { socket.onclose = null; socket.close(); socket = null; }
    terminal?.dispose(); terminal = null;
}
export async function open(host, runtimeId, callback) {
    close();
    const opening = generation;
    await script('/vendor/xterm/xterm.js'); await script('/vendor/xterm/addon-fit.js');
    if (opening !== generation) return;
    const response = await fetch('/api/v1/csrf');
    if (!response.ok) throw new Error('Sign in again');
    const { token } = await response.json();
    if (opening !== generation) return;
    terminal = new window.Terminal({ cursorBlink: true, scrollback: 3000, fontSize: 14, theme: { background: '#111827' } });
    const fit = new window.FitAddon.FitAddon(); terminal.loadAddon(fit); terminal.open(host); fit.fit();
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/runtimes/${encodeURIComponent(runtimeId)}/terminal`);
    socket = ws; ws.binaryType = 'arraybuffer';
    const send = message => { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message)); };
    terminal.onData(data => {
        // Bound each frame, including escaped control sequences and pasted text.
        for (let offset = 0; offset < data.length; offset += 1024) send({ type: 'input', data: data.slice(offset, offset + 1024) });
    });
    terminal.onResize(({ cols, rows }) => send({ type: 'resize', cols, rows }));
    observer = new ResizeObserver(() => { if (terminal) fit.fit(); }); observer.observe(host);
    ws.onopen = () => {
        send({ token }); send({ type: 'resize', cols: terminal.cols, rows: terminal.rows });
        heartbeat = setInterval(() => send({ type: 'ping' }), 20000);
        callback.invokeMethodAsync('TerminalState', true, 'Opening SSH shell…').catch(() => {}); terminal.focus();
    };
    ws.onmessage = event => {
        if (!terminal || socket !== ws) return;
        if (typeof event.data === 'string') { callback.invokeMethodAsync('TerminalState', true, JSON.parse(event.data).status).catch(() => {}); return; }
        terminal.write(new Uint8Array(event.data));
    };
    ws.onclose = () => {
        clearInterval(heartbeat); observer?.disconnect();
        if (socket === ws) {
            terminal?.writeln('\r\n[Connection closed]');
            callback.invokeMethodAsync('TerminalState', false, 'Shell closed. Reopen for a new shell; check SSH access if it closed immediately.').catch(() => {});
        }
    };
}
window.addEventListener('pagehide', close);
