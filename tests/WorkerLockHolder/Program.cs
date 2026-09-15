using HVO.AgentControl.Worker;

if (args is not [var controlDirectory]) return 64;
var options = new WorkerOptions(controlDirectory, "worker-test", "controller-test", Path.Combine(controlDirectory, "bridge.sock"), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(20));
using var store = new WorkerStore(options);
Console.Out.WriteLine("locked");
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
