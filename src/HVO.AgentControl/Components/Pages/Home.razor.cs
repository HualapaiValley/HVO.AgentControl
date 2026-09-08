using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Components.Pages;

public partial class Home
{
    [Inject] private IJSRuntime JavaScript { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "worker")] public string? WorkerId { get; set; }
    private IJSObjectReference? module;
    private WorkerDetail? detail;
    private string? selectedId, appliedWorkerId, promptRequestId;
    private string outcomeCommandId = "";
    private long outcomeExpectedRevision;
    private bool includeGuidance, hostOperations;
    private int progressMinutes = 5;
    private string promptText = "", outcome = "ReportedComplete", evidence = "";
    private readonly ConversationSelection selection = new();
    private readonly Dictionary<string, string> answers = [];
    private readonly Dictionary<string, HashSet<string>> choices = [];
    private readonly Dictionary<string, string> replyIds = [];
    private readonly Dictionary<string, CommandRecord> commandBodies = [];
    private readonly List<TranscriptMessage> olderMessages = [];
    private readonly AsyncLocal<string?> historyBeforeId = new();

    protected override async Task OnParametersSetAsync()
    {
        if (appliedWorkerId == WorkerId) return;
        appliedWorkerId = WorkerId; selectedId = WorkerId; selection.Change(selectedId); detail = null; hostOperations = false;
        error = null; notice = null; outcome = "ReportedComplete"; evidence = ""; outcomeExpectedRevision = 0;
        olderMessages.Clear(); answers.Clear(); choices.Clear(); replyIds.Clear(); commandBodies.Clear(); promptText = ""; promptRequestId = null;
        await Refresh();
    }
    protected override async Task SnapshotChanged()
    {
        selectedId ??= WorkerId;
        if (selectedId is null) return;
        var current = selection.Capture(selectedId);
        if (!snapshot!.Workers.Any(x => x.Id == current.WorkerId))
        {
            detail = null; error = "This worker is unavailable. Choose another agent conversation."; return;
        }
        try
        {
            var loaded = await ReadDetail(current.WorkerId!);
            if (!selection.IsCurrent(current)) return;
            await HydrateOutstanding(loaded);
            if (!selection.IsCurrent(current)) return;
            var isHostOperations = loaded.Worker.Role == SessionRoles.Coordinator &&
                await Store.Read(db => db.ControlSessions.AnyAsync(x => x.WorkerId == loaded.Worker.Id && x.ScopeKind == "HostOperations"));
            if (!selection.IsCurrent(current)) return;
            detail = loaded; hostOperations = isHostOperations;
            var settled = detail.Commands.Where(x => x.Kind == "Prompt" && x.State is Delivery.Finished or Delivery.Failed or Delivery.Cancelled).ToList();
            if (!settled.Any(x => x.Id == outcomeCommandId))
            {
                if (outcomeCommandId.Length > 0)
                {
                    outcomeCommandId = "";
                    outcomeExpectedRevision = 0;
                    outcome = "ReportedComplete";
                    evidence = "";
                }
                else
                {
                    outcomeCommandId = settled.OrderByDescending(x => x.CreatedAt).FirstOrDefault()?.Id ?? "";
                    outcomeExpectedRevision = detail.Worker.Revision;
                }
            }
        }
        catch when (!selection.IsCurrent(current)) { }
    }
    private void SelectConversation(ChangeEventArgs args)
    {
        if (args.Value?.ToString() is { Length: > 0 } id) Navigation.NavigateTo(ConversationUrl(id));
    }
    private void PinOutcomeRevision() => outcomeExpectedRevision = detail?.Worker.Revision ?? 0;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Home.razor.js");
        if (module is not null) await module.InvokeVoidAsync("observeTranscript", selectedId);
    }
    private Task SendPrompt() => Execute(async () =>
    {
        var current = SelectedDetail();
        promptRequestId ??= Guid.NewGuid().ToString();
        var command = await Store.Prompt(current.Worker.Id, new(promptRequestId, promptText, current.Worker.Revision, IncludeGuidance: includeGuidance, ProgressMinutes: includeGuidance && progressMinutes > 0 ? progressMinutes : null));
        notice = "Instruction queued. You can leave this page while the worker runs."; promptText = ""; promptRequestId = null;
    });
    private Task StatusInquiry() => Execute(async () =>
    {
        var current = SelectedDetail();
        await Store.Prompt(current.Worker.Id, new(Guid.NewGuid().ToString(), "Report current progress, completed validation, blockers, and the next step. Do not start unrelated work.", current.Worker.Revision, StatusInquiry: true));
        notice = "A progress inquiry is queued; current observed state remains visible above.";
    });
    private Task Abort() => Execute(async () => { await Store.Abort(selectedId!, Guid.NewGuid().ToString()); notice = "Cancellation request recorded. Watch delivery and native state for the result."; });
    private Task RecordOutcome() => Execute(async () =>
    {
        await Store.SetOutcome(selectedId!, new(outcomeCommandId, outcomeExpectedRevision, outcome, evidence)); evidence = "";
    });
    private string ReplyId(PendingRequest request)
    {
        if (replyIds.TryGetValue(request.Id, out var prior) && detail?.Commands.Any(x => x.Id == prior && x.State == Delivery.Cancelled) == true)
            replyIds.Remove(request.Id);
        if (!replyIds.TryGetValue(request.Id, out var id)) replyIds[request.Id] = id = Guid.NewGuid().ToString();
        return id;
    }
    private Task ReplyPermission(PendingRequest request, string decision) => Execute(async () =>
        { await Store.Reply(new(ReplyId(request), request.Id, decision, null)); });
    private Task ReplyQuestion(PendingRequest request, bool reject) => Execute(async () =>
    {
        var values = Questions(request).Select(x => Choices(request.Id, x.Index).Concat(Answer(request.Id, x.Index).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct().ToArray()).ToArray();
        if (!reject && values.Any(x => x.Length == 0)) throw new ControlException("Answer each question or decline the request.");
        await Store.Reply(new(ReplyId(request), request.Id, null, values, reject));
    });
    private string Answer(string id, int index) => answers.GetValueOrDefault(id + ":" + index, "");
    private void SetAnswer(string id, int index, string text) => answers[id + ":" + index] = text;
    private static NativeQuestion[] Questions(PendingRequest request) => InteractiveRequests.Questions(request.Json);
    private HashSet<string> Choices(string id, int index)
    {
        var key = id + ":" + index;
        if (!choices.TryGetValue(key, out var values)) choices[key] = values = [];
        return values;
    }
    private void Choose(string id, NativeQuestion question, string value, bool selected)
    {
        var values = Choices(id, question.Index);
        if (!question.Multiple) { values.Clear(); SetAnswer(id, question.Index, ""); }
        if (selected) values.Add(value); else values.Remove(value);
    }
    private void CustomAnswer(string id, NativeQuestion question, string text)
    {
        if (!question.Multiple) Choices(id, question.Index).Clear();
        SetAnswer(id, question.Index, text);
    }
    private Task OlderHistory() => Execute(async () =>
    {
        var current = SelectedDetail();
        var selected = selection.Capture(current.Worker.Id);
        var cursor = olderMessages.Concat(current.Messages).OrderBy(x => x.NativeCreatedAt).ThenBy(x => x.NativeId, StringComparer.Ordinal).FirstOrDefault();
        if (cursor is null)
        {
            notice = "No earlier messages are stored locally. Native OpenCode history remains on the runtime.";
            return;
        }
        WorkerDetail history;
        try { history = await ReadDetail(current.Worker.Id, cursor.NativeCreatedAt, cursor.NativeId); }
        catch when (!selection.IsCurrent(selected)) { return; }
        if (!selection.IsCurrent(selected)) return;
        olderMessages.AddRange(history.Messages.Where(x => olderMessages.All(y => y.NativeId != x.NativeId)));
        if (history.Messages.Count == 0) notice = "No earlier messages are stored locally. Native OpenCode history remains on the runtime.";
    });
    private WorkerDetail SelectedDetail()
    {
        var current = detail;
        if (current is null || current.Worker.Id != selectedId) throw new ControlException("The selected conversation changed. Wait for its details before taking an action.");
        return current;
    }
    protected virtual Task<WorkerDetail> ReadDetail(string workerId, long? before = null) => Store.Detail(workerId, before, historyBeforeId.Value);
    protected virtual async Task<WorkerDetail> ReadDetail(string workerId, long? before, string beforeId)
    {
        var prior = historyBeforeId.Value;
        historyBeforeId.Value = beforeId;
        try { return await ReadDetail(workerId, before); }
        finally { historyBeforeId.Value = prior; }
    }
    private async Task HydrateOutstanding(WorkerDetail loaded)
    {
        var summaries = loaded.Commands.Where(x => x.State == Delivery.Queued || Delivery.InFlight(x.State) || x.State == Delivery.Unknown)
            .OrderBy(x => x.QueueOrder).Take(100).ToArray();
        var missing = summaries.Where(x => !commandBodies.TryGetValue(x.Id, out var body) || body.UpdatedAt != x.UpdatedAt).Select(x => x.Id);
        foreach (var body in await Store.CommandBodies(missing)) commandBodies[body.Id] = body;
        foreach (var summary in summaries)
        {
            if (!commandBodies.TryGetValue(summary.Id, out var body)) continue;
            var index = loaded.Commands.FindIndex(x => x.Id == summary.Id);
            if (index >= 0) loaded.Commands[index] = body;
        }
    }
    private Task LoadCommandBody(CommandRecord command) => Execute(async () =>
    {
        var body = await Store.Command(command.Id);
        commandBodies[body.Id] = body;
        var index = detail!.Commands.FindIndex(x => x.Id == body.Id);
        if (index >= 0) detail.Commands[index] = body;
    });
    private static string Timestamp(long time) => DateTimeOffset.FromUnixTimeMilliseconds(time).ToString("MMM d HH:mm:ss 'UTC'");
    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private string ActiveModel
    {
        get
        {
            foreach (var message in (detail?.Messages ?? []).Where(x => x.Role == "assistant").OrderByDescending(x => x.NativeCreatedAt))
            {
                using var document = JsonDocument.Parse(message.Json);
                var info = document.RootElement.GetProperty("info");
                if (info.TryGetProperty("modelID", out var model) && info.TryGetProperty("providerID", out var provider)) return provider.GetString() + "/" + model.GetString();
            }
            return "Not observed";
        }
    }
    private static string CommandModel(CommandRecord command)
    {
        if (command.Kind != "Prompt" || command.Payload.Length == 0) return "";
        var prompt = Json.Read<PromptInput>(command.ExecutionPayload.Length > 0 ? command.ExecutionPayload : command.Payload);
        return $"{prompt.ProviderId}/{prompt.ModelId} {prompt.Agent} {prompt.Variant}";
    }
    private static string CommandText(CommandRecord command) => command.Kind == "Prompt" && command.Payload.Length > 0
        ? Json.Read<PromptInput>(command.Payload).Text : "";
    private static List<(string Label, string Text)> Parts(TranscriptMessage message)
    {
        using var doc = JsonDocument.Parse(message.Json);
        var result = new List<(string, string)>();
        foreach (var part in doc.RootElement.GetProperty("parts").EnumerateArray())
        {
            var type = part.GetProperty("type").GetString() ?? "part";
            if (part.TryGetProperty("text", out var text)) result.Add((type, text.GetString() ?? ""));
            else if (type == "tool")
            {
                var state = part.GetProperty("state");
                var sections = new List<string>();
                if (state.TryGetProperty("input", out var input))
                    sections.Add(input.TryGetProperty("command", out var command) ? command.GetString() ?? "" : Pretty(input.GetRawText()));
                if (state.TryGetProperty("output", out var output)) sections.Add(output.ValueKind == JsonValueKind.String ? output.GetString() ?? "" : Pretty(output.GetRawText()));
                if (state.TryGetProperty("error", out var errorValue)) sections.Add(errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() ?? "" : Pretty(errorValue.GetRawText()));
                result.Add(($"{part.GetProperty("tool").GetString()} · {state.GetProperty("status").GetString()}", string.Join("\n\n", sections)));
            }
            else if (type == "step-finish" && part.TryGetProperty("tokens", out var tokens))
                result.Add(("usage", $"Input {tokens.GetProperty("input")} · Output {tokens.GetProperty("output")}"));
            else if (type == "file") result.Add(("attachment", part.TryGetProperty("filename", out var filename) ? filename.GetString() ?? "file" : "file"));
        }
        if (doc.RootElement.GetProperty("info").TryGetProperty("error", out var error)) result.Add(("error", Pretty(error.GetRawText())));
        return result;
    }
    public override async ValueTask DisposeAsync()
    {
        selection.Invalidate();
        await base.DisposeAsync();
        if (module is not null) try { await module.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
