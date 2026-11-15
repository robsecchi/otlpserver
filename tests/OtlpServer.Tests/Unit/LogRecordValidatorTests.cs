using Google.Protobuf;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Unit;

/// <summary>One test per rule in LogRecordValidator, each tied to the Log Data Model.</summary>
public sealed class LogRecordValidatorTests
{
    private readonly LogRecordValidator _validator = new(Options.Create(new LogValidationOptions()));

    private static ByteString Bytes(int length, byte fill = 0xAB) =>
        ByteString.CopyFrom(Enumerable.Repeat(fill, length).ToArray());

    private LogRecordIssue Single(LogRecord record) => Assert.Single(_validator.Validate(record));

    [Fact]
    public void Valid_record_has_no_issues() =>
        Assert.Empty(_validator.Validate(Requests.ValidRecord()));

    [Fact]
    public void Valid_trace_and_span_ids_have_no_issues()
    {
        var record = Requests.ValidRecord();
        record.TraceId = Bytes(16);
        record.SpanId = Bytes(8);
        record.Flags = 0x01; // sampled: low 8 bits are W3C trace flags

        Assert.Empty(_validator.Validate(record));
    }

    [Fact]
    public void Severity_number_outside_the_enum_is_rejected()
    {
        var record = Requests.ValidRecord();
        record.SeverityNumber = (SeverityNumber)99;

        var issue = Single(record);
        Assert.Equal(IssueSeverity.Rejection, issue.Severity);
        Assert.Equal("severity_number_out_of_range", issue.Rule);
    }

    [Fact]
    public void Duplicate_attribute_key_is_rejected() =>
        Assert.Equal("duplicate_attribute_key", Single(Requests.RecordWithDuplicateKey()).Rule);

    [Theory]
    [InlineData(3)]   // wrong length
    [InlineData(32)]  // wrong length
    public void Trace_id_with_wrong_length_is_a_warning(int length)
    {
        var record = Requests.ValidRecord();
        record.TraceId = Bytes(length);

        var issue = Single(record);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("invalid_trace_id", issue.Rule);
    }

    [Fact]
    public void All_zero_trace_id_is_a_warning()
    {
        var record = Requests.ValidRecord();
        record.TraceId = Bytes(16, fill: 0);

        Assert.Equal("invalid_trace_id", Single(record).Rule);
    }

    [Fact]
    public void Invalid_span_id_is_a_warning()
    {
        var record = Requests.ValidRecord();
        record.TraceId = Bytes(16);
        record.SpanId = Bytes(4);

        var issue = Single(record);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("invalid_span_id", issue.Rule);
    }

    [Fact]
    public void Valid_span_id_without_trace_id_is_a_warning()
    {
        var record = Requests.ValidRecord();
        record.SpanId = Bytes(8);

        Assert.Equal("span_id_without_trace_id", Single(record).Rule);
    }

    [Fact]
    public void Reserved_flag_bits_are_a_warning()
    {
        var record = Requests.ValidRecord();
        record.Flags = 0x0000_0100;

        var issue = Single(record);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("reserved_flags_set", issue.Rule);
    }
}
