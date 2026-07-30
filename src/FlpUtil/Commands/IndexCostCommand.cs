using System.Globalization;
using FlpUtil.Export;
using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>
/// Reports how many index bytes each file is responsible for.
///
/// Three buckets, not one number: bytes exclusive to a file, bytes it shares with others (reported
/// whole, never divided), and bytes belonging to no file at all. The reconciliation table is
/// printed first, because a per-file figure is only worth reading if the computed segment totals
/// match the real ones.
/// </summary>
public static class IndexCostCommand
{
    public static int Run(string indexPath, string? outputPath, int top, char delimiter, bool byFolder, int depth)
    {
        using var reader = new FlpIndexReader(indexPath);

        Console.WriteLine($"Analysing {reader.IndexPath}");
        Console.WriteLine($"  {reader.NumDocs:N0} live documents, {reader.NumDeletedDocs:N0} deleted");
        Console.WriteLine();

        IndexCostReport report = new IndexCostAnalyzer(reader).Analyze();

        PrintReconciliation(report);
        PrintSummary(report);

        PrintClosure(report);

        if (byFolder)
        {
            PrintFolders(report, top, depth);
        }
        else
        {
            var files = report.Rows.Where(r => !r.IsFolder && !r.Owner.StartsWith('<')).ToList();
            PrintTop(files, report, top);
        }

        if (outputPath is not null)
            WriteCsv(report, outputPath, delimiter);

        return 0;
    }

    /// <summary>
    /// Folders ranked by what their whole subtree costs — the view that answers "which folder should
    /// I stop indexing?". Each folder's own entry cost is shown separately from its descendants', so
    /// a folder that is expensive only because of one child is distinguishable from one that is
    /// expensive throughout.
    /// </summary>
    private static void PrintFolders(IndexCostReport report, int top, int depth)
    {
        CostTree tree = CostTree.Build(report);

        if (tree.Orphans.Count > 0)
            Console.WriteLine($"warning: {tree.Orphans.Count:N0} row(s) are not under any folder.");

        var ranked = tree.ByCostDescending()
            .Where(n => depth <= 0 || n.Depth <= depth)
            .Take(top)
            .ToList();

        Console.WriteLine($"Top {ranked.Count} folders by subtree cost"
            + (depth > 0 ? $" (depth <= {depth})" : string.Empty) + ":");
        Console.WriteLine($"  {"subtree",14} {"exclusive",14} {"own",8} {"files",8} {"dirs",7}  folder");

        foreach (CostNode node in ranked)
        {
            Console.WriteLine($"  {node.SubtreeApportionedBytes,14:N0} {node.SubtreeExclusiveBytes,14:N0} "
                + $"{node.OwnExclusiveBytes,8:N0} {node.SubtreeFileCount,8:N0} {node.SubtreeFolderCount,7:N0}  "
                + Shorten(node.Path));
        }

        Console.WriteLine();
        Console.WriteLine("  subtree = including apportioned shared dictionary; exclusive = reclaimed if excluded;");
        Console.WriteLine("  own = this folder's own index documents, not its contents.");
    }

    private static void PrintReconciliation(IndexCostReport report)
    {
        Console.WriteLine("Reconciliation - computed bytes vs the segment's actual size:");
        Console.WriteLine($"  {"segment",-18} {"actual",14} {"computed",14} {"residual",12}   {"",-6}");

        foreach (SegmentCheck check in report.Checks)
        {
            string note = Explain(check, report);
            Console.WriteLine($"  {check.File,-18} {check.Actual,14:N0} {check.Computed,14:N0} {check.Residual,12:N0}   {note}");
        }

        long actual = report.Checks.Sum(c => c.Actual);
        long computed = report.Checks.Sum(c => c.Computed);
        Console.WriteLine($"  {"TOTAL",-18} {actual,14:N0} {computed,14:N0} {actual - computed,12:N0}   "
            + $"{100.0 * computed / Math.Max(actual, 1):0.0}% attributed to documents");
        Console.WriteLine();
        Console.WriteLine($"  The .frq leftover is {report.SkipEntries:N0} skip-list entries across all terms "
            + $"({SkipBytesPerEntry(report):0.0} bytes each) - a per-term structure that belongs to no");
        Console.WriteLine("  single document. Everything else unattributed is a file header or the .tii/.fnm index.");
        Console.WriteLine();

        if (report.HasPayloads)
            Console.WriteLine("  note: this index uses payloads; their bytes are included in the position figures.");
    }

    /// <summary>
    /// Names what a residual actually is. An unexplained remainder would undermine the whole point,
    /// so each one is either exact or attributed to a specific Lucene structure.
    /// </summary>
    private static string Explain(SegmentCheck check, IndexCostReport report)
    {
        if (check.Residual == 0)
            return "exact";

        if (check.Computed == 0 && check.Actual > 0)
            return "index-wide, not per-file";

        string percent = $"{100.0 * check.Residual / Math.Max(check.Actual, 1):+0.00;-0.00}%";

        if (check.File.StartsWith(".frq", StringComparison.Ordinal))
            return $"{percent} = per-term skip lists";
        if (check.File.StartsWith(".tis", StringComparison.Ordinal))
            return $"{percent} = file header + skip pointers";
        if (check.File.StartsWith(".fdt", StringComparison.Ordinal) || check.File.StartsWith(".nrm", StringComparison.Ordinal))
            return $"{percent} = per-segment file headers";

        return percent;
    }

    private static double SkipBytesPerEntry(IndexCostReport report)
    {
        SegmentCheck? frq = report.Checks.FirstOrDefault(c => c.File.StartsWith(".frq", StringComparison.Ordinal));
        return frq is null || report.SkipEntries == 0 ? 0 : (double)frq.Residual / report.SkipEntries;
    }

    private static void PrintSummary(IndexCostReport report)
    {
        Console.WriteLine("Term dictionary split:");
        Console.WriteLine($"  sole-owner entries  {report.SoleDictionaryBytes,14:N0} bytes  (charged to one file)");
        Console.WriteLine($"  shared entries      {report.SharedDictionaryBytes,14:N0} bytes  (joint - reported, never divided)");
        Console.WriteLine();
        Console.WriteLine($"Flat per-document cost: 8 bytes (.fdx) + {report.NormFieldCount} bytes (.nrm, one per indexed field)");
        Console.WriteLine("  Every file occupies two documents (item + meta), so its floor is "
            + $"{2 * (8 + report.NormFieldCount)} bytes plus its stored field text.");
        Console.WriteLine();
    }

    /// <summary>
    /// Proves the books balance: every attributed byte, plus the joint dictionary counted once, plus
    /// the named per-term and index-wide structures, must equal the store exactly.
    /// </summary>
    private static void PrintClosure(IndexCostReport report)
    {
        long exclusive = report.Rows.Sum(r => r.ExclusiveBytes);
        long unattributed = report.Checks.Sum(c => c.Actual) - report.Checks.Sum(c => c.Computed);
        long actual = report.Checks.Sum(c => c.Actual);
        long accounted = exclusive + report.SharedDictionaryBytes + unattributed;

        Console.WriteLine("Accounting:");
        Console.WriteLine($"  exclusive to one owner        {exclusive,14:N0}   {100.0 * exclusive / actual,5:F1}%");
        Console.WriteLine($"  shared term dictionary        {report.SharedDictionaryBytes,14:N0}   {100.0 * report.SharedDictionaryBytes / actual,5:F1}%   (joint - not divided)");
        Console.WriteLine($"  belongs to no file            {unattributed,14:N0}   {100.0 * unattributed / actual,5:F1}%   (skip lists, .tii, .fnm, headers)");
        Console.WriteLine($"  {"=",-28} {accounted,14:N0}");
        Console.WriteLine($"  actual index size             {actual,14:N0}   "
            + (accounted == actual ? "balances exactly" : $"OFF BY {actual - accounted:N0}"));
        Console.WriteLine();
    }

    private static void PrintTop(List<CostRow> files, IndexCostReport report, int top)
    {
        long exclusive = files.Sum(r => r.ExclusiveBytes);
        int folders = report.Rows.Count(r => r.IsFolder);
        Console.WriteLine($"{files.Count:N0} files ({exclusive:N0} bytes exclusively theirs), {folders:N0} folders.");
        Console.WriteLine();
        Console.WriteLine($"Top {top} by exclusive bytes:");
        Console.WriteLine($"  {"exclusive",12} {"positions",12} {"postings",10} {"stored",8} {"soleTerms",10} {"shared",10}  file");

        foreach (CostRow row in files.Take(top))
        {
            Console.WriteLine($"  {row.ExclusiveBytes,12:N0} {row.PositionBytes,12:N0} {row.PostingBytes,10:N0} "
                + $"{row.StoredBytes,8:N0} {row.SoleTermBytes,10:N0} {row.SharedTermBytes,10:N0}  {Shorten(row.Owner)}");
        }

        Console.WriteLine();
        long cumulative = 0;
        int nineties = 0;
        foreach (CostRow row in files)
        {
            cumulative += row.ExclusiveBytes;
            nineties++;
            if (cumulative >= exclusive * 0.9)
                break;
        }

        Console.WriteLine($"90% of exclusive index bytes come from {nineties:N0} of {files.Count:N0} files "
            + $"({100.0 * nineties / Math.Max(files.Count, 1):0.0}%).");
    }

    private static string Shorten(string path)
    {
        const int limit = 58;
        return path.Length <= limit ? path : "…" + path[^(limit - 1)..];
    }

    private static void WriteCsv(IndexCostReport report, string outputPath, char delimiter)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(outputPath);
        using var csv = new CsvWriter(stream, delimiter);

        csv.WriteRow([
            "Owner", "IsFolder", "Docs", "ExclusiveBytes",
            "StoredBytes", "NormBytes", "PostingBytes", "PositionBytes", "SoleTermBytes",
            "SharedTermBytes", "SharedTermCount",
        ]);

        foreach (CostRow row in report.Rows)
        {
            csv.WriteRow([
                row.Owner,
                row.IsFolder ? "Y" : "N",
                N(row.DocCount),
                N(row.ExclusiveBytes),
                N(row.StoredBytes),
                N(row.NormBytes),
                N(row.PostingBytes),
                N(row.PositionBytes),
                N(row.SoleTermBytes),
                N(row.SharedTermBytes),
                N(row.SharedTermCount),
            ]);
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {report.Rows.Count:N0} rows to {Path.GetFullPath(outputPath)}");
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
