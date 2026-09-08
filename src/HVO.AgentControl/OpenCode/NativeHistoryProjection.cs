using System.Buffers;
using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

public static class NativeHistoryProjection
{
    public const int DefaultWireLimit = 128_000_000;
    public const int DefaultProjectionLimit = 16_000_000;
    private const string OmissionReceiptProperty = "$hvoNativeHistoryOmission";

    public static async Task<JsonElement> ReadAsync(Stream source, int wireLimit = DefaultWireLimit,
        int projectionLimit = DefaultProjectionLimit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wireLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectionLimit);

        var input = ArrayPool<byte>.Shared.Rent(Math.Min(64 * 1024, wireLimit));
        try
        {
            var output = new ProjectionBuffer(projectionLimit);
            using var writer = new Utf8JsonWriter(output);
            var projector = new Projector(writer);
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 });
            var buffered = 0;
            var wireBytes = 0;
            var finalBlock = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!finalBlock && buffered < input.Length && wireBytes < wireLimit)
                {
                    var available = Math.Min(input.Length - buffered, wireLimit - wireBytes);
                    var read = await source.ReadAsync(input.AsMemory(buffered, available), cancellationToken);
                    if (read == 0) finalBlock = true;
                    else
                    {
                        buffered += read;
                        wireBytes += read;
                    }
                }

                var reader = new Utf8JsonReader(input.AsSpan(0, buffered), finalBlock, state);
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    projector.Process(ref reader, input.AsSpan(0, buffered));
                }

                var consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                buffered -= consumed;
                if (buffered > 0) input.AsSpan(consumed, buffered).CopyTo(input);

                if (finalBlock) break;
                if (wireBytes == wireLimit)
                {
                    var overflow = new byte[1];
                    if (await source.ReadAsync(overflow, cancellationToken) != 0)
                        throw new InvalidDataException("OpenCode history wire size limit exceeded.");
                    finalBlock = true;
                    continue;
                }

                if (buffered == input.Length)
                {
                    var size = Math.Min(checked(input.Length * 2), wireLimit);
                    if (size == input.Length)
                        throw new InvalidDataException("OpenCode history wire size limit exceeded.");
                    var expanded = ArrayPool<byte>.Shared.Rent(size);
                    input.AsSpan(0, buffered).CopyTo(expanded);
                    ArrayPool<byte>.Shared.Return(input);
                    input = expanded;
                }
            }

            projector.Complete();
            writer.Flush();
            using var document = JsonDocument.Parse(output.WrittenMemory);
            return document.RootElement.Clone();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    private sealed class Projector(Utf8JsonWriter writer)
    {
        private readonly List<Container> containers = [];
        private int suppressedDepth;
        private bool rootCompleted;

        public void Process(ref Utf8JsonReader reader, ReadOnlySpan<byte> input)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (containers.Count == 0 || containers[^1].Kind == ContainerKind.Array)
                    throw new InvalidDataException("OpenCode history contains a property outside an object.");
                var property = reader.GetString() ?? throw new InvalidDataException("OpenCode history contains a null property name.");
                var container = containers[^1];
                container.Property = property;
                if (container.Kind == ContainerKind.Info && property == OmissionReceiptProperty)
                    container.OmissionReceiptPropertySeen = true;
                if (suppressedDepth == 0) writer.WritePropertyName(property);
                return;
            }

            var parent = containers.Count == 0 ? null : containers[^1];
            var omit = suppressedDepth == 0 && IsUserDiff(parent);
            if (omit)
            {
                var message = parent!.Message!;
                WriteOmission();
                message.OmittedDiffs = true;
                parent.Property = null;
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    suppressedDepth = 1;
                    containers.Add(new(ContainerKind.Other, message));
                }
                return;
            }

            if (suppressedDepth > 0)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    suppressedDepth++;
                    containers.Add(new(ContainerKind.Other, parent?.Message));
                }
                else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                {
                    containers.RemoveAt(containers.Count - 1);
                    suppressedDepth--;
                }
                return;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    StartContainer(ContainerKind.Array, parent);
                    break;
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    StartContainer(ContainerKind.Object, parent);
                    break;
                case JsonTokenType.EndArray:
                    EndContainer(ContainerKind.Array);
                    writer.WriteEndArray();
                    break;
                case JsonTokenType.EndObject:
                    EndContainer(ContainerKind.Object);
                    writer.WriteEndObject();
                    break;
                case JsonTokenType.String:
                    CaptureMessageMetadata(parent, ref reader);
                    WriteRaw(ref reader, input);
                    CompleteValue(parent);
                    break;
                case JsonTokenType.Number:
                    WriteRaw(ref reader, input);
                    CompleteValue(parent);
                    break;
                case JsonTokenType.True:
                    writer.WriteBooleanValue(true);
                    CompleteValue(parent);
                    break;
                case JsonTokenType.False:
                    writer.WriteBooleanValue(false);
                    CompleteValue(parent);
                    break;
                case JsonTokenType.Null:
                    writer.WriteNullValue();
                    CompleteValue(parent);
                    break;
                default:
                    throw new InvalidDataException("OpenCode history contains an unsupported JSON token.");
            }
        }

        public void Complete()
        {
            if (!rootCompleted || containers.Count != 0 || suppressedDepth != 0)
                throw new InvalidDataException("OpenCode history JSON is incomplete.");
        }

        private void StartContainer(ContainerKind tokenKind, Container? parent)
        {
            if (containers.Count == 0)
            {
                if (rootCompleted || tokenKind != ContainerKind.Array)
                    throw new InvalidDataException("OpenCode history must be a JSON array.");
                containers.Add(new(ContainerKind.RootArray, null));
                return;
            }

            var kind = tokenKind;
            var message = parent!.Message;
            if (parent.Kind == ContainerKind.RootArray && tokenKind == ContainerKind.Object)
            {
                kind = ContainerKind.Message;
                message = new Message();
            }
            else if (parent.Kind == ContainerKind.Message && parent.Property == "info" && tokenKind == ContainerKind.Object)
            {
                kind = ContainerKind.Info;
            }
            else if (parent.Kind == ContainerKind.Info && parent.Property == "summary" && tokenKind == ContainerKind.Object)
            {
                kind = ContainerKind.Summary;
            }

            parent.Property = null;
            containers.Add(new(kind, message));
        }

        private void EndContainer(ContainerKind tokenKind)
        {
            if (containers.Count == 0)
                throw new InvalidDataException("OpenCode history contains an unexpected container end.");
            var ended = containers[^1];
            if (tokenKind == ContainerKind.Array && ended.Kind is not (ContainerKind.Array or ContainerKind.RootArray) ||
                tokenKind == ContainerKind.Object && ended.Kind is ContainerKind.Array or ContainerKind.RootArray)
                throw new InvalidDataException("OpenCode history contains mismatched containers.");
            if (ended.Kind == ContainerKind.Info && ended.Message?.OmittedDiffs == true)
            {
                if (ended.Message.Role != "user")
                    throw new InvalidDataException("OpenCode summary diffs cannot be omitted without a user role.");
                if (string.IsNullOrEmpty(ended.Message.Id) || string.IsNullOrEmpty(ended.Message.SessionId))
                    throw new InvalidDataException("OpenCode user summary diffs do not have a native message locator.");
                if (ended.OmissionReceiptPropertySeen)
                    throw new InvalidDataException("OpenCode user message conflicts with the reserved history omission receipt.");
                WriteOmissionReceipt(ended.Message);
            }
            containers.RemoveAt(containers.Count - 1);
            if (ended.Kind == ContainerKind.RootArray) rootCompleted = true;
        }

        private static void CompleteValue(Container? parent)
        {
            if (parent is not null) parent.Property = null;
        }

        private static void CaptureMessageMetadata(Container? parent, ref Utf8JsonReader reader)
        {
            if (parent?.Kind != ContainerKind.Info || parent.Message is null) return;
            switch (parent.Property)
            {
                case "id": parent.Message.Id = reader.GetString(); break;
                case "sessionID": parent.Message.SessionId = reader.GetString(); break;
                case "role":
                    parent.Message.Role = reader.GetString();
                    if (parent.Message.OmittedDiffs && parent.Message.Role != "user")
                        throw new InvalidDataException("OpenCode summary diffs cannot be omitted from a non-user message.");
                    break;
            }
        }

        private static bool IsUserDiff(Container? parent) =>
            parent?.Kind == ContainerKind.Summary && parent.Property == "diffs" &&
            parent.Message?.Role is null or "user";

        private void WriteOmission()
        {
            writer.WriteStartObject();
            writer.WriteString("$hvo", "omitted-native-user-summary-diffs");
            writer.WriteBoolean("omitted", true);
            writer.WriteStartObject("recovery");
            writer.WriteString("kind", "native-message");
            writer.WriteString("scope", "enclosing-info");
            writer.WriteString("receipt", OmissionReceiptProperty);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private void WriteOmissionReceipt(Message message)
        {
            writer.WriteStartObject(OmissionReceiptProperty);
            writer.WriteString("$hvo", "native-message-recovery");
            writer.WriteString("kind", "native-message");
            writer.WriteString("sessionID", message.SessionId);
            writer.WriteString("messageID", message.Id);
            writer.WriteEndObject();
        }

        private void WriteRaw(ref Utf8JsonReader reader, ReadOnlySpan<byte> input)
        {
            var start = checked((int)reader.TokenStartIndex);
            var end = checked((int)reader.BytesConsumed);
            writer.WriteRawValue(input[start..end], skipInputValidation: true);
        }

        private enum ContainerKind { Array, Object, RootArray, Message, Info, Summary, Other }

        private sealed class Container(ContainerKind kind, Message? message)
        {
            public ContainerKind Kind { get; } = kind;
            public Message? Message { get; } = message;
            public string? Property { get; set; }
            public bool OmissionReceiptPropertySeen { get; set; }
        }

        private sealed class Message
        {
            public string? Id { get; set; }
            public string? SessionId { get; set; }
            public string? Role { get; set; }
            public bool OmittedDiffs { get; set; }
        }
    }

    private sealed class ProjectionBuffer(int limit) : IBufferWriter<byte>
    {
        private const int WriterSlack = 4096;
        private byte[] bytes = new byte[Math.Min(16 * 1024, checked(limit + WriterSlack))];
        private int written;

        public ReadOnlyMemory<byte> WrittenMemory => bytes.AsMemory(0, written);

        public void Advance(int count)
        {
            if (count < 0 || written + count > limit)
                throw new InvalidDataException("OpenCode history retained projection size limit exceeded.");
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return bytes.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return bytes.AsSpan(written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            var required = checked(written + Math.Max(sizeHint, 1));
            var maximum = checked(limit + WriterSlack);
            if (required > maximum)
                throw new InvalidDataException("OpenCode history retained projection size limit exceeded.");
            if (required <= bytes.Length) return;
            Array.Resize(ref bytes, Math.Min(Math.Max(required, checked(bytes.Length * 2)), maximum));
        }
    }
}
