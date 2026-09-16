using System.Diagnostics;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerSupervisorTests
{
    [Fact]
    public void FixedSupervisorViewerOperationUsesExactAttachCredentialAndDescriptorContract()
    {
        if (!OperatingSystem.IsLinux()) return;
        var harness = """
import importlib.util, json, os, socket, struct, sys
spec=importlib.util.spec_from_file_location('worker_supervisor', sys.argv[1])
s=importlib.util.module_from_spec(spec); spec.loader.exec_module(s)
class Child:
 def __init__(self, command, **kwargs):
  self.command=command; self.kwargs=kwargs; self.pid=4321; self.state=None
 def poll(self): return self.state
 def terminate(self): self.state=0
 def kill(self): self.state=-9
 def wait(self, timeout=None): return self.state
s.children={'acp':Child([])}
captured=[]
def popen(command, **kwargs):
 child=Child(command, **kwargs); captured.append(child); return child
s.subprocess.Popen=popen
left,right=socket.socketpair()
try:
 right.sendall(b'{"operation":"viewer-start","sessionId":"ses-fixed","rows":31,"columns":101}\n')
 s.peer_uid=lambda _: s.BRIDGE_UID
 s.handle(left)
 data, anc, flags, address=right.recvmsg(4096, socket.CMSG_SPACE(4))
 response=json.loads(data)
 assert response['ok'] is True and response['viewerHandle']
 assert len(captured)==1
 child=captured[0]
 assert child.command==s.ATTACH_PREFIX+['ses-fixed']
 assert child.kwargs['preexec_fn'] is s.viewer_setup
 assert s.viewer_setup.__code__.co_argcount==0
 assert child.kwargs['env']['OPENCODE_SERVER_USERNAME']=='opencode'
 assert child.kwargs['env']['OPENCODE_SERVER_PASSWORD']==s.server_password
 assert not any('CONTROLLER' in key or 'KEY' in key for key in child.kwargs['env'])
 assert anc and anc[0][1]==socket.SCM_RIGHTS
 fd=struct.unpack('i', anc[0][2][:4])[0]; os.close(fd)
 assert s.viewer['master_fd'] is None
 extra_left,extra_right=socket.socketpair(); extra_right.sendall(b'{"operation":"viewer-start","sessionId":"ses-fixed"}\n'); s.handle(extra_left)
 assert json.loads(extra_right.recv(4096))['error']=='viewer-already-running'; extra_left.close(); extra_right.close()
 stop_left,stop_right=socket.socketpair(); stop_right.sendall(json.dumps({'operation':'viewer-stop','viewerHandle':response['viewerHandle']}).encode()+b'\n'); s.handle(stop_left)
 assert json.loads(stop_right.recv(4096))['ok'] is True and s.viewer is None; stop_left.close(); stop_right.close()
 assert s.children['acp'].poll() is None
finally:
 left.close(); right.close(); s.close_viewer()
""";
        RunHarness(harness);
    }

    /// <summary>
    /// The supervisor can no longer observe the PTY master once it is transferred
    /// to the bridge, but it still owns the viewer child and can report whether it
    /// is alive. That is what lets an unconfirmed stop be resolved as either
    /// genuinely reclaimed or genuinely uncertain, instead of being swallowed.
    /// </summary>
    [Fact]
    public void FixedSupervisorReportsExactViewerProcessStateForAnUncertainStop()
    {
        if (!OperatingSystem.IsLinux()) return;
        var harness = """
import importlib.util, json, os, socket, struct, sys
spec=importlib.util.spec_from_file_location('worker_supervisor', sys.argv[1])
s=importlib.util.module_from_spec(spec); spec.loader.exec_module(s)
class Child:
 def __init__(self, command, **kwargs):
  self.command=command; self.kwargs=kwargs; self.pid=4321; self.state=None
 def poll(self): return self.state
 def terminate(self): self.state=0
 def kill(self): self.state=-9
 def wait(self, timeout=None): return self.state
s.children={'acp':Child([])}
captured=[]
def popen(command, **kwargs):
 child=Child(command, **kwargs); captured.append(child); return child
s.subprocess.Popen=popen
s.peer_uid=lambda _: s.BRIDGE_UID

def ask(request):
 left,right=socket.socketpair()
 try:
  right.sendall(json.dumps(request).encode()+b'\n'); s.handle(left); return json.loads(right.recv(4096))
 finally:
  left.close(); right.close()

left,right=socket.socketpair()
try:
 right.sendall(b'{"operation":"viewer-start","sessionId":"ses-status"}\n')
 s.handle(left)
 data, anc, flags, address=right.recvmsg(4096, socket.CMSG_SPACE(4))
 handle=json.loads(data)['viewerHandle']
 fd=struct.unpack('i', anc[0][2][:4])[0]; os.close(fd)
 assert s.viewer['master_fd'] is None
 alive=ask({'operation':'viewer-status','viewerHandle':handle})
 assert alive=={'ok': True, 'state': 'running'}, alive
 assert ask({'operation':'viewer-status','viewerHandle':'other'})=={'ok': True, 'state': 'absent'}
 assert ask({'operation':'viewer-status'})['error']=='invalid-request'
 captured[0].state=0
 gone=ask({'operation':'viewer-status','viewerHandle':handle})
 assert gone=={'ok': True, 'state': 'absent'}, gone
 assert s.viewer is None
 assert s.children['acp'].poll() is None
finally:
 left.close(); right.close(); s.close_viewer()
""";
        RunHarness(harness);
    }

    private static void RunHarness(string harness)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var supervisor = Path.Combine(root, "src/container/worker-supervisor.py");
        var temporary = Path.Combine(Path.GetTempPath(), "worker-supervisor-test-" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(temporary, harness);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, ArgumentList = { temporary, supervisor } })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "Python supervisor harness timed out.");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(temporary); }
    }
}
