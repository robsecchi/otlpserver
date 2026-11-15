using Google.Protobuf;
using OpenTelemetry.Proto.Logs.V1;

namespace OtlpServer.Logs.Validation;

/// <summary>
/// Decides, record by record, what this server accepts.
/// <para>
/// The OTLP spec does NOT say which data a server must reject; it only says how to
/// report a rejection (via <c>partial_success</c>). So this class is server policy.
/// The rules below come from the "MUST" / "SHOULD" wording in
/// <c>opentelemetry/proto/logs/v1/logs.proto</c>:
/// </para>
/// <list type="bullet">
///   <item>A violated MUST that makes the record unreliable becomes a <see cref="IssueSeverity.Rejection"/>.</item>
///   <item>Where the proto says how receivers should cope (e.g. "SHOULD assume not associated
///         with a trace"), the record is kept and we emit a <see cref="IssueSeverity.Warning"/>.</item>
/// </list>
/// </summary>
public sealed class LogRecordValidator: ILogRecordValidator
{
    private const int TraceIdLength = 16;
    private const int SpanIdLength = 8;
    private const uint ReservedFlagsMask = ~(uint)LogRecordFlags.TraceFlagsMask; // upper 24 bits

    public IReadOnlyList<LogRecordIssue> Validate(LogRecord record)
    {
        var issues = new List<LogRecordIssue>();

        // ---- Rejections ------------------------------------------------------------

        // severity_number is an enum: 0 (UNSPECIFIED) .. 24 (FATAL4). Protobuf keeps
        // unknown numeric values, so a sender can put e.g. 99 on the wire. That value
        // means nothing in the Log Data Model, so the record can't be classified.
        if (!Enum.IsDefined(record.SeverityNumber))
        {
            issues.Add(new(IssueSeverity.Rejection, "severity_number_out_of_range",
                $"severity_number={(int)record.SeverityNumber} is outside the valid range 0..24"));
        }

        // "Attribute keys MUST be unique (it is not allowed to have more than one
        //  attribute with the same key)."
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in record.Attributes)
        {
            if (!seen.Add(attribute.Key))
            {
                issues.Add(new(IssueSeverity.Rejection, "duplicate_attribute_key",
                    $"attribute key '{attribute.Key}' appears more than once"));
                break; // one occurrence is enough to reject the record
            }
        }

        // ---- Warnings (record still accepted) ---------------------------------------

        // trace_id: 16 bytes, not all zeroes. Anything else is invalid and receivers
        // "SHOULD assume that the log record is not associated with a trace". An empty
        // field just means "no trace", which is fine.
        var traceIdValid = IsValidId(record.TraceId, TraceIdLength);
        if (!record.TraceId.IsEmpty && !traceIdValid)
        {
            issues.Add(new(IssueSeverity.Warning, "invalid_trace_id",
                $"trace_id has {record.TraceId.Length} byte(s) or is all zeroes (expected {TraceIdLength} non-zero bytes); record treated as not associated with a trace"));
        }

        // span_id: same idea, 8 bytes.
        var spanIdValid = IsValidId(record.SpanId, SpanIdLength);
        if (!record.SpanId.IsEmpty && !spanIdValid)
        {
            issues.Add(new(IssueSeverity.Warning, "invalid_span_id",
                $"span_id has {record.SpanId.Length} byte(s) or is all zeroes (expected {SpanIdLength} non-zero bytes); record treated as not associated with a span"));
        }

        // "If the sender specifies a valid span_id then it SHOULD also specify a valid trace_id."
        if (spanIdValid && !traceIdValid)
        {
            issues.Add(new(IssueSeverity.Warning, "span_id_without_trace_id",
                "span_id is set but trace_id is missing or invalid"));
        }

        // flags: the low 8 bits are W3C trace flags; "24 most significant bits are
        // reserved and must be set to 0". Readers mask them out, so this is harmless.
        if ((record.Flags & ReservedFlagsMask) != 0)
        {
            issues.Add(new(IssueSeverity.Warning, "reserved_flags_set",
                $"flags=0x{record.Flags:X8} has reserved bits set (only the lowest 8 bits may be used)"));
        }

        return issues;
    }

    private static bool IsValidId(ByteString id, int expectedLength)
    {
        if (id.Length != expectedLength) return false;
        foreach (var b in id.Span)
        {
            if (b != 0) return true;
        }
        return false; // all zeroes
    }
}
