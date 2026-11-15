using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpLoadGen;
using OtlpServer.Logs;
using OtlpServer.Logs.Validation;
using OtlpServer.Transport;

BenchmarkRunner.Run<HotPathBenchmarks>(args: args);

/// <summary>
/// The per-export work the server does once the bytes are in memory, one stage at a time:
/// <code>
///  bytes ──► decode (protobuf | JSON) ──► core handler: admission + validation + response
/// </code>
/// Reported per batch: time, and memory ALLOCATED (what drives GC cost at high request rates).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class HotPathBenchmarks
{
    [Params(512, 8192)]
    public int Records { get; set; }

    private byte[] _protobuf = [];
    private byte[] _json = [];
    private ExportLogsServiceRequest _request = new();
    private LogsExportHandler _handler = null!;

    [GlobalSetup]
    public void Setup()
    {
        _request = Payloads.Batch(Records, resources: 8);
        _protobuf = _request.ToByteArray();
        _json = System.Text.Encoding.UTF8.GetBytes(ToOtlpJson(_request));

        _handler = new LogsExportHandler(
            new LogRecordValidator(Options.Create(new LogValidationOptions())),
            Options.Create(new LogsExportOptions()),
            new OtlpServer.Diagnostics.AggregatedWarnings(NullLogger<OtlpServer.Diagnostics.AggregatedWarnings>.Instance),
            NullLogger<LogsExportHandler>.Instance);

        Console.WriteLine($"// {Records} records: protobuf {_protobuf.Length:N0} bytes, JSON {_json.Length:N0} bytes");
    }

    [Benchmark(Description = "decode protobuf")]
    public ExportLogsServiceRequest DecodeProtobuf() =>
        OtlpProtobufDecoder.ParseLogsRequest(new ReadOnlySequence<byte>(_protobuf));

    [Benchmark(Description = "decode JSON")]
    public ExportLogsServiceRequest DecodeJson() => OtlpJsonCodec.ParseLogsRequest(_json);

    [Benchmark(Description = "core handler (validate + respond)")]
    public Task<ExportLogsServiceResponse> Handle() => _handler.ExportAsync(_request, ExportContext.Anonymous, CancellationToken.None);

    private static string ToOtlpJson(ExportLogsServiceRequest request)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonFormatter.Default.Format(request))!;
        foreach (var rl in root["resourceLogs"]!.AsArray())
        foreach (var sl in rl!["scopeLogs"]!.AsArray())
        foreach (var lr in sl!["logRecords"]!.AsArray())
        foreach (var key in (string[])["traceId", "spanId"])
        {
            if (lr![key] is System.Text.Json.Nodes.JsonValue v)
                lr[key] = Convert.ToHexString(Convert.FromBase64String(v.GetValue<string>()));
        }
        return root.ToJsonString();
    }
}
