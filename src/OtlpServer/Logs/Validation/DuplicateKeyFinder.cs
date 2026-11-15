using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;

namespace OtlpServer.Logs.Validation;

/// <summary>
/// Finds the first repeated key in an attribute set, looking inside nested values too.
///
/// <para>
/// From common.proto: attribute keys "MUST be unique", and a <c>KeyValueList</c>'s keys
/// "MUST be unique" as well. A <c>KeyValueList</c> can sit inside any <c>AnyValue</c>, so
/// the search follows every path where one can appear:
/// </para>
/// <code>
///  attributes ─┬─ key "a" → AnyValue.kvlist_value ─── keys must be unique here too
///              └─ key "b" → AnyValue.array_value ──┬─ [0] AnyValue.kvlist_value ─── and here
///                                                  └─ [1] ...
/// </code>
/// <para>
/// Recursion depth is safe: the protobuf parser already refused anything nested deeper
/// than its recursion limit (100) before validation runs.
/// </para>
/// </summary>
internal static class DuplicateKeyFinder
{
    /// <param name="path">Name of the set being checked, used in the result (e.g. "attributes").</param>
    /// <returns>Where the duplicate is and which key it is, or null if all keys are unique.</returns>
    public static (string Path, string Key)? Find(RepeatedField<KeyValue> keyValues, string path)
    {
        // Check this level first, so the reported duplicate is the shallowest one.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyValue in keyValues)
        {
            if (!seen.Add(keyValue.Key))
            {
                return (path, keyValue.Key);
            }
        }

        foreach (var keyValue in keyValues)
        {
            if (Find(keyValue.Value, $"{path}[\"{keyValue.Key}\"]") is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Looks for duplicates inside a single value (e.g. a log record's body).</summary>
    public static (string Path, string Key)? Find(AnyValue? value, string path)
    {
        switch (value?.ValueCase)
        {
            case AnyValue.ValueOneofCase.KvlistValue:
                return Find(value.KvlistValue.Values, path);

            case AnyValue.ValueOneofCase.ArrayValue:
                var items = value.ArrayValue.Values;
                for (var i = 0; i < items.Count; i++)
                {
                    if (Find(items[i], $"{path}[{i}]") is { } nested)
                    {
                        return nested;
                    }
                }
                return null;

            default:
                return null; // scalars and bytes can't contain keys
        }
    }
}
