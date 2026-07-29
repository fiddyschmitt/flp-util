namespace FlpUtil.Index;

/// <summary>
/// One document read out of an FLP (Lucene) index, flattened to string values.
/// A field can legitimately appear more than once, hence the list.
/// </summary>
public sealed class IndexDoc
{
    public required int DocId { get; init; }

    public required Dictionary<string, List<string>> Fields { get; init; }

    public bool Has(string field) => Fields.ContainsKey(field);

    /// <summary>First value of <paramref name="field"/>, or null when absent.</summary>
    public string? Get(string field) =>
        Fields.TryGetValue(field, out var values) && values.Count > 0 ? values[0] : null;

    public IReadOnlyList<string> GetAll(string field) =>
        Fields.TryGetValue(field, out var values) ? values : [];
}
