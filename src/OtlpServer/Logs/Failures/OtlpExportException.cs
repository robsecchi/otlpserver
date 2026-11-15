namespace OtlpServer.Logs.Failures;

/// <summary>
/// Thrown by the core when the WHOLE export request fails, as opposed to partial success,
/// which is a normal response. Transport adapters catch it and map <see cref="Kind"/> to a
/// status code (see <see cref="ExportFailureKind"/> for the table).
/// </summary>
public sealed class OtlpExportException : Exception
{
    private OtlpExportException(
        ExportFailureKind kind,
        string message,
        TimeSpan? retryAfter = null,
        IReadOnlyList<ExportFieldViolation>? violations = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        Violations = violations ?? [];
    }

    public ExportFailureKind Kind { get; }

    /// <summary>Only <see cref="ExportFailureKind.Throttled"/> tells the client it may retry.</summary>
    public bool IsRetryable => Kind == ExportFailureKind.Throttled;

    /// <summary>For throttling: how long the client should wait before retrying (gRPC RetryInfo / HTTP Retry-After).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>For invalid data: which parts of the request were wrong.</summary>
    public IReadOnlyList<ExportFieldViolation> Violations { get; }

    public static OtlpExportException InvalidData(string message, IReadOnlyList<ExportFieldViolation> violations) =>
        new(ExportFailureKind.InvalidData, message, violations: violations);

    public static OtlpExportException TooLarge(string message) =>
        new(ExportFailureKind.TooLarge, message);

    public static OtlpExportException Throttled(string message, TimeSpan retryAfter) =>
        new(ExportFailureKind.Throttled, message, retryAfter: retryAfter);

    public static OtlpExportException Internal(string message, Exception? inner = null) =>
        new(ExportFailureKind.Internal, message, inner: inner);
}
