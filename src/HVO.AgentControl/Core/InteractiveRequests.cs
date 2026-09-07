using System.Text.Json;

namespace HVO.AgentControl.Core;

public sealed record QuestionOption(string Label, string Description);
public sealed record NativeQuestion(int Index, string Text, QuestionOption[] Options, bool Multiple, bool Custom);

public static class InteractiveRequests
{
    public static NativeQuestion[] Questions(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("questions").EnumerateArray().Select((q, i) => new NativeQuestion(i,
            q.GetProperty("question").GetString() ?? "Question",
            q.GetProperty("options").EnumerateArray().Select(x => new QuestionOption(x.GetProperty("label").GetString() ?? "",
                x.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "")).ToArray(),
            q.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean(),
            !q.TryGetProperty("custom", out var custom) || custom.GetBoolean())).ToArray();
    }

    public static void ValidateAnswers(string json, string[][]? answers)
    {
        var questions = Questions(json);
        if (answers is null || answers.Length != questions.Length) throw new ControlException("Answer every question.", 400);
        foreach (var question in questions)
        {
            var values = answers[question.Index];
            if (values is null || values.Length == 0 || (!question.Multiple && values.Length != 1) || values.Any(string.IsNullOrWhiteSpace) || values.Distinct().Count() != values.Length)
                throw new ControlException("Choose one answer, or multiple answers only where allowed.", 400);
            if (!question.Custom && values.Any(value => !question.Options.Any(x => x.Label == value)))
                throw new ControlException("Choose an advertised answer for this question.", 400);
        }
    }

    public static string PermissionSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tool = root.TryGetProperty("permission", out var permission) ? permission.GetString() : "Tool access";
        var scope = root.TryGetProperty("patterns", out var patterns) ? string.Join("\n", patterns.EnumerateArray().Select(x => x.GetString())) : "See request details";
        return tool + "\n" + scope;
    }
}
