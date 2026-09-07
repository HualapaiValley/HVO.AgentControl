#!/usr/bin/env bash
# This talks only to the disposable target created by NativeReleaseTests.
set -euo pipefail
cd "$(dirname "$0")/../.."
docker cp .fixture/secrets/server-password hvo-agentcontrol-fixture-b:/tmp/hvo-probe-password >/dev/null
docker cp .fixture/native-sessions.json hvo-agentcontrol-fixture-b:/tmp/hvo-native-sessions.json >/dev/null
docker exec -i hvo-agentcontrol-fixture-b python3 - <<'PY'
import base64, json, urllib.request, urllib.error, time
password = open('/tmp/hvo-probe-password').read().strip()
sessions = json.load(open('/tmp/hvo-native-sessions.json'))
def request(path, body=None):
    req = urllib.request.Request('http://127.0.0.1:9496' + path, data=None if body is None else json.dumps(body).encode(), headers={'Authorization': 'Basic ' + base64.b64encode(('opencode:' + password).encode()).decode(), 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=30) as response:
        data = response.read()
        return response.status, json.loads(data) if data else None
session = sessions['sessionA']['id']
scope = '?directory=%2Fhome%2Fagent%2Fworkspaces%2Fa'
message_id = 'msg_' + format((int(time.time()*1000)<<12)&0xffffffffffff, '012x') + 'NativeProbe001'
payload = {'messageID': message_id, 'model': {'providerID': 'opencode', 'modelID': 'big-pickle'}, 'noReply': True, 'parts': [{'type':'text','text': 'Native contract persistence probe; do not execute tools.'}]}
status, first = request('/session/' + session + '/message' + scope, payload)
_, history = request('/session/' + session + '/message' + scope)
status2, second = request('/session/' + session + '/message' + scope, payload)
_, after = request('/session/' + session + '/message' + scope)
print(json.dumps({'version': request('/global/health')[1]['version'], 'noReplyStatus':status, 'callerIdPersisted':any(x['info']['id']==message_id for x in history), 'repeatedStatus':status2,'messagesBefore':len(history),'messagesAfter':len(after),'partsBefore':len(first['parts']),'persistedPartsAfter':len(next(x for x in after if x['info']['id']==message_id)['parts']), 'pendingQuestions':request('/question'+scope)[1], 'pendingPermissions':request('/permission'+scope)[1]}, indent=2))
# An explicit bounded task on the advertised anonymous provider. Failure is evidence, not a passed inference test.
payload.pop('noReply')
payload['messageID'] = 'msg_' + format((int(time.time()*1000)<<12)&0xffffffffffff, '012x') + 'NativeProbe002'
payload['parts'][0]['text'] = 'In this disposable repository, create hello.txt containing Hello AgentControl, then run cat hello.txt. Report the output and stop. Do not commit or access other directories.'
print('anonymousProviderSubmission', request('/session/' + session + '/prompt_async' + scope, payload)[0])
for attempt in range(40):
    time.sleep(1)
    _, history = request('/session/' + session + '/message' + scope)
    assistants=[x for x in history if x['info'].get('parentID')==payload['messageID']]
    if any(x['info'].get('finish') == 'stop' or 'error' in x['info'] for x in assistants):
        print(json.dumps({'anonymousProviderResult':assistants}, indent=2))
        break
else:
    print(json.dumps({'anonymousProviderResult':'No completed response within 40 seconds; inspect native error/retry state.'}))
PY
