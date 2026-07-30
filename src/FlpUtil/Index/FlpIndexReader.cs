using System.Globalization;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace FlpUtil.Index;

/// <summary>
/// Read-only access to a FileLocator Pro index store.
///
/// FLP's indexer is built on Lucene++ (a C++ port of Java Lucene 3.x), so an index store is a
/// plain Lucene 3.0 directory (segments.gen, segments_N, and either a _N.cfs compound file or
/// loose _N.fdt/.fdx/.fnm/... files). Lucene.NET 4.8's read-only Lucene3x codec is selected
/// automatically by DirectoryReader.Open when it sees a pre-4.0 segments header.
///
/// Lucene contact is confined to this class and to <see cref="IndexCostAnalyzer"/>, which needs
/// the raw term enumerators to attribute index bytes.
/// </summary>
public sealed class FlpIndexReader : IDisposable
{
    private readonly LuceneDirectory _directory;
    private readonly DirectoryReader _reader;

    /// <summary>Raw reader, for byte-level analysis. Read-only; never wrap this in an IndexWriter.</summary>
    internal DirectoryReader Raw => _reader;

    internal LuceneDirectory Store => _directory;

    public FlpIndexReader(string indexPath)
    {
        if (!System.IO.Directory.Exists(indexPath))
            throw new DirectoryNotFoundException($"Index store not found: {indexPath}");

        IndexPath = Path.GetFullPath(indexPath);

        // SimpleFSDirectory + a no-op lock factory: we never write, and we must not interfere
        // with flpidx.exe if it happens to be updating the index at the same time.
        _directory = new SimpleFSDirectory(new DirectoryInfo(IndexPath), NoLockFactory.GetNoLockFactory());
        _reader = DirectoryReader.Open(_directory);
    }

    public string IndexPath { get; }

    /// <summary>Document slots, including ones marked deleted.</summary>
    public int MaxDoc => _reader.MaxDoc;

    /// <summary>Live (non-deleted) documents.</summary>
    public int NumDocs => _reader.NumDocs;

    public int NumDeletedDocs => _reader.NumDeletedDocs;

    public IReadOnlyList<string> SegmentNames =>
        [.. _reader.Leaves.Select(leaf => leaf.Reader.ToString() ?? "?")];

    /// <summary>
    /// Field names that are <em>indexed</em>. Stored-only fields do not appear here, so this is
    /// a lower bound on the schema — <see cref="ReadAll"/> is what discovers the true column set.
    /// </summary>
    public IReadOnlyList<string> IndexedFieldNames
    {
        get
        {
            var fields = MultiFields.GetFields(_reader);
            return fields is null ? [] : [.. fields.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Streams every live document. Cheap to call twice — the export makes one pass to build the
    /// folder tree and discover columns, then a second pass to write rows.
    /// </summary>
    public IEnumerable<IndexDoc> ReadAll()
    {
        IBits? liveDocs = MultiFields.GetLiveDocs(_reader);

        for (int docId = 0; docId < _reader.MaxDoc; docId++)
        {
            // FLP deletes and re-adds a document whenever a file changes, so an index that has
            // been updated in place will have plenty of dead slots.
            if (liveDocs is not null && !liveDocs.Get(docId))
                continue;

            Document doc = _reader.Document(docId);

            var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (IIndexableField field in doc.Fields)
            {
                if (!fields.TryGetValue(field.Name, out var values))
                    fields[field.Name] = values = [];
                values.Add(ToStringValue(field));
            }

            yield return new IndexDoc { DocId = docId, Fields = fields };
        }
    }

    /// <summary>
    /// Flattens a stored field to text. Values arrive as string, numeric or raw bytes depending
    /// on how FLP wrote them; binary falls back to hex so nothing is silently dropped.
    /// </summary>
    private static string ToStringValue(IIndexableField field)
    {
        if (field.GetStringValue() is { } text)
            return text;

        string? number = field.NumericType switch
        {
            NumericFieldType.BYTE => field.GetByteValue()?.ToString(CultureInfo.InvariantCulture),
            NumericFieldType.INT16 => field.GetInt16Value()?.ToString(CultureInfo.InvariantCulture),
            NumericFieldType.INT32 => field.GetInt32Value()?.ToString(CultureInfo.InvariantCulture),
            NumericFieldType.INT64 => field.GetInt64Value()?.ToString(CultureInfo.InvariantCulture),
            NumericFieldType.SINGLE => field.GetSingleValue()?.ToString("R", CultureInfo.InvariantCulture),
            NumericFieldType.DOUBLE => field.GetDoubleValue()?.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
        if (number is not null)
            return number;

        if (field.GetBinaryValue() is { } bytes)
            return "0x" + Convert.ToHexString(bytes.Bytes, bytes.Offset, bytes.Length);

        return string.Empty;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _directory.Dispose();
    }
}
