"""Create an owner password on the selected Docker host without printing it."""
import argparse
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--context', default='default')
args = parser.parse_args()
docker = ['docker', '--context', args.context]
subprocess.run(docker + ['volume', 'create', 'agentcontrol-v2-secrets'], check=True)
code = '''import os,secrets
path='/secrets/owner-password'
if not os.path.exists(path):
 fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
 with os.fdopen(fd,'w') as stream: stream.write(secrets.token_urlsafe(32)+'\\n')
 os.chown(path,1000,1000)
 print('Created owner password (contents not printed).')
else: print('Existing owner password preserved.')
'''
subprocess.run(docker + ['run', '--rm', '--user', 'root', '--entrypoint', 'python3',
    '--mount', 'type=volume,source=agentcontrol-v2-secrets,target=/secrets',
    'agentcontrol-v2-control', '-c', code], check=True)
