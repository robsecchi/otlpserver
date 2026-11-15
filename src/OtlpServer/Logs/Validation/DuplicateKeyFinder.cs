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
/// ALLOCATION-FREE when there is no duplicate, which is almost always: this runs for every
/// record, millions of times per second at load. So:
/// </para>
/// <list type="bullet">
///   <item>Small sets (≤ <see cref="PairwiseLimit"/> keys, the usual case) are checked by
///         comparing each key with the ones before it: no HashSet to allocate. For a handful
///         of keys that's also faster than hashing. Bigger sets use a HashSet.</item>
///   <item>Indexed loops: <c>foreach</c> over a RepeatedField allocates an enumerator.</item>
///   <item>The path to a duplicate (e.g. <c>attributes["http"][2]</c>) is only built once a
///         duplicate is found, while returning from the recursion.</item>
/// </list>
/// <para>
/// Recursion depth is safe: the protobuf parser already refused anything nested deeper than
/// its recursion limit (100) before validation runs.
/// </para>
/// </summary>
internal static class DuplicateKeyFinder
{
    private const int PairwiseLimit = 16;

    /// <param name="path">Name of the set being checked, used in the result (e.g. "attributes").</param>
    /// <returns>Where the duplicate is and which key it is, or null if all keys are unique.</returns>
    public static (string Path, string Key)? Find(RepeatedField<KeyValue> keyValues, string path) =>
        FindIn(keyValues) is { } hit ? (hit.PathFrom(path), hit.Key) : null;

    /// <summary>Looks for duplicates inside a single value (e.g. a log record's body).</summary>
    public static (string Path, string Key)? Find(AnyValue? value, string path) =>
        FindIn(value) is { } hit ? (hit.PathFrom(path), hit.Key) : null;

    private static Hit? FindIn(RepeatedField<KeyValue> keyValues)
    {
        var count = keyValues.Count;

        // This level first, so the reported duplicate is the shallowest one. The duplicate
        // reported is the SECOND occurrence of the first key that repeats.
        if (count <= PairwiseLimit)
        {
            for (var i = 1; i < count; i++)
            {
                var key = keyValues[i].Key;
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(keyValues[j].Key, key, StringComparison.Ordinal)) return new Hit(key);
                }
            }
        }
        else
        {
            var seen = new HashSet<string>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if (!seen.Add(keyValues[i].Key)) return new Hit(keyValues[i].Key);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var keyValue = keyValues[i];
            if (FindIn(keyValue.Value) is { } nested) return nested.Under($"[\"{keyValue.Key}\"]");
        }
        return null;
    }

    private static Hit? FindIn(AnyValue? value)
    {
        switch (value?.ValueCase)
        {
            case AnyValue.ValueOneofCase.KvlistValue:
                return FindIn(value.KvlistValue.Values);

            case AnyValue.ValueOneofCase.ArrayValue:
                var items = value.ArrayValue.Values;
                for (var i = 0; i < items.Count; i++)
                {
                    if (FindIn(items[i]) is { } nested) return nested.Under($"[{i}]");
                }
                return null;

            default:
                return null; // scalars and bytes can't contain keys
        }
    }

    /// <summary>A found duplicate. Only ever created when there IS one, so it may allocate.</summary>
    private sealed class Hit(string key)
    {
        private List<string>? _segments; // innermost first, added while the recursion unwinds

        public string Key { get; } = key;

        public Hit Under(string segment)
        {
            (_segments ??= []).Add(segment);
            return this;
        }

        public string PathFrom(string root)
        {
            if (_segments is null) return root;
            _segments.Reverse();
            return root + string.Concat(_segments);
        }
    }
}
