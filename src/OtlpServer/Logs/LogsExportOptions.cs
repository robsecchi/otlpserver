namespace OtlpServer.Logs;

/// <summary>
/// Settings for the logs export core, bound from the "Otlp:Logs" configuration section.
/// (Concurrency limits are not here: they apply to every signal and transport, see
/// Admission/AdmissionOptions, section "Otlp:Admission".)
/// </summary>
public sealed class LogsExportOptions
{
    public const string SectionName = "Otlp:Logs";

    /// <summary>
    /// TESTING ONLY: an artificial delay added to each export, so admission limits (and so
    /// throttling) can be triggered by hand. Keep it at zero in real use.
    /// </summary>
    public TimeSpan SimulatedProcessingDelay { get; set; } = TimeSpan.Zero;
}
