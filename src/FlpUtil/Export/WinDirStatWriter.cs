using System.Globalization;
using FlpUtil.Cli;
using FlpUtil.Index;

namespace FlpUtil.Export;

public sealed class WinDirStatWriteResult
{
    public required long RootLogicalBytes { get; init; }
    public required long RootPhysicalBytes { get; init; }
    public required int FileRows { get; init; }
    public required int FolderRows { get; init; }
    public required int SyntheticRows { get; init; }
    public required int UnsafeValues { get; init; }

    /// <summary>Bytes deliberately left out because they belong to no path — see the writer's notes.</summary>
    public required long OmittedBytes { get; init; }

    public required IReadOnlyList<string> OmittedReasons { get; init; }

    public int TotalRows => FileRows + FolderRows + SyntheticRows + 1;
}

/// <summary>
/// Writes an FLP index's cost as a WinDirStat saved-results file, so WinDirStat's tree/list view and
/// treemap can be pointed at index bytes instead of disk bytes.
///
/// The tree it builds is the shape a real multi-drive WinDirStat scan produces:
///
/// <code>
///   "FLP index: name"                          0x10000001  pseudo root
///     "C:\"                                    0x00000002  drive
///       "C:\Users" ... "C:\...\go"             0x00000004  folders
///         "C:\...\go\&lt;folder entry&gt;"     0x00000008  that folder's own index documents
///         "C:\...\go\MANUAL.html"              0x00000008  a real file
/// </code>
///
/// Three invariants matter, none of which WinDirStat reports on:
/// a parent must be written before its children; a folder must report a non-zero file/folder count
/// or it will not be registered as a parent and its children are dropped; and only <em>drives</em>
/// may be children of the pseudo root — a file or directory there crashes WinDirStat outright.
///
/// Consequently the index-wide bytes (skip lists, <c>.tii</c>, <c>.fnm</c>, headers, and FLP's own
/// index-metadata and deleted documents) are omitted rather than parked under a drive: they belong
/// to no path, cannot be reclaimed by excluding a folder, and would overstate whichever drive they
/// were attached to. The write result reports exactly what was left out.
///
/// <c>Logical Size</c> carries exclusive bytes (what excluding the folder actually reclaims) and
/// <c>Physical Size</c> carries exclusive plus the item's apportioned share of the joint term
/// dictionary (which sums to the real store size). WinDirStat's "use logical size" option toggles
/// which one drives the treemap, so both views live in one file.
/// </summary>
public static class WinDirStatWriter
{
    /// <summary>Synthetic leaf holding a folder's own index documents, so sums stay exact.</summary>
    public const string FolderEntryLeaf = "<folder entry>";

    public static WinDirStatWriteResult Write(
        string outputPath,
        IndexCostReport report,
        CostTree tree,
        string rootLabel,
        IProgressSink? progress = null)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        int unsafeValues = 0;
        int fileRows = 0, folderRows = 0, syntheticRows = 0;

        // Synthetic <folder entry> leaves change the file counts WinDirStat displays, so work out
        // how many fall in each subtree before emitting anything.
        Dictionary<string, int> syntheticCounts = CountSyntheticLeaves(tree);

        // Bytes that belong to no path are left out rather than parked somewhere convenient. They
        // cannot be reclaimed by excluding a folder, so putting them under a drive would overstate
        // that drive; and they cannot hang off the root, because only drives may live there.
        var omitted = new List<string>();
        long omittedBytes = 0;
        foreach (CostRow row in report.Rows.Where(r => r.Owner.StartsWith('<')))
        {
            omittedBytes += row.ApportionedBytes;
            omitted.Add($"{row.Owner} ({row.ApportionedBytes:N0} bytes)");
        }

        if (report.UnattributedBytes > 0)
        {
            omittedBytes += report.UnattributedBytes;
            omitted.Add($"index-wide structures - skip lists, .tii, .fnm, headers "
                + $"({report.UnattributedBytes:N0} bytes)");
        }

        long rootLogical = tree.Roots.Sum(r => r.SubtreeExclusiveBytes);
        long rootPhysical = tree.Roots.Sum(r => r.SubtreeApportionedBytes);

        int rootFiles = tree.Roots.Sum(r => r.SubtreeFileCount)
            + tree.Roots.Sum(r => syntheticCounts.GetValueOrDefault(r.FolderId));
        int rootFolders = tree.Roots.Sum(r => r.SubtreeFolderCount + 1);

        // No BOM: every WinDirStat 2.x handles its absence, but only 2.7+ skips one if present.
        using var stream = File.Create(outputPath);
        using var csv = new CsvWriter(stream, ',', writeBom: false);
        using IProgressScope scope = (progress ?? NullProgress.Instance)
            .Begin("writing rows", rootFiles + rootFolders + 1);

        csv.WriteRow(WinDirStatFormat.RequiredColumns);

        // Newest folder timestamp in the index, so the root and drives show a sensible date rather
        // than the zero FILETIME (which WinDirStat renders as 1601).
        DateTime? newest = tree.Nodes.Select(n => n.LastChange).Where(d => d is not null).DefaultIfEmpty(null).Max();

        WriteRow(csv, rootLabel, WinDirStatFormat.TypeRoot, rootFiles, rootFolders,
            rootLogical, rootPhysical, attributes: string.Empty, lastChange: newest, ref unsafeValues);

        foreach (CostNode driveNode in tree.Roots)
        {
            // A drive's name must end in a backslash: the loader takes the first two characters as
            // the display name and registers both "C:\" and "C:" as parent keys.
            string drivePath = driveNode.Path.EndsWith('\\') ? driveNode.Path : driveNode.Path + '\\';

            WriteRow(csv, drivePath, WinDirStatFormat.TypeDrive,
                driveNode.SubtreeFileCount + syntheticCounts.GetValueOrDefault(driveNode.FolderId),
                driveNode.SubtreeFolderCount,
                driveNode.SubtreeExclusiveBytes, driveNode.SubtreeApportionedBytes,
                attributes: string.Empty, lastChange: driveNode.LastChange ?? newest, ref unsafeValues);
            folderRows++;

            // Pre-order below the drive, so a parent is always written before its children.
            foreach (CostNode node in Descendants(driveNode))
            {
                WriteRow(csv, node.Path, WinDirStatFormat.TypeDirectory,
                    node.SubtreeFileCount + syntheticCounts.GetValueOrDefault(node.FolderId),
                    node.SubtreeFolderCount,
                    node.SubtreeExclusiveBytes, node.SubtreeApportionedBytes,
                    attributes: string.Empty, lastChange: node.LastChange, ref unsafeValues);
                folderRows++;

                EmitLeaves(csv, node, ref fileRows, ref syntheticRows, ref unsafeValues);
                scope.Report(fileRows + folderRows + syntheticRows);
            }

            EmitLeaves(csv, driveNode, ref fileRows, ref syntheticRows, ref unsafeValues);
        }

        return new WinDirStatWriteResult
        {
            RootLogicalBytes = rootLogical,
            RootPhysicalBytes = rootPhysical,
            FileRows = fileRows,
            FolderRows = folderRows,
            SyntheticRows = syntheticRows,
            UnsafeValues = unsafeValues,
            OmittedBytes = omittedBytes,
            OmittedReasons = omitted,
        };
    }

    private static void EmitLeaves(CsvWriter csv, CostNode node,
        ref int fileRows, ref int syntheticRows, ref int unsafeValues)
    {
        // The folder's own index documents, as a leaf, so the folder's size equals the sum of its
        // children exactly rather than quietly exceeding it.
        if (node.OwnExclusiveBytes > 0 || node.OwnApportionedBytes > 0)
        {
            WriteRow(csv, Combine(node.Path, FolderEntryLeaf), WinDirStatFormat.TypeFile, 0, 0,
                node.OwnExclusiveBytes, node.OwnApportionedBytes,
                attributes: string.Empty, lastChange: node.LastChange, ref unsafeValues);
            syntheticRows++;
        }

        foreach (CostRow file in node.Files)
        {
            WriteRow(csv, file.Owner, WinDirStatFormat.TypeFile, 0, 0,
                file.ExclusiveBytes, file.ApportionedBytes,
                AttributesOf(file), file.LastChange, ref unsafeValues);
            fileRows++;
        }

        // Childless indexed directories. Emitted as directories with zero counts, which the loader
        // attaches happily; it just does not register them as parents, which is correct.
        foreach (CostRow empty in node.EmptyFolders)
        {
            WriteRow(csv, empty.Owner, WinDirStatFormat.TypeDirectory, 0, 0,
                empty.ExclusiveBytes, empty.ApportionedBytes,
                AttributesOf(empty), empty.LastChange, ref unsafeValues);
            fileRows++;
        }
    }

    private static string AttributesOf(CostRow row)
    {
        long mask = row.RawAttributes is null
            ? 0
            : FieldDecoders.TryParseNumber(row.RawAttributes, FlpEncoding.HexAttributes) ?? 0;
        return WinDirStatFormat.FormatAttributes(mask);
    }

    private static void WriteRow(CsvWriter csv, string name, uint itemType,
        int files, int folders, long logical, long physical,
        string attributes, DateTime? lastChange, ref int unsafeValues)
    {
        if (!WinDirStatFormat.IsSafeValue(name))
            unsafeValues++;

        csv.WriteRow([
            name,
            files.ToString(CultureInfo.InvariantCulture),
            folders.ToString(CultureInfo.InvariantCulture),
            logical.ToString(CultureInfo.InvariantCulture),
            physical.ToString(CultureInfo.InvariantCulture),
            attributes,
            WinDirStatFormat.FormatTimestamp(lastChange),
            WinDirStatFormat.FormatItemType(itemType),
            WinDirStatFormat.FormatIndex(0),
        ]);
    }

    /// <summary>Pre-order walk of everything below <paramref name="root"/>, excluding itself.</summary>
    private static IEnumerable<CostNode> Descendants(CostNode root)
    {
        var stack = new Stack<CostNode>();
        for (int i = root.Children.Count - 1; i >= 0; i--)
            stack.Push(root.Children[i]);

        while (stack.Count > 0)
        {
            CostNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    /// <summary>
    /// Number of synthetic <c>&lt;folder entry&gt;</c> leaves in each folder's subtree, so the file
    /// counts we report match the rows we actually write.
    /// </summary>
    private static Dictionary<string, int> CountSyntheticLeaves(CostTree tree)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = tree.PreOrder().ToList();

        // Pre-order puts parents before children, so walking it backwards visits children first.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            CostNode node = order[i];
            int own = node.OwnExclusiveBytes > 0 || node.OwnApportionedBytes > 0 ? 1 : 0;
            foreach (CostNode child in node.Children)
                own += counts.GetValueOrDefault(child.FolderId);
            counts[node.FolderId] = own;
        }

        return counts;
    }

    private static string Combine(string parent, string leaf) =>
        parent.EndsWith('\\') ? parent + leaf : $"{parent}\\{leaf}";
}
