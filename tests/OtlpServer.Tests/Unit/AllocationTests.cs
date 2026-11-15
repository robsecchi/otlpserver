using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OtlpServer.Logs;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Unit;

/// <summary>
/// Guards S2.3: validating CLEAN data must not allocate. It runs for every record, millions of
/// times per second at load, and before S2.3 it allocated ~1 KB per record (about a third of
/// everything a gRPC export allocated).
/// <para>
/// Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/>: exact and deterministic,
/// since everything runs synchronously on this thread.
/// </para>
/// </summary>
public sealed class AllocationTests
{
    private static readonly LogRecordValidator Validator = new(Options.Create(new LogValidationOptions()));

    /// <summary>A realistic clean record: trace context, several attributes, a nested map, a map body.</summary>
    private static LogRecord CleanRecord()
    {
        var record = ValidRecord();
        record.TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)0xAB, 16).ToArray());
        record.SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)0xCD, 8).ToArray());
        record.Flags = 1;
        record.Attributes.Add(Attribute("http.request.method", "GET"));
        record.Attributes.Add(Attribute("http.route", "/api/orders/{id}"));
        record.Attributes.Add(new KeyValue { Key = "http.response.status_code", Value = new AnyValue { IntValue = 200 } });
        record.Attributes.Add(new KeyValue { Key = "user", Value = Map(Attribute("id", "u-1"), Attribute("tier", "gold")) });
        record.Body = Map(Attribute("message", "hello"), Attribute("level", "info"));
        return record;
    }

    private static long AllocatedBy(Action action)
    {
        action(); // warm up: JIT, static initializers
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Validating_clean_records_allocates_nothing()
    {
        var record = CleanRecord();

        var bytes = AllocatedBy(() =>
        {
            for (var i = 0; i < 1000; i++) Validator.Validate(record);
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Validating_a_clean_resource_and_scope_allocates_nothing()
    {
        var resource = DefaultResource();
        resource.Attributes.Add(Attribute("k8s.pod.name", "orders-api-1"));
        var scope = DefaultScope();

        var bytes = AllocatedBy(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                Validator.ValidateResource(resource);
                Validator.ValidateScope(scope);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public async Task Handling_a_clean_batch_allocates_a_fixed_amount_not_per_record()
    {
        // Only the per-export objects (outcome, response, task) are left: a few hundred bytes,
        // the same for 10 or 1,000 records. Before S2.3: ~1 KB PER record.
        var handler = new LogsExportHandler(Validator, Options.Create(new LogsExportOptions()),
            new OtlpServer.Diagnostics.AggregatedWarnings(NullLogger<OtlpServer.Diagnostics.AggregatedWarnings>.Instance),
            NullLogger<LogsExportHandler>.Instance);
        var small = WithRecords(Enumerable.Range(0, 10).Select(_ => CleanRecord()).ToArray());
        var large = WithRecords(Enumerable.Range(0, 1000).Select(_ => CleanRecord()).ToArray());
        await handler.ExportAsync(small, ExportContext.Anonymous, CancellationToken.None); // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        await handler.ExportAsync(small, ExportContext.Anonymous, CancellationToken.None);
        var forSmall = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        await handler.ExportAsync(large, ExportContext.Anonymous, CancellationToken.None);
        var forLarge = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(forSmall, forLarge);
        Assert.True(forLarge < 2048, $"{forLarge} bytes for 1,000 clean records");
    }
}
