using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace OtlpLoadGen;

/// <summary>
/// Realistic OTLP log batches, shaped like what a Kubernetes node agent (OTel Collector) sends:
/// <code>
///  ExportLogsServiceRequest
///    └─ ResourceLogs × resources     one per pod: k8s.* + service.* + host/cloud attributes (~12)
///        └─ ScopeLogs × 1
///            └─ LogRecord × (records / resources)
///                 time + observed time, severity, ~200-char access-log body,
///                 6 attributes (string / int / double), trace_id + span_id
/// </code>
/// Deterministic for a given seed, so benchmark runs are comparable.
/// Shared (as a linked file) by the load generator and the benchmarks.
/// </summary>
public static class Payloads
{
    private static readonly string[] Methods = ["GET", "GET", "GET", "POST", "PUT", "DELETE"];
    private static readonly string[] Routes = ["/api/orders/{id}", "/api/customers/{id}", "/api/cart", "/healthz", "/api/search"];
    private static readonly int[] Statuses = [200, 200, 200, 200, 201, 204, 400, 404, 500];

    public static ExportLogsServiceRequest Batch(int records, int resources = 8, int seed = 42)
    {
        var random = new Random(seed);
        var request = new ExportLogsServiceRequest();
        var baseTime = 1_763_200_000_000_000_000UL; // fixed, for determinism
        var perResource = Math.Max(1, records / Math.Max(1, resources));
        var remaining = records;

        for (var r = 0; r < resources && remaining > 0; r++)
        {
            var pod = $"orders-api-7d9f8c6b5-{(char)('a' + r % 26)}{random.Next(1000, 9999)}";
            var resource = new Resource
            {
                Attributes =
                {
                    Str("service.name", "orders-api"),
                    Str("service.version", "1.42.3"),
                    Str("service.namespace", "shop"),
                    Str("k8s.cluster.name", "prod-eu-west-1"),
                    Str("k8s.namespace.name", "shop"),
                    Str("k8s.deployment.name", "orders-api"),
                    Str("k8s.pod.name", pod),
                    Str("k8s.pod.uid", RandomGuid(random).ToString()),
                    Str("k8s.container.name", "orders-api"),
                    Str("k8s.node.name", $"ip-10-0-{random.Next(0, 255)}-{random.Next(0, 255)}.eu-west-1.compute.internal"),
                    Str("cloud.provider", "aws"),
                    Str("cloud.region", "eu-west-1"),
                },
            };

            var scope = new ScopeLogs { Scope = new InstrumentationScope { Name = "Microsoft.AspNetCore.Hosting", Version = "10.0.0" } };
            var count = r == resources - 1 ? remaining : Math.Min(perResource, remaining);
            for (var i = 0; i < count; i++)
            {
                scope.LogRecords.Add(Record(random, baseTime + (ulong)(i * 1_000_000)));
            }
            remaining -= count;

            request.ResourceLogs.Add(new ResourceLogs { Resource = resource, ScopeLogs = { scope } });
        }

        return request;
    }

    private static LogRecord Record(Random random, ulong time)
    {
        var method = Methods[random.Next(Methods.Length)];
        var route = Routes[random.Next(Routes.Length)];
        var status = Statuses[random.Next(Statuses.Length)];
        var duration = Math.Round(random.NextDouble() * 250, 3);
        var path = route.Replace("{id}", random.Next(1, 999_999).ToString());
        var requestId = RandomGuid(random).ToString("N");

        var traceId = new byte[16];
        var spanId = new byte[8];
        random.NextBytes(traceId);
        random.NextBytes(spanId);

        var severity = status >= 500 ? SeverityNumber.Error : status >= 400 ? SeverityNumber.Warn : SeverityNumber.Info;
        return new LogRecord
        {
            TimeUnixNano = time,
            ObservedTimeUnixNano = time + 2_500_000,
            SeverityNumber = severity,
            SeverityText = severity.ToString().ToUpperInvariant(),
            Body = new AnyValue
            {
                StringValue = $"Request finished HTTP/1.1 {method} http://orders-api.shop.svc.cluster.local{path} - {status} " +
                              $"{random.Next(80, 20_000)} application/json;+charset=utf-8 {duration}ms request_id={requestId} " +
                              $"user_agent=\"okhttp/4.12.0\"",
            },
            Attributes =
            {
                Str("http.request.method", method),
                Str("http.route", route),
                new KeyValue { Key = "http.response.status_code", Value = new AnyValue { IntValue = status } },
                new KeyValue { Key = "http.server.request.duration", Value = new AnyValue { DoubleValue = duration } },
                Str("user.id", $"u-{random.Next(1, 50_000)}"),
                Str("request.id", requestId),
            },
            TraceId = ByteString.CopyFrom(traceId),
            SpanId = ByteString.CopyFrom(spanId),
            Flags = 1,
        };
    }

    private static Guid RandomGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static KeyValue Str(string key, string value) => new() { Key = key, Value = new AnyValue { StringValue = value } };
}
