using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpLoadGen;

// ---------------------------------------------------------------------------------------------
// OTLP load generator.
//
//   dotnet run -c Release --project tools/OtlpLoadGen -- \
//     --transport grpc|http  --encoding protobuf|json  [--gzip]  --batch 512  --resources 8 \
//     --concurrency 32  --connections 1  --duration 20  --warmup 5 \
//     [--target URL]  [--server-pid PID]  [--api-key KEY]  [--label NAME]  [--row]
//
// Each worker sends one export, waits for the answer, sends the next (closed loop), like an
// exporter with N parallel senders. Bodies are serialized ONCE up front, so the numbers are
// about the server, not the client. (gzip over gRPC is the exception: grpc-dotnet always
// compresses on the client.)
// ---------------------------------------------------------------------------------------------

var o = LoadOptions.Parse(args);
var request = Payloads.Batch(o.Batch, o.Resources);
var protobuf = request.ToByteArray();
var body = o.Encoding == "json" ? Encoding.UTF8.GetBytes(ToOtlpJson(request)) : protobuf;
if (o.Gzip && o.Transport == "http") body = Gzip(body);

Func<Task<string>> send = o.Transport == "grpc" ? GrpcSender(o, protobuf) : HttpSender(o, body);

// Closed-loop workers. Only the measurement window (after the warmup) is recorded.
var stats = Enumerable.Range(0, o.Concurrency).Select(_ => new WorkerStats()).ToArray();
var measuring = false;
var stop = false;
var workers = stats.Select(s => Task.Run(async () =>
{
    while (!Volatile.Read(ref stop))
    {
        var start = Stopwatch.GetTimestamp();
        string outcome;
        try { outcome = await send(); }
        catch (Exception ex) { outcome = ex.GetType().Name; }
        if (Volatile.Read(ref measuring)) s.Record(outcome, Stopwatch.GetElapsedTime(start));
    }
})).ToArray();

var server = o.ServerPid is { } pid ? Process.GetProcessById(pid) : null;
var self = Process.GetCurrentProcess();

await Task.Delay(TimeSpan.FromSeconds(o.Warmup));
server?.Refresh();
self.Refresh();
var serverCpu0 = server?.TotalProcessorTime ?? TimeSpan.Zero;
var selfCpu0 = self.TotalProcessorTime;
var wall = Stopwatch.StartNew();
Volatile.Write(ref measuring, true);

long peakRss = 0;
while (wall.Elapsed < TimeSpan.FromSeconds(o.Duration))
{
    await Task.Delay(200);
    if (server is null) continue;
    server.Refresh();
    peakRss = Math.Max(peakRss, server.WorkingSet64);
}

Volatile.Write(ref measuring, false);
var seconds = wall.Elapsed.TotalSeconds;
server?.Refresh();
self.Refresh();
var serverCores = server is null ? double.NaN : (server.TotalProcessorTime - serverCpu0).TotalSeconds / seconds;
var selfCores = (self.TotalProcessorTime - selfCpu0).TotalSeconds / seconds;
Volatile.Write(ref stop, true);
await Task.WhenAll(workers);

// ---- Summary ------------------------------------------------------------------------------
var latencies = stats.SelectMany(s => s.Latencies).Order().ToArray();
var outcomes = stats.SelectMany(s => s.Outcomes).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
var ok = outcomes.GetValueOrDefault("OK");
var total = outcomes.Values.Sum();
double P(double q) => latencies.Length == 0 ? double.NaN : latencies[Math.Min(latencies.Length - 1, (int)(q * latencies.Length))].TotalMilliseconds;
var errors = string.Join(" ", outcomes.Where(kv => kv.Key != "OK").OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));
var label = o.Label ?? $"{o.Transport}/{o.Encoding}{(o.Gzip ? "+gzip" : "")} b{o.Batch} c{o.Concurrency}";
var ci = CultureInfo.InvariantCulture;

if (o.Row)
{
    Console.WriteLine(string.Create(ci,
        $"| {label} | {ok / seconds:N0} | {ok * (double)o.Batch / seconds:N0} | {ok * (double)body.Length / seconds / 1e6:N1} | " +
        $"{P(0.50):N1} | {P(0.95):N1} | {P(0.99):N1} | {(errors == "" ? "-" : errors)} | {serverCores:N2} | {peakRss / 1e6:N0} | {selfCores:N2} |"));
}
else
{
    Console.WriteLine(string.Create(ci, $"""
        {label}
          batch:        {o.Batch} records in {o.Resources} resources, {protobuf.Length:N0} bytes protobuf, {body.Length:N0} bytes on the wire
          requests:     {total:N0} in {seconds:N1} s ({ok:N0} OK, {errors})
          throughput:   {ok / seconds:N0} exports/s, {ok * (double)o.Batch / seconds:N0} records/s, {ok * (double)body.Length / seconds / 1e6:N1} MB/s
          latency ms:   p50 {P(0.50):N1}  p95 {P(0.95):N1}  p99 {P(0.99):N1}  max {(latencies.Length > 0 ? latencies[^1].TotalMilliseconds : double.NaN):N1}
          server:       {serverCores:N2} cores, peak RSS {peakRss / 1e6:N0} MB
          load gen:     {selfCores:N2} cores
        """));
}

// ---- Senders --------------------------------------------------------------------------------

static Func<Task<string>> GrpcSender(LoadOptions o, byte[] protobuf)
{
    var bytes = Marshallers.Create<byte[]>(b => b, b => b);
    var method = new Method<byte[], byte[]>(MethodType.Unary, LogsService.Descriptor.FullName, "Export", bytes, bytes);
    // A Collector exporter normally holds ONE HTTP/2 connection and multiplexes on it.
    var invokers = Enumerable.Range(0, o.Connections)
        .Select(_ => GrpcChannel.ForAddress(o.Target ?? "http://localhost:4317", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = false },
            MaxSendMessageSize = null,
        }).CreateCallInvoker())
        .ToArray();

    var headers = new Metadata();
    if (o.ApiKey is not null) headers.Add("authorization", $"Bearer {o.ApiKey}");
    if (o.Gzip) headers.Add("grpc-internal-encoding-request", "gzip");
    var next = 0;

    return async () =>
    {
        var invoker = invokers[(int)((uint)Interlocked.Increment(ref next) % invokers.Length)];
        try
        {
            await invoker.AsyncUnaryCall(method, null, new CallOptions(headers), protobuf).ResponseAsync;
            return "OK";
        }
        catch (RpcException ex)
        {
            return ex.StatusCode.ToString();
        }
    };
}

static Func<Task<string>> HttpSender(LoadOptions o, byte[] body)
{
    var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = o.Concurrency })
    {
        BaseAddress = new Uri(o.Target ?? "http://localhost:4318"),
    };
    var contentType = o.Encoding == "json" ? "application/json" : "application/x-protobuf";

    return async () =>
    {
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (o.Gzip) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/logs") { Content = content };
        if (o.ApiKey is not null) message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {o.ApiKey}");

        using var response = await http.SendAsync(message);
        await response.Content.LoadIntoBufferAsync();
        return response.IsSuccessStatusCode ? "OK" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
    };
}

// ---- Encoding helpers -------------------------------------------------------------------------

static string ToOtlpJson(ExportLogsServiceRequest request)
{
    // Standard proto3 JSON, then traceId/spanId base64 → hex as OTLP/JSON requires.
    var root = JsonNode.Parse(JsonFormatter.Default.Format(request))!;
    foreach (var rl in root["resourceLogs"]!.AsArray())
    foreach (var sl in rl!["scopeLogs"]!.AsArray())
    foreach (var lr in sl!["logRecords"]!.AsArray())
    {
        foreach (var key in (string[])["traceId", "spanId"])
        {
            if (lr![key] is JsonValue v) lr[key] = Convert.ToHexString(Convert.FromBase64String(v.GetValue<string>()));
        }
    }
    return root.ToJsonString();
}

static byte[] Gzip(byte[] data)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(data);
    return output.ToArray();
}

// ---- Types --------------------------------------------------------------------------------------

sealed class WorkerStats
{
    public List<TimeSpan> Latencies { get; } = new(100_000);
    public Dictionary<string, int> Outcomes { get; } = [];

    public void Record(string outcome, TimeSpan latency)
    {
        Latencies.Add(latency);
        Outcomes[outcome] = Outcomes.GetValueOrDefault(outcome) + 1;
    }
}

sealed record LoadOptions(
    string Transport, string Encoding, bool Gzip, int Batch, int Resources, int Concurrency, int Connections,
    int Duration, int Warmup, string? Target, int? ServerPid, string? ApiKey, string? Label, bool Row)
{
    public static LoadOptions Parse(string[] args)
    {
        string? Get(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        int GetInt(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        var options = new LoadOptions(
            Transport: Get("--transport") ?? "grpc",
            Encoding: Get("--encoding") ?? "protobuf",
            Gzip: args.Contains("--gzip"),
            Batch: GetInt("--batch", 512),
            Resources: GetInt("--resources", 8),
            Concurrency: GetInt("--concurrency", 8),
            Connections: GetInt("--connections", 1),
            Duration: GetInt("--duration", 20),
            Warmup: GetInt("--warmup", 5),
            Target: Get("--target"),
            ServerPid: Get("--server-pid") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : null,
            ApiKey: Get("--api-key"),
            Label: Get("--label"),
            Row: args.Contains("--row"));

        if (options.Transport == "grpc" && options.Encoding == "json")
        {
            throw new ArgumentException("OTLP/gRPC is protobuf only; use --transport http for JSON.");
        }
        return options;
    }
}
