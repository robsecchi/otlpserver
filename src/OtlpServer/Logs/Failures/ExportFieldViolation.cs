namespace OtlpServer.Logs.Failures;

/// <summary>
/// Points to one problem in the request: which field, and what's wrong with it.
/// Same shape as <c>google.rpc.BadRequest.FieldViolation</c>, but transport-neutral.
/// </summary>
/// <param name="Field">Path into the request, e.g. "resource_logs[0].scope_logs[0].log_records[3]".</param>
/// <param name="Description">What is wrong and how to fix it.</param>
public readonly record struct ExportFieldViolation(string Field, string Description);
