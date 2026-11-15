using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Unit;

/// <summary>
/// "Keys MUST be unique" (common.proto), at every level, under both configured policies.
/// </summary>
public sealed class DuplicateKeyValidationTests
{
    private static LogRecordValidator Validator(DuplicateAttributeKeyHandling handling = DuplicateAttributeKeyHandling.Reject) =>
        new(Options.Create(new LogValidationOptions { DuplicateAttributeKeys = handling }));

    // ---- Where duplicates are found (default policy: Reject) ----------------------------

    [Fact]
    public void Resource_with_duplicate_key_is_rejected()
    {
        var issue = Assert.Single(Validator().ValidateResource(ResourceWithDuplicateKey()));

        Assert.Equal(IssueSeverity.Rejection, issue.Severity);
        Assert.Equal("duplicate_resource_attribute_key", issue.Rule);
        Assert.Contains("'service.name'", issue.Detail);
    }

    [Fact]
    public void Scope_with_duplicate_key_is_rejected()
    {
        var issue = Assert.Single(Validator().ValidateScope(ScopeWithDuplicateKey()));

        Assert.Equal(IssueSeverity.Rejection, issue.Severity);
        Assert.Equal("duplicate_scope_attribute_key", issue.Rule);
    }

    [Fact]
    public void Unset_resource_and_scope_have_no_issues()
    {
        Assert.Empty(Validator().ValidateResource(null));
        Assert.Empty(Validator().ValidateScope(null));
    }

    [Fact]
    public void Duplicate_inside_a_nested_attribute_map_is_found()
    {
        var record = ValidRecord();
        record.Attributes.Add(new KeyValue
        {
            Key = "http",
            Value = Map(Attribute("method", "GET"), Attribute("method", "POST")),
        });

        var issue = Assert.Single(Validator().Validate(record));
        Assert.Equal("duplicate_attribute_key", issue.Rule);
        Assert.Contains("attributes[\"http\"]", issue.Detail);
    }

    [Fact]
    public void Duplicate_inside_a_map_in_an_array_in_the_body_is_found()
    {
        var record = ValidRecord();
        record.Body = new AnyValue
        {
            ArrayValue = new ArrayValue
            {
                Values =
                {
                    Map(Attribute("a", "1")),
                    Map(Attribute("b", "1"), Attribute("b", "2")),
                },
            },
        };

        var issue = Assert.Single(Validator().Validate(record));
        Assert.Contains("'b'", issue.Detail);
        Assert.Contains("body[1]", issue.Detail);
    }

    [Fact]
    public void Same_key_in_different_maps_is_not_a_duplicate()
    {
        // Uniqueness is per map: "id" at the top level and "id" inside a nested map are fine.
        var record = ValidRecord();
        record.Attributes.Add(Attribute("id", "outer"));
        record.Attributes.Add(new KeyValue { Key = "user", Value = Map(Attribute("id", "inner")) });
        record.Body = Map(Attribute("id", "body"));

        Assert.Empty(Validator().Validate(record));
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        var record = ValidRecord();
        record.Attributes.Add(Attribute("K", "upper")); // ValidRecord already has "k"

        Assert.Empty(Validator().Validate(record));
    }

    // ---- AcceptWithWarning policy --------------------------------------------------------

    [Fact]
    public void Accept_policy_turns_every_level_into_a_warning()
    {
        var validator = Validator(DuplicateAttributeKeyHandling.AcceptWithWarning);

        var issues = validator.ValidateResource(ResourceWithDuplicateKey())
            .Concat(validator.ValidateScope(ScopeWithDuplicateKey()))
            .Concat(validator.Validate(RecordWithDuplicateKey()))
            .ToList();

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        // The message tells the sender why their data was accepted anyway.
        Assert.All(issues, i => Assert.Contains("DuplicateAttributeKeys=AcceptWithWarning", i.Detail));
    }

    [Fact]
    public void Accept_policy_does_not_relax_other_rules()
    {
        var record = RecordWithDuplicateKey();
        record.SeverityNumber = (OpenTelemetry.Proto.Logs.V1.SeverityNumber)99;

        var issues = Validator(DuplicateAttributeKeyHandling.AcceptWithWarning).Validate(record);

        Assert.Contains(issues, i => i.Rule == "severity_number_out_of_range" && i.Severity == IssueSeverity.Rejection);
    }
}
