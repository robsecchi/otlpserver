using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpServer.Logs.Validation;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// Blocks every Validate call until <see cref="Release"/> is set, so a test can hold an
/// export inside the server (i.e. keep a concurrency slot taken) for as long as it needs.
/// </summary>
public sealed class GatedValidator : ILogRecordValidator
{
    private readonly LogRecordValidator _inner = new(Options.Create(new LogValidationOptions()));

    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public IReadOnlyList<LogRecordIssue> Validate(LogRecord record)
    {
        Entered.Set();
        Release.Wait(TimeSpan.FromSeconds(10));
        return _inner.Validate(record);
    }

    public IReadOnlyList<LogRecordIssue> ValidateResource(Resource? resource) => _inner.ValidateResource(resource);
    public IReadOnlyList<LogRecordIssue> ValidateScope(InstrumentationScope? scope) => _inner.ValidateScope(scope);
}

/// <summary>Simulates a server bug: every record makes validation throw.</summary>
public sealed class ThrowingValidator(string message) : ILogRecordValidator
{
    public IReadOnlyList<LogRecordIssue> Validate(LogRecord record) => throw new InvalidOperationException(message);
    public IReadOnlyList<LogRecordIssue> ValidateResource(Resource? resource) => [];
    public IReadOnlyList<LogRecordIssue> ValidateScope(InstrumentationScope? scope) => [];
}
