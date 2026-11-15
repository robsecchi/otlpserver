using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>Builders for OTLP requests and log records used across the tests.</summary>
public static class Requests
{
    /// <summary>
    /// One resource, one scope, holding the given records, which is the shape most SDKs send.
    /// </summary>
    public static ExportLogsServiceRequest WithRecords(params LogRecord[] records) =>
        WithResources(ResourceLogsOf(DefaultResource(), ScopeLogsOf(DefaultScope(), records)));

    public static ExportLogsServiceRequest WithResources(params ResourceLogs[] resourceLogs) =>
        new() { ResourceLogs = { resourceLogs } };

    public static ResourceLogs ResourceLogsOf(Resource resource, params ScopeLogs[] scopeLogs) =>
        new() { Resource = resource, ScopeLogs = { scopeLogs } };

    public static ScopeLogs ScopeLogsOf(InstrumentationScope scope, params LogRecord[] records) =>
        new() { Scope = scope, LogRecords = { records } };

    public static Resource DefaultResource() =>
        new() { Attributes = { Attribute("service.name", "otlp-server-tests") } };

    public static InstrumentationScope DefaultScope() => new() { Name = "tests" };

    /// <summary>Two different service.name values: which service sent these logs?</summary>
    public static Resource ResourceWithDuplicateKey() =>
        new() { Attributes = { Attribute("service.name", "a"), Attribute("service.name", "b") } };

    public static InstrumentationScope ScopeWithDuplicateKey() =>
        new() { Name = "tests", Attributes = { Attribute("x", "1"), Attribute("x", "2") } };

    /// <summary>Wraps key-values in an AnyValue map (kvlist_value).</summary>
    public static AnyValue Map(params KeyValue[] keyValues) =>
        new() { KvlistValue = new KeyValueList { Values = { keyValues } } };

    /// <summary>A record with no problems at all.</summary>
    public static LogRecord ValidRecord(string body = "hello") => new()
    {
        TimeUnixNano = 1_700_000_000_000_000_000UL,
        SeverityNumber = SeverityNumber.Info,
        Body = new AnyValue { StringValue = body },
        Attributes = { Attribute("k", "v") },
    };

    /// <summary>Rejected by the server: attribute keys MUST be unique.</summary>
    public static LogRecord RecordWithDuplicateKey()
    {
        var record = ValidRecord("duplicate key");
        record.Attributes.Add(Attribute("k", "again"));
        return record;
    }

    /// <summary>Accepted with a warning: trace_id must be 16 bytes.</summary>
    public static LogRecord RecordWithShortTraceId()
    {
        var record = ValidRecord("short trace id");
        record.TraceId = ByteString.CopyFrom(1, 2, 3);
        return record;
    }

    public static KeyValue Attribute(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    /// <summary>Asks the gRPC client to gzip this call's request message.</summary>
    public static CallOptions Compressed(string encoding = "gzip") =>
        new(headers: new Metadata { { "grpc-internal-encoding-request", encoding } });
}
