using System.Diagnostics;
using System.Buffers;
using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Groundhog.Cli.Ide;

internal static class IdeServer
{
    internal static async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellation,
        IdeServices? services = null)
    {
        var formatter = new ValidatingFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        };
        using var handler = new RecoveringMessageHandler(output, new IdeInputStream(input), formatter);
        using var rpc = new SafeJsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true
        };
        rpc.TraceSource.Switch.Level = SourceLevels.Off;
        await using var backend = services ?? new IdeServices();
        backend.Notify = (method, value) => rpc.NotifyWithParameterObjectAsync(method, value);
        foreach (var method in IdeServices.Methods)
        {
            var endpoint = new Endpoint(backend, method);
            rpc.AddLocalRpcMethod(typeof(Endpoint).GetMethod(nameof(Endpoint.InvokeAsync))!, endpoint,
                new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });
        }
        rpc.StartListening();
        using var registration = cancellation.Register(rpc.Dispose);
        try { await rpc.Completion; }
        catch (Exception ex) when (ex is ConnectionLostException or OperationCanceledException or IOException
            or BadRpcHeaderException or JsonException) { }
        return 0;
    }

    private sealed class Endpoint(IdeServices services, string method)
    {
        public Task<object> InvokeAsync(JsonElement parameters, CancellationToken cancellation) =>
            services.InvokeAsync(method, parameters, cancellation);
    }

    private sealed class RecoveringMessageHandler(Stream output, Stream input, IJsonRpcMessageFormatter formatter)
        : HeaderDelimitedMessageHandler(output, input, formatter)
    {
        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                try { return await base.ReadCoreAsync(cancellationToken); }
                catch (JsonException)
                {
                    await WriteAsync(new JsonRpcError
                    {
                        Error = new() { Code = JsonRpcErrorCode.ParseError, Message = "Invalid JSON." }
                    }, cancellationToken);
                }
                catch (InvalidRequestException)
                {
                    await WriteAsync(new JsonRpcError
                    {
                        Error = new() { Code = JsonRpcErrorCode.InvalidRequest, Message = "Invalid JSON-RPC request." }
                    }, cancellationToken);
                }
            }
        }
    }

    private sealed class InvalidRequestException : Exception;
    private sealed class ValidatingFormatter : IJsonRpcMessageTextFormatter, IJsonRpcInstanceContainer
    {
        private readonly SystemTextJsonFormatter _inner = new();
        internal JsonSerializerOptions JsonSerializerOptions { get => _inner.JsonSerializerOptions; set => _inner.JsonSerializerOptions = value; }
        public System.Text.Encoding Encoding { get => _inner.Encoding; set => _inner.Encoding = value; }
        JsonRpc IJsonRpcInstanceContainer.Rpc { set => ((IJsonRpcInstanceContainer)_inner).Rpc = value; }
        public object GetJsonText(JsonRpcMessage message) => _inner.GetJsonText(message);
        JsonRpcMessage IJsonRpcMessageFormatter.Deserialize(ReadOnlySequence<byte> contentBuffer) => Validate(contentBuffer);
        JsonRpcMessage IJsonRpcMessageTextFormatter.Deserialize(ReadOnlySequence<byte> contentBuffer, System.Text.Encoding encoding) => Validate(contentBuffer);
        void IJsonRpcMessageFormatter.Serialize(IBufferWriter<byte> writer, JsonRpcMessage message)
        {
            if (message is JsonRpcError error && error.RequestId.IsEmpty && error.Error is { } detail)
            {
                using var json = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(json, new { jsonrpc = "2.0", id = (object?)null,
                    error = new { code = (int)detail.Code, message = detail.Message } });
            }
            else { _inner.Serialize(writer, message); }
        }
        private JsonRpcMessage Validate(ReadOnlySequence<byte> contentBuffer)
        {
            using var document = JsonDocument.Parse(contentBuffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
                || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String
                || (root.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                || (id.ValueKind == JsonValueKind.Number && !id.TryGetInt64(out _)))
            { throw new InvalidRequestException(); }
            return _inner.Deserialize(contentBuffer);
        }
    }

    // StreamJsonRpc's default error payload includes exception type/stack details. Never expose those to an IDE.
    private sealed class SafeJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
        {
            if (exception is LocalRpcException safe)
            {
                return new() { Code = (JsonRpcErrorCode)safe.ErrorCode, Message = safe.Message, Data = safe.ErrorData };
            }
            if (exception is OperationCanceledException)
            {
                return new() { Code = (JsonRpcErrorCode)(-32800), Message = "Request cancelled.", Data = new { kind = "cancelled" } };
            }
            return new() { Code = JsonRpcErrorCode.InternalError, Message = "The backend could not complete the request." };
        }
    }
}
