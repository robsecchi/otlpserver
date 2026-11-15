using System.Buffers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport;

/// <summary>
/// Decodes binary protobuf request bodies. Shared by OTLP/gRPC (the request marshaller) and
/// OTLP/HTTP with <c>Content-Type: application/x-protobuf</c>: both put exactly the same
/// bytes on the wire, only the framing around them differs.
/// </summary>
public static class OtlpProtobufDecoder
{
    /// <exception cref="OtlpExportException">
    /// <see cref="ExportFailureKind.InvalidData"/> if the bytes aren't a valid message
    /// (gRPC INVALID_ARGUMENT, HTTP 400).
    /// </exception>
    public static ExportLogsServiceRequest ParseLogsRequest(ReadOnlySequence<byte> payload)
    {
        try
        {
            return ExportLogsServiceRequest.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            // Covers truncated data, invalid tags / wire types, and nesting deeper than the
            // parser's recursion limit (100). The library's message is about the encoding
            // only (no internals), so it's safe and useful to pass on.
            throw OtlpExportException.InvalidData(
                $"Request body is not a valid ExportLogsServiceRequest protobuf message: {ex.Message}",
                violations: []);
        }
    }
}
