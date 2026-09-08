using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class PreparedCheckoutVerificationTests
{
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task RequestIdentityAndRuntimeRevisionAreStableBeforeTransportDispatch()
    {
        await using var app = new TestApp();
        var runtime = await ExistingMachine(app);
        var input = Input(runtime);

        var command = await app.Store.VerifyPreparedCheckout(input);
        Assert.Equal(Delivery.Queued, command.State);
        Assert.Equal(command.Id, (await app.Store.VerifyPreparedCheckout(input)).Id);

        await Assert.ThrowsAsync<ControlException>(() => app.Store.VerifyPreparedCheckout(input with { Branch = "other" }));
        var stale = await Assert.ThrowsAsync<ControlException>(() => app.Store.VerifyPreparedCheckout(input with
        {
            Id = Id(),
            ExpectedRuntimeRevision = runtime.Revision - 1
        }));
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task UnsupportedEnvironmentReturnsTypedResultWithoutTransportWork()
    {
        await using var app = new TestApp();
        var runtime = await Runtime(app, RuntimeEnvironmentKind.ManagedDevcontainer);

        var command = await app.Store.VerifyPreparedCheckout(Input(runtime));

        Assert.Equal(Delivery.Finished, command.State);
        var result = Json.Read<PreparedCheckoutVerification>(command.ResultJson);
        Assert.Equal(PreparedCheckoutStatus.Unsupported, result.Status);
        Assert.Equal("unsupported_runtime_environment", result.Code);
    }

    [Fact]
    public async Task BackendRestartRequeuesReadOnlyVerificationWithItsOriginalIdentity()
    {
        await using var app = new TestApp();
        var runtime = await ExistingMachine(app);
        var input = Input(runtime);
        var command = await app.Store.VerifyPreparedCheckout(input);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.State = Delivery.Dispatching; return true; });

        await app.Store.Recover();

        var recovered = await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!);
        Assert.Equal(Delivery.Queued, recovered.State);
        Assert.Equal(Json.Write(input), recovered.Payload);
        Assert.Equal(command.Id, (await app.Store.VerifyPreparedCheckout(input)).Id);
    }

    [Fact]
    public async Task SupervisorPersistsTypedEvidenceAndRejectsRuntimeChangeBeforeProbe()
    {
        await using var app = new TestApp();
        var runtime = await ExistingMachine(app);
        var input = Input(runtime);
        var command = await app.Store.VerifyPreparedCheckout(input);
        var expected = new PreparedCheckoutVerification(PreparedCheckoutStatus.Verified, "verified", "matched",
            input.Directory, input.Directory, input.Repository, input.Branch, input.Head, true, ControlStore.Now);
        var transport = new FakeTransport(expected);

        await Dispatch(app, command, runtime, transport);

        var saved = await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!);
        Assert.Equal(Delivery.Finished, saved.State);
        Assert.Equal("verified", Json.Read<PreparedCheckoutVerification>(saved.ResultJson).Code);
        Assert.Equal(1, transport.Calls);

        runtime = await app.Store.Write(async db =>
        {
            var changed = (await db.Runtimes.FindAsync(runtime.Id))!;
            changed.Revision++;
            return changed;
        });
        var nextInput = Input(runtime) with { Id = Id() };
        var next = await app.Store.VerifyPreparedCheckout(nextInput);
        await app.Store.Write(async db => { (await db.Runtimes.FindAsync(runtime.Id))!.Revision++; return true; });
        await Dispatch(app, next, runtime, transport);

        var rejected = Json.Read<PreparedCheckoutVerification>((await app.Store.Read(async db => (await db.Commands.FindAsync(next.Id))!)).ResultJson);
        Assert.Equal(PreparedCheckoutStatus.Rejected, rejected.Status);
        Assert.Equal("runtime_changed", rejected.Code);
        Assert.Equal(1, transport.Calls);

        var current = await app.Store.Read(async db => (await db.Runtimes.FindAsync(runtime.Id))!);
        var racingInput = Input(current) with { Id = Id() };
        var racing = await app.Store.VerifyPreparedCheckout(racingInput);
        var racingTransport = new FakeTransport(expected, () => app.Store.Write(async db =>
        {
            (await db.Runtimes.FindAsync(runtime.Id))!.Revision++;
            return true;
        }));
        await Dispatch(app, racing, current, racingTransport);
        var discarded = Json.Read<PreparedCheckoutVerification>((await app.Store.Read(async db => (await db.Commands.FindAsync(racing.Id))!)).ResultJson);
        Assert.Equal(PreparedCheckoutStatus.Rejected, discarded.Status);
        Assert.Equal("runtime_changed", discarded.Code);
        Assert.Equal(1, racingTransport.Calls);
    }

    [Fact]
    public async Task OwnerApiUsesExistingCommandReceiptPattern()
    {
        await using var app = new TestApp();
        var runtime = await ExistingMachine(app);
        using var owner = await app.SignIn();

        var response = await owner.PostAsJsonAsync("/api/v1/workspaces/verify-prepared", Input(runtime));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("VerifyPreparedCheckout", (await response.Content.ReadFromJsonAsync<CommandRecord>())!.Kind);
    }

    private static VerifyPreparedCheckoutInput Input(RuntimeRecord runtime) => new(Id(), runtime.Id, runtime.Revision,
        "/home/agent/workspaces/task", "https://github.com/example/repository.git", "feature/task", new string('a', 40));

    private static async Task<RuntimeRecord> ExistingMachine(TestApp app) => await Runtime(app, RuntimeEnvironmentKind.ExistingMachine);

    private static async Task<RuntimeRecord> Runtime(TestApp app, string kind)
    {
        var command = await app.Store.SaveRuntimeForSetup(PersistenceTests.Profile(), Id());
        var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == command.ResultId));
        var host = await app.Store.CreateHost(new(Id(), Id(), "checkout-host"));
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, new(Id(), 0, runtime.Revision, host.Id, kind));
        return await app.Store.Read(async db => (await db.Runtimes.FindAsync(runtime.Id))!);
    }

    private static async Task Dispatch(TestApp app, CommandRecord command, RuntimeRecord runtime, IRuntimeTransport transport)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        await (Task)typeof(RuntimeSupervisor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(supervisor, [command, runtime, transport, CancellationToken.None])!;
    }

    private sealed class FakeTransport(PreparedCheckoutVerification result, Func<Task>? during = null) : IRuntimeTransport
    {
        public int Calls { get; private set; }
        public OpenCodeClient Api { get; } = new(new HttpClient(new NeverHandler()) { BaseAddress = new("http://review.invalid") });
        public bool Connected => true;
        public string Platform => "fake";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken token) => throw new NotSupportedException();
        public async Task<PreparedCheckoutVerification> VerifyPreparedCheckout(RuntimeRecord runtime, VerifyPreparedCheckoutInput input, CancellationToken token)
        { Calls++; if (during is not null) await during(); return result; }
        public Task StopOwnedServer(CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Api.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP request expected.");
    }
}
