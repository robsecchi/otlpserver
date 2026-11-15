# Performance baseline (S1)

Measured 2026-11-15, commit `dd065ae` (end of step 6), before any optimization. These are the
numbers later steps (S2, S3) are compared against.

## Environment
- Apple M3 Pro, 11 cores, 18 GB RAM, macOS; .NET 10, Release build, Server GC.
- Server and load generator on the **same machine** over loopback: they share the CPU, so the
  absolute numbers are a lower bound for a dedicated server, and network transfer is nearly free.
- No storage yet: an export ends once it is validated.
- Payload: `tools/OtlpLoadGen/Payloads.cs` (Kubernetes-like: 8 pods per batch, ~12 resource
  attributes, ~200-char access-log body, 6 attributes, trace context). 512 records = 253 KB
  protobuf / 458 KB JSON; 8192 records = 3.99 MB protobuf.

## Micro-benchmarks (per batch, BenchmarkDotNet)
Reproduce: `dotnet run -c Release --project benchmarks/OtlpServer.Benchmarks`

| Method                              | Records | Mean         | Error        | StdDev       | Gen0     | Gen1     | Gen2     | Allocated    |
|------------------------------------ |-------- |-------------:|-------------:|-------------:|---------:|---------:|---------:|-------------:|
| 'decode protobuf'                   | 512     |    234.55 us |     0.551 us |     0.288 us |   9.0332 |   3.6621 |        - |   1014.16 KB |
| 'decode JSON'                       | 512     |  4,631.32 us |    46.238 us |    30.583 us |  78.1250 |  54.6875 |  54.6875 |   6703.21 KB |
| 'core handler (validate + respond)' | 512     |     94.07 us |     0.462 us |     0.275 us |   4.5166 |        - |        - |    499.07 KB |
| 'decode protobuf'                   | 8192    |  3,722.33 us |    79.025 us |    52.270 us |  39.0625 |  15.6250 |        - |  15862.16 KB |
| 'decode JSON'                       | 8192    | 77,464.32 us | 1,960.389 us | 1,025.321 us | 666.6667 | 500.0000 | 333.3333 | 105455.31 KB |
| 'core handler (validate + respond)' | 8192    |  1,440.36 us |     4.031 us |     2.399 us |  70.3125 |        - |        - |    7759.1 KB |


## Load test (closed loop, 15 s after a 5 s warmup)
Reproduce: `tools/loadtest.sh`. One gRPC connection (HTTP/2, multiplexed), like one Collector exporter.
"server cores" = CPU time of the server process / wall time. "wire MB/s" for gRPC+gzip shows the
uncompressed size (the gRPC client compresses).

| scenario | exports/s | records/s | wire MB/s | p50 ms | p95 ms | p99 ms | errors | server cores | server peak RSS MB | loadgen cores |
|---|---|---|---|---|---|---|---|---|---|---|
| grpc/protobuf b512 c1 | 1,673 | 856,717 | 422.9 | 0.6 | 0.7 | 1.6 | - | 2.74 | 243 | 1.09 |
| grpc/protobuf b512 c8 | 4,587 | 2,348,605 | 1,159.3 | 1.6 | 3.6 | 5.0 | - | 7.67 | 425 | 2.21 |
| grpc/protobuf b512 c32 | 4,616 | 2,363,241 | 1,166.6 | 6.6 | 10.7 | 14.4 | - | 7.21 | 1,031 | 2.25 |
| grpc/protobuf b512 c64 | 4,546 | 2,327,402 | 1,148.9 | 13.2 | 20.8 | 30.7 | - | 7.15 | 1,585 | 2.43 |
| grpc/protobuf b512 c128 (over limit) | 4,538 | 2,323,320 | 1,146.8 | 26.5 | 46.0 | 50.1 | - | 7.51 | 1,721 | 2.20 |
| grpc/protobuf b512 c32, quiet logs | 4,658 | 2,384,714 | 1,177.2 | 6.5 | 10.5 | 13.6 | - | 7.12 | 1,014 | 2.33 |
| http/protobuf b512 c32 | 4,134 | 2,116,405 | 1,044.7 | 6.4 | 13.9 | 16.8 | - | 8.58 | 601 | 0.61 |
| http/json b512 c32 | 951 | 486,962 | 435.3 | 30.5 | 59.0 | 91.5 | - | 9.07 | 1,277 | 0.24 |
| http/protobuf+gzip b512 c32 | 5,667 | 2,901,588 | 374.2 | 3.6 | 16.1 | 30.4 | - | 7.70 | 940 | 0.60 |
| grpc/protobuf+gzip b512 c32 | 3,927 | 2,010,601 | 992.5 | 6.6 | 19.0 | 28.7 | - | 6.09 | 1,085 | 3.20 |
| grpc/protobuf b8192 c8 | 269 | 2,201,188 | 1,070.9 | 27.2 | 55.0 | 63.8 | - | 7.76 | 2,204 | 2.11 |
| grpc/protobuf b512 c32, API key | 4,797 | 2,455,974 | 1,212.3 | 6.4 | 9.8 | 13.0 | - | 7.58 | 1,050 | 2.48 |
