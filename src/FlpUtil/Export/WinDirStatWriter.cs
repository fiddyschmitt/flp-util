using System.Globalization;
using FlpUtil.Cli;
using FlpUtil.Index;

namespace FlpUtil.Export;

public sealed class WinDirStatWriteResult
{
    /// <summary>Sum of plain file sizes — the Logical Size column.</summary>
    public required long RootLogicalBytes { get; init; }

    /// <summary>Total index cost — the Physical Size column; plus omitted bytes it equals the store.</summary>
    public required long RootPhysicalBytes { get; init; }
    public required int FileRows { get; init; }

    /// <summary>Directory-typed rows: the folder tree plus childless indexed directories.</summary>
    public required int FolderRows { get; init; }
    public required int SyntheticRows { get; init; }
    public required int UnsafeValues { get; init; }

    /// <summary>Folders that exist only because an item's path implied them — see the writer's notes.</summary>
    public required int ImplicitFolders { get; init; }

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
/// WinDirStat enforces three invariants silently: a parent must be written before its children; a
/// folder must report a non-zero item count or its children are dropped; and only drives may be
/// children of the pseudo root — anything else there crashes it. Its parent lookup is also
/// case-sensitive and knows nothing about containers, while FLP's folder documents can disagree
/// with themselves about a path (same-subject emails differing only in case, attachment paths
/// implying folders no document declares).
///
/// The writer therefore does not trust the id-space tree for structure. Every emitted row is placed
/// in a path trie first, where a child's path is derived from its parent's <em>emitted</em> path —
/// so a parent row always exists, always precedes its children, and always agrees byte-for-byte
/// with the prefix its children use. Folders nobody declared but some item's path implies are
/// synthesized. Sums are computed bottom-up over the trie, so every folder equals the sum of its
/// children by construction.
///
/// Index-wide bytes (skip lists, <c>.tii</c>, <c>.fnm</c>, headers, FLP's own metadata and deleted
/// documents) are omitted rather than parked under a drive: they belong to no path and cannot be
/// reclaimed by excluding a folder. The write result reports exactly what was left out.
///
/// <c>Physical Size</c> carries index cost — exclusive bytes plus the item's apportioned share
/// of the joint term dictionary — and sums to the real store size; it drives WinDirStat's default
/// treemap. <c>Logical Size</c> carries the file's plain size as FLP recorded it, matching what
/// that column means in every ordinary WinDirStat scan, so the "use logical size" option flips the
/// same tree between an index-cost view and a familiar disk-usage view. A row whose index cost
/// rivals its file size is content that is expensive to index — every token novel.
/// </summary>
public static class WinDirStatWriter
{
    /// <summary>Synthetic leaf holding a folder's own index documents, so sums stay exact.</summary>
    public const string FolderEntryLeaf = "<folder entry>";

    private sealed class PathNode(string segment, int sequence)
    {
        public string Segment { get; } = segment;

        public int Sequence { get; } = sequence;

        public Dictionary<string, PathNode>? Children;
        public List<PathNode>? ChildOrder;
        public List<Leaf>? Leaves;

        /// <summary>Bytes emitted as a <c>&lt;folder entry&gt;</c> leaf (the folder's own documents;
        /// FileSize is non-zero only for containers, whose file lives at the folder's own path).</summary>
        public long EntryFileSize, EntryPhysical;

        /// <summary>
        /// Bytes belonging to the folder itself with no leaf of their own — a childless indexed
        /// directory. Folded into the declared size while childless; promoted to the folder-entry
        /// leaf if children ever appear, so sums stay exact either way.
        /// </summary>
        public long SelfFileSize, SelfPhysical;

        public DateTime? LastChange;
        public string Attributes = string.Empty;

        /// <summary>True when created from an empty-directory row rather than a folder document.</summary>
        public bool FromEmptyDir;

        /// <summary>True when no document declared this folder — an item's path implied it.</summary>
        public bool Implicit;

        // Computed bottom-up before emission.
        public long SubFileSize, SubPhysical;
        public int FileCount, FolderCount;

        public bool HasContents => (ChildOrder is { Count: > 0 }) || (Leaves is { Count: > 0 }) || EntryPhysical > 0 || EntryFileSize > 0;

        public PathNode GetOrAddChild(string segment, ref int nextSequence, out bool created)
        {
            Children ??= new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);
            ChildOrder ??= [];

            if (Children.TryGetValue(segment, out PathNode? child))
            {
                created = false;
                return child;
            }

            created = true;
            child = new PathNode(segment, nextSequence++);
            Children[segment] = child;
            ChildOrder.Add(child);
            return child;
        }
    }

    private readonly record struct Leaf(
        string Name, long FileSize, long Physical, string Attributes, DateTime? LastChange, int Sequence);

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

        int nextSequence = 0;
        int implicitFolders = 0;
        var sentinel = new PathNode(string.Empty, nextSequence++);
        DateTime? newest = null;

        BuildTrie(tree, sentinel, ref nextSequence, ref newest);
        FoldLeafFolderCollisions(sentinel);
        ComputeSums(sentinel, ref implicitFolders);

        long rootFileSize = 0, rootPhysical = 0;
        int rootFiles = 0, rootFolders = 0;
        foreach (PathNode root in sentinel.ChildOrder ?? [])
        {
            rootFileSize += root.SubFileSize;
            rootPhysical += root.SubPhysical;
            rootFiles += root.FileCount;
            rootFolders += root.FolderCount + 1;
        }

        int unsafeValues = 0, fileRows = 0, folderRows = 0, syntheticRows = 0;

        // No BOM: every WinDirStat 2.x handles its absence, but only 2.7+ skips one if present.
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        using var csv = new CsvWriter(stream, ',', writeBom: false);
        using IProgressScope scope = (progress ?? NullProgress.Instance)
            .Begin("writing rows", rootFiles + rootFolders + 1);

        csv.WriteRow(WinDirStatFormat.RequiredColumns);

        var rowBuffer = new string?[WinDirStatFormat.RequiredColumns.Length];

        WriteRow(csv, rowBuffer, rootLabel, WinDirStatFormat.RootTypeText, rootFiles, rootFolders,
            rootFileSize, rootPhysical, attributes: string.Empty, lastChange: newest, ref unsafeValues);

        foreach (PathNode driveNode in Ordered(sentinel.ChildOrder))
        {
            // A drive-letter root gets the trailing backslash WinDirStat itself writes; children
            // then attach through the two-character alias ("C:") the loader registers. A UNC root
            // must stay exactly as-is: it has no usable alias (the loader's substr(0,2) is just
            // "\\"), so its children can only attach through the verbatim full name.
            bool isDriveLetter = driveNode.Segment.Length == 2 && driveNode.Segment[1] == ':';
            string drivePath = isDriveLetter ? driveNode.Segment + '\\' : driveNode.Segment;

            WriteRow(csv, rowBuffer, drivePath, WinDirStatFormat.DriveTypeText,
                driveNode.FileCount, driveNode.FolderCount,
                driveNode.SubFileSize, driveNode.SubPhysical,
                attributes: string.Empty, lastChange: driveNode.LastChange ?? newest, ref unsafeValues);
            folderRows++;

            EmitTree(csv, rowBuffer, driveNode,
                ref fileRows, ref folderRows, ref syntheticRows, ref unsafeValues, scope);
        }

        return new WinDirStatWriteResult
        {
            RootLogicalBytes = rootFileSize,
            RootPhysicalBytes = rootPhysical,
            FileRows = fileRows,
            FolderRows = folderRows,
            SyntheticRows = syntheticRows,
            UnsafeValues = unsafeValues,
            ImplicitFolders = implicitFolders,
            OmittedBytes = omittedBytes,
            OmittedReasons = omitted,
        };
    }

    // ---- trie construction ---------------------------------------------------------------------

    private static void BuildTrie(CostTree tree, PathNode sentinel,
        ref int nextSequence, ref DateTime? newest)
    {
        // Folder-path → trie node, so each CostNode's leaves descend from a cached position
        // instead of re-splitting the full path per row.
        var byPath = new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);

        foreach (CostNode node in tree.PreOrder())
        {
            PathNode folder = GetFolder(sentinel, byPath, node.Path, ref nextSequence, declared: true);

            folder.EntryFileSize += node.OwnFileSizeBytes;
            folder.EntryPhysical += node.OwnApportionedBytes;
            folder.LastChange ??= node.LastChange;
            if (node.LastChange is { } change && (newest is null || change > newest))
                newest = change;

            foreach (CostRow file in node.Files)
                InsertLeaf(sentinel, byPath, folder, node.Path, file, ref nextSequence);

            foreach (CostRow empty in node.EmptyFolders)
            {
                PathNode emptyNode = GetFolder(sentinel, byPath, empty.Owner, ref nextSequence, declared: true);
                emptyNode.SelfFileSize += empty.FileSizeBytes;
                emptyNode.SelfPhysical += empty.ApportionedBytes;
                emptyNode.LastChange ??= empty.LastChange;
                if (emptyNode.Attributes.Length == 0)
                    emptyNode.Attributes = AttributesOf(empty);
                emptyNode.FromEmptyDir = true;
            }
        }

        // Anything the tree could not place still deserves a spot if its path parses.
        foreach (CostRow orphan in tree.Orphans)
        {
            if (orphan.Owner.StartsWith('<') || orphan.Owner.LastIndexOf('\\') <= 0)
                continue;

            int cut = orphan.Owner.LastIndexOf('\\');
            PathNode folder = GetFolder(sentinel, byPath, orphan.Owner[..cut], ref nextSequence, declared: false);
            AddLeaf(folder, orphan.Owner[(cut + 1)..], orphan, ref nextSequence);
        }
    }

    /// <summary>
    /// Finds or creates the trie folder for a full path. The first segment is the root: a drive
    /// letter, a UNC <c>\\server\share</c> pair, or whatever unresolved marker the path starts
    /// with.
    /// </summary>
    private static PathNode GetFolder(PathNode sentinel, Dictionary<string, PathNode> byPath,
        string path, ref int nextSequence, bool declared)
    {
        if (byPath.TryGetValue(path, out PathNode? cached))
        {
            if (declared)
                cached.Implicit = false;
            return cached;
        }

        PathNode current = sentinel;
        foreach (string segment in SplitPath(path))
        {
            current = current.GetOrAddChild(Sanitize(segment), ref nextSequence, out bool created);
            if (created)
                current.Implicit = true;
        }

        if (declared)
            current.Implicit = false;

        byPath[path] = current;
        return current;
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        int start = 0;

        // A UNC root is one unit: \\server\share.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int firstSep = path.IndexOf('\\', 2);
            int secondSep = firstSep < 0 ? -1 : path.IndexOf('\\', firstSep + 1);
            int end = secondSep < 0 ? path.Length : secondSep;
            yield return path[..end];
            start = end + 1;
        }

        while (start <= path.Length - 1)
        {
            int separator = path.IndexOf('\\', start);
            int end = separator < 0 ? path.Length : separator;
            if (end > start)
                yield return path[start..end];
            start = end + 1;
        }
    }

    /// <summary>
    /// Inserts a file row beneath its folder. A name that itself contains separators — an
    /// attachment inside an email, stored as <c>message.msg\image001.png</c> — creates the
    /// intermediate folders no document declared. A row whose path IS the folder's path is the
    /// container file itself; its bytes fold into the folder's entry leaf.
    /// </summary>
    private static void InsertLeaf(PathNode sentinel, Dictionary<string, PathNode> byPath,
        PathNode folder, string folderPath, CostRow row, ref int nextSequence)
    {
        string owner = row.Owner;
        string relative;

        if (owner.Length > folderPath.Length + 1
            && owner.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)
            && owner[folderPath.Length] == '\\')
        {
            relative = owner[(folderPath.Length + 1)..];
        }
        else if (string.Equals(owner, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            folder.EntryFileSize += row.FileSizeBytes;
            folder.EntryPhysical += row.ApportionedBytes;
            folder.LastChange ??= row.LastChange;
            return;
        }
        else
        {
            // The row's own path disagrees with its folder's - resolve it from the root instead.
            int cut = owner.LastIndexOf('\\');
            if (cut <= 0)
                return;
            folder = GetFolder(sentinel, byPath, owner[..cut], ref nextSequence, declared: false);
            relative = owner[(cut + 1)..];
        }

        int separator = relative.IndexOf('\\');
        while (separator >= 0)
        {
            folder = folder.GetOrAddChild(Sanitize(relative[..separator]), ref nextSequence, out bool created);
            if (created)
                folder.Implicit = true;

            relative = relative[(separator + 1)..];
            separator = relative.IndexOf('\\');
        }

        if (relative.Length == 0)
            return;

        AddLeaf(folder, relative, row, ref nextSequence);
    }

    private static void AddLeaf(PathNode folder, string name, CostRow row, ref int nextSequence)
    {
        folder.Leaves ??= [];
        folder.Leaves.Add(new Leaf(
            Sanitize(name), row.FileSizeBytes, row.ApportionedBytes,
            AttributesOf(row), row.LastChange, nextSequence++));
    }

    /// <summary>
    /// A leaf and a folder can end up sharing a name — the container file and the folder its
    /// interior implied. WinDirStat would attach the leaf as the folder's sibling twin; folding the
    /// leaf's bytes into the folder's entry leaf keeps one row per path and the sums nested where
    /// they belong.
    /// </summary>
    private static void FoldLeafFolderCollisions(PathNode node)
    {
        var stack = new Stack<PathNode>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            PathNode current = stack.Pop();

            if (current.Leaves is not null && current.Children is not null)
            {
                for (int i = current.Leaves.Count - 1; i >= 0; i--)
                {
                    Leaf leaf = current.Leaves[i];
                    if (!current.Children.TryGetValue(leaf.Name, out PathNode? twin))
                        continue;

                    twin.EntryFileSize += leaf.FileSize;
                    twin.EntryPhysical += leaf.Physical;
                    twin.LastChange ??= leaf.LastChange;
                    current.Leaves.RemoveAt(i);
                }
            }

            if (current.ChildOrder is not null)
            {
                foreach (PathNode child in current.ChildOrder)
                    stack.Push(child);
            }
        }
    }

    private static void ComputeSums(PathNode sentinel, ref int implicitFolders)
    {
        // Post-order without recursion: children first.
        var order = new List<PathNode>();
        var stack = new Stack<PathNode>();
        stack.Push(sentinel);
        while (stack.Count > 0)
        {
            PathNode node = stack.Pop();
            order.Add(node);
            if (node.ChildOrder is not null)
            {
                foreach (PathNode child in node.ChildOrder)
                    stack.Push(child);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            PathNode node = order[i];
            if (node.Implicit)
                implicitFolders++;

            // A childless folder's own bytes stay in its declared size; once it has any contents
            // they must move to the entry leaf or the children would no longer sum to the parent.
            if (node.SelfFileSize != 0 || node.SelfPhysical != 0)
            {
                bool hasContents = (node.ChildOrder is { Count: > 0 }) || (node.Leaves is { Count: > 0 })
                    || node.EntryPhysical > 0 || node.EntryFileSize > 0;
                if (hasContents)
                {
                    node.EntryFileSize += node.SelfFileSize;
                    node.EntryPhysical += node.SelfPhysical;
                    node.SelfFileSize = 0;
                    node.SelfPhysical = 0;
                }
            }

            long fileSize = node.EntryFileSize + node.SelfFileSize;
            long physical = node.EntryPhysical + node.SelfPhysical;
            int files = node.EntryPhysical > 0 || node.EntryFileSize > 0 ? 1 : 0;
            int folders = 0;

            if (node.Leaves is not null)
            {
                foreach (Leaf leaf in node.Leaves)
                {
                    fileSize += leaf.FileSize;
                    physical += leaf.Physical;
                    files++;
                }
            }

            if (node.ChildOrder is not null)
            {
                foreach (PathNode child in node.ChildOrder)
                {
                    fileSize += child.SubFileSize;
                    physical += child.SubPhysical;
                    files += child.FileCount;
                    folders += child.FolderCount + 1;
                }
            }

            node.SubFileSize = fileSize;
            node.SubPhysical = physical;
            node.FileCount = files;
            node.FolderCount = folders;
        }
    }

    // ---- emission ------------------------------------------------------------------------------

    /// <summary>
    /// Pre-order emission below one drive, iterative so container nesting depth cannot overflow the
    /// stack. Each stack entry is either a folder whose row (and then contents) is due, or a
    /// contents-only marker: the drive's own leaves are emitted <em>after</em> all its subtrees,
    /// which is the order this writer has always produced — kept so container-free indexes remain
    /// byte-identical across versions.
    /// </summary>
    private static void EmitTree(CsvWriter csv, string?[] rowBuffer, PathNode driveNode,
        ref int fileRows, ref int folderRows, ref int syntheticRows, ref int unsafeValues, IProgressScope scope)
    {
        var stack = new Stack<(PathNode Node, string Path, bool ContentsOnly)>();
        stack.Push((driveNode, driveNode.Segment, ContentsOnly: true));
        PushBlockChildren(stack, driveNode, driveNode.Segment);

        while (stack.Count > 0)
        {
            (PathNode node, string path, bool contentsOnly) = stack.Pop();

            if (!contentsOnly)
            {
                WriteRow(csv, rowBuffer, path, WinDirStatFormat.DirectoryTypeText,
                    node.FileCount, node.FolderCount, node.SubFileSize, node.SubPhysical,
                    node.Attributes, node.LastChange, ref unsafeValues);
                folderRows++;
            }

            // The folder's own documents, as a leaf, so its size equals the sum of its children.
            if (node.EntryPhysical > 0 || node.EntryFileSize > 0)
            {
                WriteRow(csv, rowBuffer, path + '\\' + FolderEntryLeaf, WinDirStatFormat.FileTypeText, 0, 0,
                    node.EntryFileSize, node.EntryPhysical,
                    attributes: string.Empty, lastChange: node.LastChange, ref unsafeValues);
                syntheticRows++;
            }

            if (node.Leaves is not null)
            {
                foreach (Leaf leaf in node.Leaves.OrderByDescending(l => l.Physical).ThenBy(l => l.Sequence))
                {
                    WriteRow(csv, rowBuffer, path + '\\' + leaf.Name, WinDirStatFormat.FileTypeText, 0, 0,
                        leaf.FileSize, leaf.Physical, leaf.Attributes, leaf.LastChange, ref unsafeValues);
                    fileRows++;
                }
            }

            if (node.ChildOrder is not null)
            {
                // Childless indexed directories are inlined after the files; folders with contents
                // get their own block. Stable ordering keeps ties in discovery order.
                foreach (PathNode child in node.ChildOrder
                             .Where(c => c.FromEmptyDir && !c.HasContents)
                             .OrderByDescending(c => c.SubPhysical).ThenBy(c => c.Sequence))
                {
                    WriteRow(csv, rowBuffer, path + '\\' + child.Segment, WinDirStatFormat.DirectoryTypeText, 0, 0,
                        child.SubFileSize, child.SubPhysical, child.Attributes, child.LastChange, ref unsafeValues);
                    folderRows++;
                }

                if (!contentsOnly)
                    PushBlockChildren(stack, node, path);
            }

            scope.Report(fileRows + folderRows + syntheticRows);
        }
    }

    /// <summary>Pushes block children in reverse size order, so they pop largest-first.</summary>
    private static void PushBlockChildren(Stack<(PathNode, string, bool)> stack, PathNode node, string path)
    {
        if (node.ChildOrder is null)
            return;

        var blocks = node.ChildOrder
            .Where(c => !(c.FromEmptyDir && !c.HasContents))
            .OrderByDescending(c => c.SubPhysical).ThenBy(c => c.Sequence)
            .ToList();

        for (int i = blocks.Count - 1; i >= 0; i--)
            stack.Push((blocks[i], path + '\\' + blocks[i].Segment, false));
    }

    private static IEnumerable<PathNode> Ordered(List<PathNode>? nodes) =>
        nodes is null ? [] : nodes.OrderByDescending(n => n.SubPhysical).ThenBy(n => n.Sequence);

    private static string AttributesOf(CostRow row)
    {
        long mask = row.RawAttributes is null
            ? 0
            : FieldDecoders.TryParseNumber(row.RawAttributes, FlpEncoding.HexAttributes) ?? 0;
        return WinDirStatFormat.FormatAttributes(mask);
    }

    /// <summary>
    /// Names inside containers are not filesystem names — an email subject can legally hold the two
    /// things WinDirStat's parser cannot survive: a double quote ends the field early and corrupts
    /// every later column, and an embedded newline splits the row and aborts the whole load. Both
    /// are replaced, segment by segment, so parents and children stay consistent by construction.
    /// </summary>
    private static string Sanitize(string name)
    {
        if (name.AsSpan().IndexOfAny('"', '\r', '\n') < 0)
            return name;

        return name.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void WriteRow(CsvWriter csv, string?[] row, string name, string itemTypeText,
        int files, int folders, long logical, long physical,
        string attributes, DateTime? lastChange, ref int unsafeValues)
    {
        name = Sanitize(name);
        if (!WinDirStatFormat.IsSafeValue(name))
            unsafeValues++;

        row[0] = name;
        row[1] = files.ToString(CultureInfo.InvariantCulture);
        row[2] = folders.ToString(CultureInfo.InvariantCulture);
        row[3] = logical.ToString(CultureInfo.InvariantCulture);
        row[4] = physical.ToString(CultureInfo.InvariantCulture);
        row[5] = attributes;
        row[6] = WinDirStatFormat.FormatTimestamp(lastChange);
        row[7] = itemTypeText;
        row[8] = WinDirStatFormat.ZeroIndexText;
        csv.WriteRow(row.AsSpan());
    }
}
