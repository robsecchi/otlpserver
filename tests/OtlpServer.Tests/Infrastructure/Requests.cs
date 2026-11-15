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
    public static ExportLogsServiceRequest WithRecords(params LogRecord[] records)
    {
        var scopeLogs = new ScopeLogs { Scope = new InstrumentationScope { Name = "tests" } };
        scopeLogs.LogRecords.AddRange(records);

        var resourceLogs = new ResourceLogs
        {
            Resource = new Resource { Attributes = { Attribute("service.name", "otlp-server-tests") } },
            ScopeLogs = { scopeLogs },
        };

        return new ExportLogsServiceRequest { ResourceLogs = { resourceLogs } };
    }

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
