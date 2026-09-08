using System.Reflection;
using HVO.AgentControl.Components.Pages;
using Microsoft.JSInterop;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TerminalTests
{
    [Fact]
    public async Task ImportFailureResetsStatusAndAllowsSuccessfulRetry()
    {
        var js = new FailingImportRuntime();
        var terminal = CreateTerminal(js);

        await Invoke(terminal, "Open");

        Assert.False(Get<bool>(terminal, "connected"));
        Assert.Equal("Unable to open terminal. Check runtime SSH access and sign-in.", Get<string>(terminal, "status"));

        await Invoke(terminal, "Open");

        Assert.True(Get<bool>(terminal, "connected"));
        Assert.Equal("Connecting…", Get<string>(terminal, "status"));
        Assert.Equal(1, js.Module.OpenCount);
    }

    [Fact]
    public async Task CloseBeforeImportCompletesDisposesStaleModuleWithoutOpening()
    {
        var js = new DelayedImportRuntime();
        var terminal = CreateTerminal(js);

        var opening = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(1);
        await Invoke(terminal, "Close");
        var stale = js.CompleteImport(0);
        await opening;

        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(0, stale.OpenCount);
        Assert.Null(GetModule(terminal));
    }

    [Fact]
    public async Task DisposeBeforeImportCompletesDisposesStaleModuleIdempotentlyWithoutOpening()
    {
        var js = new DelayedImportRuntime();
        var terminal = CreateTerminal(js);

        var opening = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(1);
        await terminal.DisposeAsync();
        var stale = js.CompleteImport(0);
        await opening;
        await terminal.DisposeAsync();

        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(0, stale.OpenCount);
        Assert.Null(GetModule(terminal));
    }

    [Fact]
    public async Task CloseThenReopenOnlyOpensNewestGenerationAndDisposesStaleImport()
    {
        var js = new DelayedImportRuntime();
        var terminal = CreateTerminal(js);

        var firstOpen = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(1);
        await Invoke(terminal, "Close");
        var secondOpen = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(2);

        var current = js.CompleteImport(1);
        await secondOpen;
        var stale = js.CompleteImport(0);
        await firstOpen;
        await terminal.DisposeAsync();
        await terminal.DisposeAsync();

        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(0, stale.OpenCount);
        Assert.Equal(1, current.OpenCount);
        Assert.Equal(1, current.CloseCount);
        Assert.Equal(1, current.DisposeCount);
        Assert.Null(GetModule(terminal));
    }

    [Fact]
    public async Task StaleImportFailureAfterReopenDoesNotResetConnectedState()
    {
        var js = new DelayedImportRuntime();
        var terminal = CreateTerminal(js);

        var firstOpen = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(1);
        await Invoke(terminal, "Close");
        var secondOpen = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(2);

        js.CompleteImport(1);
        await secondOpen;
        js.FailImport(0);
        await firstOpen;

        Assert.True(Get<bool>(terminal, "connected"));
        Assert.Equal("Connecting…", Get<string>(terminal, "status"));
    }

    [Fact]
    public async Task StaleImportFailureAfterDisposeDoesNotResetStateOrEscape()
    {
        var js = new DelayedImportRuntime();
        var terminal = CreateTerminal(js);

        var opening = Invoke(terminal, "Open");
        await js.WaitForImportsAsync(1);
        await terminal.DisposeAsync();
        js.FailImport(0);

        await opening;
        Assert.True(Get<bool>(terminal, "connected"));
    }

    private static Terminal CreateTerminal(IJSRuntime js)
    {
        var terminal = new Terminal();
        typeof(Terminal).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(terminal, js);
        return terminal;
    }

    private static async Task Invoke(Terminal terminal, string name)
    {
        await (Task)typeof(Terminal).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(terminal, null)!;
    }

    private static IJSObjectReference? GetModule(Terminal terminal) => (IJSObjectReference?)typeof(Terminal).GetField("module", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal);

    private static TValue Get<TValue>(Terminal terminal, string name) => (TValue)typeof(Terminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;

    private sealed class DelayedImportRuntime : IJSRuntime
    {
        private readonly List<TaskCompletionSource<IJSObjectReference>> imports = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            var completion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
            imports.Add(completion);
            return new ValueTask<TValue>(AwaitImport<TValue>(completion.Task, cancellationToken));
        }

        private static async Task<TValue> AwaitImport<TValue>(Task<IJSObjectReference> import, CancellationToken cancellationToken)
            => (TValue)(object)await import.WaitAsync(cancellationToken);

        public Task WaitForImportsAsync(int count)
        {
            Assert.True(imports.Count >= count);
            return Task.CompletedTask;
        }

        public FakeModule CompleteImport(int index)
        {
            var module = new FakeModule();
            imports[index].SetResult(module);
            return module;
        }

        public void FailImport(int index) => imports[index].SetException(new JSException("import failed"));
    }

    private sealed class FailingImportRuntime : IJSRuntime
    {
        public FakeModule Module { get; } = new();
        private bool failed;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            if (!failed)
            {
                failed = true;
                return ValueTask.FromException<TValue>(new JSException("import failed"));
            }

            return ValueTask.FromResult((TValue)(object)Module);
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "open") OpenCount++;
            if (identifier == "close") CloseCount++;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
