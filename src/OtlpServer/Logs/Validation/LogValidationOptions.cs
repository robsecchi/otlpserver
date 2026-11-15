namespace OtlpServer.Logs.Validation;

/// <summary>
/// Validation policy settings, bound from the "Otlp:Logs:Validation" configuration section.
/// </summary>
public sealed class LogValidationOptions
{
    public const string SectionName = "Otlp:Logs:Validation";

    /// <summary>
    /// What to do when an attribute set has the same key more than once. Applies to resource
    /// attributes, scope attributes, record attributes, and nested key-value lists (also
    /// inside the body), because the spec requires unique keys in all of them.
    /// </summary>
    public DuplicateAttributeKeyHandling DuplicateAttributeKeys { get; set; } = DuplicateAttributeKeyHandling.Reject;
}

public enum DuplicateAttributeKeyHandling
{
    /// <summary>
    /// Spec-compliant default. A record with duplicate keys is rejected. A resource or scope
    /// with duplicate keys rejects every record under it, since those records would have
    /// an ambiguous identity (e.g. two different service.name values).
    /// </summary>
    Reject,

    /// <summary>
    /// For importing existing "dirty" datasets that can't be cleaned. The data is accepted
    /// unchanged and each problem is reported as a warning in <c>partial_success</c>.
    /// Which value wins (or whether such data is kept apart, e.g. in a quarantine) is up to
    /// the storage layer.
    /// </summary>
    AcceptWithWarning,
}
