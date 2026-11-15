namespace OtlpServer.Logs;

/// <summary>
/// What the core knows about WHO sent an export, filled in by the transport from the
/// authenticated request. Transport-neutral: no HttpContext here.
/// <para>
/// Used to name the client in data-quality warnings today; per-client metrics (and later,
/// storage tagging or quarantine per source) build on the same value.
/// </para>
/// </summary>
/// <param name="Client">The authenticated client (API-key client name or JWT client claim); null when authentication is off.</param>
public readonly record struct ExportContext(string? Client)
{
    public static ExportContext Anonymous => default;

    /// <summary>For logs: the client, or "anonymous".</summary>
    public string ClientOrAnonymous => Client ?? "anonymous";
}
