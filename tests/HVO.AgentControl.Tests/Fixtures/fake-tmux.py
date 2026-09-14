#!/usr/bin/python3
"""Fake tmux binary for TmuxAttachLauncherTests.

This is a build-copied canonical fixture rather than a file each test writes,
for the same reason as `fake_acp.py` (#232): writing an executable while other
tests are starting processes loses a race. `File.WriteAllText` holds a writable
descriptor briefly, a concurrent `Process.Start` on another thread forks and
inherits it, and the `execve` of this file then fails with `ETXTBSY`
("Text file busy") - measured at roughly 2% of starts under a parallel process
load. That surfaced as a rare, unreproducible failure on the very first
`EnsureAsync` call.

Each test instead symlinks to this one file from its own temporary directory,
so nothing ever opens it for writing. `ROOT` is therefore derived from the
invoked (unresolved) path rather than baked in at write time: the kernel passes
the symlink path to the interpreter, so `sys.argv[0]`'s directory is the calling
test's private directory.

Behaviour reproduces the real tmux 3.4 target grammar that issue #238 exposed,
verified against tmux 3.4 on a private socket:
  * '=' exact-match syntax is honoured only by commands that resolve a session
    through the fuzzy target parser (has-session, show-environment, list-panes,
    kill-session, new-window).
  * set-option treats '=' as part of a literal name and fails with
    "no such session: =name".
  * show-options -qv treats it the same way but stays SILENT with exit code 0,
    which is why the original defect never surfaced in tests.
Session ids ("$N") always resolve exactly for every command.
"""
import json, os, sys

ROOT = os.path.dirname(os.path.abspath(sys.argv[0]))
args = sys.argv[1:]
with open(os.path.join(ROOT, 'calls.jsonl'), 'a') as log:
    log.write(json.dumps({'Args': args, 'Env': dict(os.environ)}) + '\n')
path = os.path.join(ROOT, 'state.json')
others_path = os.path.join(ROOT, 'others.json')
arm_path = os.path.join(ROOT, 'arm')
arm_seen_path = os.path.join(ROOT, 'arm-seen')
state = json.load(open(path)) if os.path.exists(path) else None
others = json.load(open(others_path)) if os.path.exists(others_path) else []
command = args[0]
failure = os.path.join(ROOT, 'failure')
if os.path.exists(failure) and open(failure).read() == command: sys.exit(2)

def save():
    if state is not None: json.dump(state, open(path, 'w'))
    json.dump(others, open(others_path, 'w'))

def sessions():
    return ([state] if state is not None else []) + others

def target(flag='-t'):
    return args[args.index(flag) + 1] if flag in args else None

def resolve(value, exact_ok):
    # Strip the trailing window component of a 'session:' target.
    value = value[:-1] if value.endswith(':') else value
    if value.startswith('$'):
        found = [s for s in sessions() if s['id'] == value]
        return found[0] if found else None
    if value.startswith('='):
        if not exact_ok: return None  # '=' is a literal name character here.
        found = [s for s in sessions() if s['name'] == value[1:]]
        return found[0] if found else None
    found = [s for s in sessions() if s['name'] == value]
    if found: return found[0]
    found = [s for s in sessions() if s['name'].startswith(value)]
    return found[0] if len(found) == 1 else None

if command == 'has-session':
    sys.exit(0 if resolve(target(), True) else 1)
elif command == 'list-sessions':
    if not sessions(): sys.exit(1)
    assert args[args.index('-F') + 1] == '#{session_id} #{session_name}'
    for s in sessions(): print(s['id'] + ' ' + s['name'])
elif command == 'show-environment':
    s = resolve(target(), True)
    if not s: sys.exit(1)
    print('AGENTCONTROL_OWNER=' + s['owner'])
    # Deterministic server restart: once list-panes has passed (the last probe
    # before the recovery owner recheck), replace this server after the recheck
    # succeeds with an unrelated session that reuses id $0 under another name.
    if os.path.exists(arm_seen_path):
        os.remove(arm_seen_path)
        state = {'id': '$0', 'name': open(arm_path).read().strip(), 'owner': 'foreign', 'panes': {'%50': 0}, 'identity': 'foreign:%50'}
        save()
elif command == 'show-options':
    s = resolve(target(), False)
    if s is None: sys.exit(0)  # -qv: silent and successful.
    print(s.get('identity', ''))
elif command == 'set-option':
    s = resolve(target(), False)
    if s is None:
        sys.stderr.write('no such session: ' + str(target()) + '\n')
        sys.exit(1)
    s['identity'] = args[-1]
    save()
elif command in ('select-window', 'select-pane'):
    owner = [s for s in sessions() if args[-1] in s['panes']]
    if not owner: sys.exit(1)
    owner[0]['selectedWindow' if command == 'select-window' else 'selectedPane'] = args[-1]
    save()
elif command == 'list-panes':
    s = resolve(target(), True)
    if not s: sys.exit(1)
    for pane, dead in s['panes'].items(): print(pane + ' ' + str(dead))
    if os.path.exists(arm_path):
        open(arm_seen_path, 'w').write('1')
elif command in ('new-session', 'new-window'):
    assert args[args.index('-F') + 1] == '#{session_id} #{pane_id}' and '-P' in args
    if command == 'new-session':
        name = args[args.index('-s') + 1]
        if any(s['name'] == name for s in sessions()): sys.exit(1)
        used = {s['id'] for s in sessions()}
        ident = next('$' + str(n) for n in range(100) if '$' + str(n) not in used)
        owner = args[args.index('-e') + 1].split('=', 1)[1]
        state = {'id': ident, 'name': name, 'owner': owner, 'panes': {}, 'identity': ''}
        s = state
        pane = '%1'
    else:
        s = resolve(target(), True)
        if not s: sys.exit(1)
        pane = '%' + str(s.get('nextPane', 2))
        s['nextPane'] = int(pane[1:]) + 1
    s['panes'][pane] = 0
    save()
    print(s['id'] + ' ' + pane)
elif command == 'kill-session':
    s = resolve(target(), True)
    if not s: sys.exit(1)
    if state is not None and s is state:
        state = None
        os.remove(path)
    else:
        others.remove(s)
    save()
else:
    sys.exit(2)
