using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

public static class SseReader
{
    // StreamReader preserves UTF-8 decoder state across arbitrarily split network reads.
    // Both lines and complete frames are bounded before a JSON document is allocated.
    public static async IAsyncEnumerable<JsonElement> Read(Stream stream, int limit = 1_048_576,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var previousCr = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) yield break; // incomplete final frames are never accepted
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch == '\n' && previousCr) { previousCr = false; continue; }
                previousCr = ch == '\r';
                if (ch is '\n' or '\r')
                {
                    var value = line.ToString(); line.Clear();
                    if (value.Length == 0 && data.Length > 0)
                    {
                        using var document = JsonDocument.Parse(data.ToString());
                        data.Clear();
                        yield return document.RootElement.Clone();
                    }
                    else if (value == "data" || value.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var content = value.Length > 4 ? value[5..] : "";
                        if (content.StartsWith(' ')) content = content[1..];
                        if (data.Length + content.Length + 1 > limit) throw new InvalidDataException("SSE frame size limit exceeded.");
                        data.Append(content).Append('\n');
                    }
                }
                else
                {
                    if (line.Length >= limit) throw new InvalidDataException("SSE line size limit exceeded.");
                    line.Append(ch);
                }
            }
        }
    }
}
