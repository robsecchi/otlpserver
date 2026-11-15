using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpServer.Logs.Validation;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>
/// Blocks Validate calls until <see cref="Release"/> is set, so a test can hold exports inside
/// the server (i.e. keep admission slots taken) for as long as it needs.
/// </summary>
/// <param name="blockFirst">How many exports to hold (one record each); later ones pass straight through. Default: all.</param>
public sealed class GatedValidator(int blockFirst = int.MaxValue) : ILogRecordValidator
{
    private readonly LogRecordValidator _inner = new(Options.Create(new LogValidationOptions()));
    private int _calls;

    /// <summary>Set when the first held export has reached validation.</summary>
    public ManualResetEventSlim Entered { get; } = new();

    /// <summary>Signalled once per held export that has reached validation.</summary>
    public SemaphoreSlim EnteredEach { get; } = new(0);

    public ManualResetEventSlim Release { get; } = new();

    public IReadOnlyList<LogRecordIssue> Validate(LogRecord record)
    {
        if (Interlocked.Increment(ref _calls) <= blockFirst)
        {
            Entered.Set();
            EnteredEach.Release();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
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
