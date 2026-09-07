#!/usr/bin/python3
"""Deterministic OpenCode HTTP/SSE contract fixture. This is never a model run."""
import base64
import copy
import json
import os
import queue
import socket
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

if '--version' in sys.argv:
    print('1.18.29')
    sys.exit(0)
port = int(sys.argv[sys.argv.index('--port') + 1])
state_file = os.path.join(os.getcwd(), 'fixture-state.json')
lock = threading.RLock()
state = {'sessions': {}, 'messages': {}, 'status': {}, 'permissions': {}, 'questions': {}, 'submissions': 0}
if os.path.exists(state_file):
    with open(state_file) as f:
        state.update(json.load(f))
    state['status'] = {}
    state['permissions'] = {}
    state['questions'] = {}
subscribers = []
waiters = {}
no_models = False
hold_provider = False
provider_waiting = threading.Event()
provider_release = threading.Event()

def save():
    with lock:
        with open(state_file + '.tmp', 'w') as f:
            json.dump(state, f)
        os.replace(state_file + '.tmp', state_file)

def now():
    return int(time.time() * 1000)

def emit(directory, kind, properties):
    value = {'directory': directory, 'payload': {'id': 'evt_' + uuid.uuid4().hex, 'type': kind, 'properties': properties}}
    with lock:
        for subscriber in subscribers:
            subscriber.put(value)

def finish(session_id, message_id, text):
    directory = state['sessions'][session_id]['directory']
    aborted = False
    request = None
    kind = None
    if '[question]' in text or '[permission]' in text:
        kind = 'questions' if '[question]' in text else 'permissions'
        request_id = ('que_' if kind == 'questions' else 'per_') + uuid.uuid4().hex
        request = {'id': request_id, 'sessionID': session_id}
        if kind == 'questions':
            request['questions'] = [{'header': 'Choice', 'question': 'Which fixture option?', 'options': [{'label': 'One', 'description': 'First option'}, {'label': 'Two', 'description': 'Second option'}], 'multiple': False}]
        else:
            request.update({'permission': 'bash', 'patterns': ['printf fixture'], 'always': ['printf *'], 'metadata': {}})
        with lock:
            state[kind][request_id] = request
            waiters[request_id] = threading.Event()
            save()
        emit(directory, 'question.asked' if kind == 'questions' else 'permission.asked', request)
        waiters[request_id].wait(60)
    else:
        # A real process remains active through SSH/client disconnects, but its output is simulated.
        for _ in range(80 if '[hold]' in text else 5):
            time.sleep(.1)
            if state['status'].get(session_id) != 'busy':
                aborted = True
                break
    with lock:
        if request and request['id'] in state[kind]:
            del state[kind][request['id']]
        output = 'Fixture response: ' + text + ' — café 🛰\nTranscript is simulated.'
        assistant = {'info': {'id': 'msg_' + uuid.uuid4().hex, 'sessionID': session_id, 'parentID': message_id, 'role': 'assistant', 'time': {'created': now(), 'completed': now()}, 'tokens': {'input': 5, 'output': 8}},
                     'parts': [{'id': 'prt_' + uuid.uuid4().hex, 'sessionID': session_id, 'messageID': message_id, 'type': 'text', 'text': output}]}
        if '[error]' in text or aborted:
            assistant['info']['error'] = {'name': 'FixtureError' if not aborted else 'MessageAbortedError', 'data': {'message': 'Simulated native error'}}
        state['messages'][session_id].append(assistant)
        state['status'][session_id] = 'idle'
        save()
    for _ in range(2):
        emit(directory, 'message.part.updated', {'sessionID': session_id, 'part': assistant['parts'][0]})
    emit(directory, 'future.event', {'sessionID': session_id, 'unknownOptionalField': True})
    emit(directory, 'session.status', {'sessionID': session_id, 'status': {'type': 'idle'}})

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *_):
        pass
    def auth(self):
        value = base64.b64encode(('opencode:' + os.environ['OPENCODE_SERVER_PASSWORD']).encode()).decode()
        if self.headers.get('Authorization') == 'Basic ' + value:
            return True
        self.respond(401, {'error': 'Authentication required'})
        return False
    def respond(self, status, value=None):
        data = json.dumps(value, ensure_ascii=False).encode() if value is not None else b''
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        if data:
            self.wfile.write(data)
    def context(self):
        parsed = urlparse(self.path)
        return parsed.path, parse_qs(parsed.query).get('directory', [''])[0]
    def do_GET(self):
        global no_models, hold_provider
        if not self.auth():
            return
        path, directory = self.context()
        if path == '/global/event':
            return self.events()
        if path == '/provider':
            with lock:
                should_hold = hold_provider
                hold_provider = False
            if should_hold:
                provider_waiting.set()
                provider_release.wait(60)
                provider_waiting.clear()
        with lock:
            if path == '/fixture/hold-provider':
                provider_release.clear()
                hold_provider = True
                value = True
            elif path == '/fixture/release-provider':
                provider_release.set()
                value = True
            elif path == '/global/health':
                value = {'healthy': True, 'version': '1.18.29'}
            elif path == '/doc':
                routes = ['/global/event', '/session', '/session/{sessionID}/prompt_async', '/session/{sessionID}/message', '/session/status', '/provider', '/path', '/session/{sessionID}/abort', '/permission/{requestID}/reply', '/question/{requestID}/reply']
                value = {'paths': {route: {} for route in routes}}
            elif path == '/path':
                value = {'directory': os.path.realpath(directory)}
            elif path == '/provider':
                value = {'connected': [] if no_models else ['fixture'], 'all': [{'id': 'fixture', 'models': {'deterministic': {'name': 'Deterministic fixture'}, 'deterministic-alt': {'name': 'Alternate deterministic fixture', 'variants': {'high': {}}}}}], 'default': {}}
            elif path == '/fixture/no-models':
                no_models = True
                value = True
            elif path == '/fixture/models':
                no_models = False
                value = True
            elif path == '/fixture/stats':
                value = {'submissions': state['submissions'], 'sessions': len(state['sessions']), 'providerWaiting': provider_waiting.is_set()}
            elif path == '/fixture/drop-sse':
                for subscriber in subscribers:
                    subscriber.put(None)
                value = True
            elif path == '/session':
                value = [s for s in state['sessions'].values() if s['directory'] == directory]
            elif path == '/session/status':
                value = {sid: {'type': status} for sid, status in state['status'].items() if state['sessions'][sid]['directory'] == directory and status != 'idle'}
            elif path in ['/question', '/permission']:
                value = [x for x in state['questions' if path == '/question' else 'permissions'].values() if state['sessions'][x['sessionID']]['directory'] == directory]
            elif path.startswith('/session/'):
                parts = path.split('/')
                session = state['sessions'].get(parts[2])
                if not session or session['directory'] != directory:
                    return self.respond(404, {})
                if len(parts) == 3:
                    value = session
                else:
                    value = state['messages'][parts[2]]
                    if len(parts) > 4:
                        value = next((m for m in value if m['info']['id'] == parts[4]), None)
                        if value is None:
                            return self.respond(404, {})
            else:
                return self.respond(404, {})
            self.respond(200, copy.deepcopy(value))
    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            data = bytearray()
            while True:
                size = int(self.rfile.readline().strip().split(b";")[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                data.extend(self.rfile.read(size))
                self.rfile.read(2)
        else:
            data = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        return json.loads(data or b"{}")

    def do_POST(self):
        if not self.auth():
            return
        path, directory = self.context()
        body = self.read_body()
        with lock:
            if path == '/session':
                sid = 'ses_' + uuid.uuid4().hex
                value = {'id': sid, 'directory': directory, 'title': body['title'], 'time': {'created': now(), 'updated': now()}}
                state['sessions'][sid] = value
                state['messages'][sid] = []
                save()
                return self.respond(200, value)
            parts = path.split('/')
            if parts[1] in ['permission', 'question']:
                kind = 'permissions' if parts[1] == 'permission' else 'questions'
                pending = state[kind].get(parts[2])
                if not pending or state['sessions'][pending['sessionID']]['directory'] != directory:
                    return self.respond(404, {})
                del state[kind][parts[2]]
                waiters[parts[2]].set()
                save()
                return self.respond(200, True)
            if parts[1] != 'session':
                return self.respond(404, {})
            sid = parts[2]
            if sid not in state['sessions'] or state['sessions'][sid]['directory'] != directory:
                return self.respond(404, {})
            if parts[3] == 'abort':
                state['status'][sid] = 'idle'
                for request_id, event in waiters.items():
                    if any(state[k].get(request_id, {}).get('sessionID') == sid for k in ['questions', 'permissions']):
                        event.set()
                return self.respond(200, True)
            if parts[3] != 'prompt_async':
                return self.respond(404, {})
            if state['status'].get(sid) == 'busy':
                return self.respond(409, {})
            text = body['parts'][0]['text']
            if '[unknown]' in text:
                self.connection.shutdown(socket.SHUT_RDWR)
                self.close_connection = True
                return
            state['submissions'] += 1
            mid = body['messageID']
            state['messages'][sid].append({'info': {'id': mid, 'sessionID': sid, 'role': 'user', 'time': {'created': now()}}, 'parts': [{'id': 'prt_' + uuid.uuid4().hex, 'type': 'text', 'text': text}]})
            state['status'][sid] = 'busy'
            save()
            emit(directory, 'session.status', {'sessionID': sid, 'status': {'type': 'busy'}})
            threading.Thread(target=finish, args=(sid, mid, text), daemon=True).start()
            if '[drop-response]' in text:
                self.connection.shutdown(socket.SHUT_RDWR)
                self.close_connection = True
                return
            self.respond(204)
    def events(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('Connection', 'close')
        self.end_headers()
        subscriber = queue.Queue()
        with lock:
            subscribers.append(subscriber)
        subscriber.put({'payload': {'type': 'server.connected', 'properties': {}}})
        try:
            while True:
                try:
                    event = subscriber.get(timeout=1)
                except queue.Empty:
                    event = {'payload': {'type': 'server.heartbeat', 'properties': {}}}
                if event is None:
                    break
                data = ('data: ' + json.dumps(event, ensure_ascii=False) + '\n\n').encode()
                for offset in range(0, len(data), 7):
                    self.wfile.write(data[offset:offset+7])
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            with lock:
                subscribers.remove(subscriber)
            self.close_connection = True

ThreadingHTTPServer(('127.0.0.1', port), Handler).serve_forever()
