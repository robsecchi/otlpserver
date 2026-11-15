namespace OtlpServer.Logs.Failures;

/// <summary>
/// The kinds of export failure the core can report, without referring to any transport.
///
/// <para>
/// OTLP sorts failures into two groups:
/// </para>
/// <list type="bullet">
///   <item><b>Retryable</b>: processing failed but may work later. The client MAY resend the
///         SAME data (with exponential backoff, or after the delay the server gives).</item>
///   <item><b>Non-retryable</b>: resending would fail the same way. The client MUST NOT retry
///         and MUST drop the data (it SHOULD count what it dropped).</item>
/// </list>
/// <para>Each transport adapter maps a kind to its own wire signal:</para>
/// <code>
///  Kind         Retryable  OTLP/gRPC status                   OTLP/HTTP
///  -----------  ---------  ---------------------------------  ---------------------------------
///  InvalidData  no         INVALID_ARGUMENT + BadRequest      400 Bad Request
///  TooLarge     no         RESOURCE_EXHAUSTED (no RetryInfo)  413 Content Too Large
///  Throttled    yes        UNAVAILABLE      + RetryInfo       503 Service Unavailable + Retry-After
///  QuotaExceeded yes       RESOURCE_EXHAUSTED + RetryInfo     429 Too Many Requests + Retry-After
///  Internal     no         INTERNAL                           500 Internal Server Error
/// </code>
/// </summary>
public enum ExportFailureKind
{
    /// <summary>The request's data can't be accepted as sent (here: every record was rejected).</summary>
    InvalidData,

    /// <summary>
    /// The payload is over a size limit (on the wire, or once decompressed). Sending the same
    /// payload again would fail the same way, so the client must drop it (and should send
    /// smaller batches).
    /// </summary>
    TooLarge,

    /// <summary>
    /// Backpressure: the server can't keep up right now. The spec says the server SHOULD
    /// signal this so the client throttles itself (OTLP/gRPC: UNAVAILABLE, optionally with
    /// RetryInfo).
    /// </summary>
    Throttled,

    /// <summary>
    /// Per-client backpressure: THIS client is over its own share, while the server may have
    /// room for others. Retryable after the given delay. A different signal from Throttled
    /// ("the server is busy"), so clients and dashboards can tell the two apart.
    /// </summary>
    QuotaExceeded,

    /// <summary>
    /// Unexpected server-side bug. We treat it as non-retryable: if the same input always
    /// hits the bug, retrying only puts the client into a retry loop.
    /// </summary>
    Internal,
}
