using System.Globalization;
using FlpUtil.Cli;
using FlpUtil.Export;
using FlpUtil.Index;

namespace FlpUtil.Commands;

public sealed class ExportOptions
{
    public required string IndexPath { get; init; }
    public required string OutputPath { get; init; }
    public bool IncludeFolders { get; init; }
    public bool Raw { get; init; }
    public char Delimiter { get; init; } = ',';
    public string MultiValueSeparator { get; init; } = "|";
}

/// <summary>
/// Exports every item in an FLP index, with all of its metadata, to CSV.
///
/// FLP splits an item across two Lucene documents — the item document (name, size, timestamps,
/// attributes) and a meta document (indexing status) — joined on <c>id</c> == <c>mid</c>. Neither
/// carries a path, so the folder documents have to be assembled into a tree first.
///
/// Pass one builds that tree, the meta lookup and the real column set (stored-only fields never
/// appear in Lucene's indexed-field list); pass two streams the rows out.
/// </summary>
public static class ExportCommand
{
    private static readonly string[] DecodedColumns =
    [
        "FullPath", "Folder", "Name", "Extension", "IsFolder", "SizeBytes",
        "Modified", "Created", "IndexedDate",
        "ContentIndexed", "OtherFlagBits", "TermCount",
        "Attributes", "ItemType", "FolderId", "ItemId", "DocId", "MetaDocId",
    ];

    public static int Run(ExportOptions options, IProgressSink? progress = null)
    {
        IProgressSink sink = progress ?? NullProgress.Instance;
        using var reader = new FlpIndexReader(options.IndexPath);

        Console.WriteLine($"Reading {reader.IndexPath}");
        Console.WriteLine($"  {reader.NumDocs:N0} live documents ({reader.NumDeletedDocs:N0} deleted)");

        var scan = Scan(reader, sink);
        Console.WriteLine($"  {scan.Folders.Count:N0} folders, {scan.Meta.Count:N0} meta records, "
            + $"{scan.Fields.Count:N0} distinct stored fields");

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var rawColumns = scan.Fields.Order(StringComparer.Ordinal).ToList();
        List<string> header = options.Raw
            ? [.. rawColumns.Select(RawColumnName)]
            : [.. DecodedColumns, .. rawColumns.Select(RawColumnName)];

        long files = 0, folders = 0, unmatched = 0;

        using (var stream = File.Create(options.OutputPath))
        using (var csv = new CsvWriter(stream, options.Delimiter))
        {
            csv.WriteRow(header);
            using IProgressScope writeScope = sink.Begin("pass 2/2 writing", reader.MaxDoc);

            foreach (var doc in reader.ReadAll())
            {
                writeScope.Report(doc.DocId + 1);
                if (!FlpSchema.IsItemDoc(doc))
                    continue;

                bool isFolder = IsFolderItem(doc);
                if (isFolder && !options.IncludeFolders)
                    continue;

                string itemId = doc.Get(FlpSchema.ItemId) ?? string.Empty;
                MetaRecord? meta = scan.Meta.GetValueOrDefault(itemId);
                if (meta is null)
                    unmatched++;

                var row = new List<string?>(header.Count);
                if (!options.Raw)
                    row.AddRange(BuildDecodedCells(doc, meta, scan.Folders, isFolder));

                foreach (string field in rawColumns)
                    row.Add(RawValue(doc, meta, field, options.MultiValueSeparator));

                csv.WriteRow(row);

                if (isFolder)
                    folders++;
                else
                    files++;
            }
        }

        Console.WriteLine($"Wrote {files:N0} file rows"
            + (options.IncludeFolders ? $" and {folders:N0} folder rows" : string.Empty)
            + $" to {Path.GetFullPath(options.OutputPath)}");

        if (unmatched > 0)
            Console.WriteLine($"  warning: {unmatched:N0} item(s) had no matching meta document; status columns are blank for those.");
        if (!options.IncludeFolders)
            Console.WriteLine("  (folder entries omitted; pass --include-folders to include them)");

        return 0;
    }

    private sealed record MetaRecord(int DocId, Dictionary<string, List<string>> Fields)
    {
        public string? Get(string field) =>
            Fields.TryGetValue(field, out var values) && values.Count > 0 ? values[0] : null;
    }

    private sealed record ScanResult(
        FolderTree Folders,
        Dictionary<string, MetaRecord> Meta,
        HashSet<string> Fields);

    /// <summary>Pass one: folder tree, meta lookup, and the union of stored field names.</summary>
    private static ScanResult Scan(FlpIndexReader reader, IProgressSink sink)
    {
        var folders = new FolderTree();
        var meta = new Dictionary<string, MetaRecord>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);

        using IProgressScope scope = sink.Begin("pass 1/2 scanning", reader.MaxDoc);

        foreach (var doc in reader.ReadAll())
        {
            scope.Report(doc.DocId + 1);
            if (FlpSchema.IsIndexDoc(doc))
                continue;

            if (FlpSchema.IsFolderDoc(doc))
            {
                folders.Add(doc);
                continue;
            }

            if (FlpSchema.IsMetaDoc(doc))
            {
                if (doc.Get(FlpSchema.MetaId) is { Length: > 0 } key)
                    meta[key] = new MetaRecord(doc.DocId, doc.Fields);
                foreach (string name in doc.Fields.Keys)
                    fields.Add(name);
                continue;
            }

            if (FlpSchema.IsItemDoc(doc))
            {
                foreach (string name in doc.Fields.Keys)
                    fields.Add(name);
            }
        }

        return new ScanResult(folders, meta, fields);
    }

    private static List<string?> BuildDecodedCells(IndexDoc doc, MetaRecord? meta, FolderTree folders, bool isFolder)
    {
        (string folderId, string idName) = FlpSchema.SplitItemId(doc.Get(FlpSchema.ItemId));
        string name = doc.Get(FlpSchema.ItemName) is { Length: > 0 } stored ? stored : idName;

        var flags = FieldDecoders.DecodeIndexFlags(meta?.Get(FlpSchema.IndexFlags));
        long? size = FieldDecoders.TryParseNumber(doc.Get(FlpSchema.Size), FlpEncoding.DecimalNumber);
        long? terms = FieldDecoders.TryParseNumber(meta?.Get(FlpSchema.TermCount), FlpEncoding.HexNumber);

        return
        [
            folders.ResolveItemPath(folderId, name),
            folders.ResolveFolderPath(folderId),
            name,
            isFolder ? string.Empty : ExtensionOf(name),
            isFolder ? "Y" : "N",
            isFolder ? null : size?.ToString(CultureInfo.InvariantCulture),
            FieldDecoders.FormatTimestamp(
                FieldDecoders.TryParseFileTime(doc.Get(FlpSchema.Modified), FlpEncoding.DecimalFileTime)),
            FieldDecoders.FormatTimestamp(
                FieldDecoders.TryParseFileTime(doc.Get(FlpSchema.Created), FlpEncoding.DecimalFileTime)),
            FieldDecoders.FormatTimestamp(
                FieldDecoders.TryParseFileTime(meta?.Get(FlpSchema.IndexedDate), FlpEncoding.DecimalFileTime)),
            flags.IsKnown ? (flags.HasContent ? "Y" : "N") : string.Empty,
            flags.OtherBits,
            terms?.ToString(CultureInfo.InvariantCulture),
            FieldDecoders.FormatFileAttributes(doc.Get(FlpSchema.Attributes)),
            doc.Get(FlpSchema.ItemType),
            folderId,
            doc.Get(FlpSchema.ItemId),
            doc.DocId.ToString(CultureInfo.InvariantCulture),
            meta?.DocId.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private static string? RawValue(IndexDoc doc, MetaRecord? meta, string field, string separator)
    {
        var values = doc.GetAll(field);
        if (values.Count == 0 && meta is not null && meta.Fields.TryGetValue(field, out var metaValues))
            values = metaValues;

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => string.Join(separator, values),
        };
    }

    /// <summary>
    /// Directory items carry <c>itemtype</c> 4; the attribute mask is checked too so a future
    /// itemtype value cannot quietly turn folders into files.
    /// </summary>
    private static bool IsFolderItem(IndexDoc doc) =>
        doc.Get(FlpSchema.ItemType) == FlpSchema.ItemTypeFolder
        || FieldDecoders.HasAttribute(doc.Get(FlpSchema.Attributes), FileAttributes.Directory);

    private static string ExtensionOf(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..] : string.Empty;
    }

    /// <summary>Raw columns are prefixed so they can never collide with a decoded column name.</summary>
    private static string RawColumnName(string field) => "raw_" + field;
}
