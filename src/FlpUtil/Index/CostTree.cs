namespace FlpUtil.Index;

/// <summary>One folder in the cost hierarchy, with its subtree totals rolled up.</summary>
public sealed class CostNode
{
    public required string FolderId { get; init; }
    public required string Path { get; init; }
    public CostNode? Parent { get; set; }
    public List<CostNode> Children { get; } = [];
    public List<CostRow> Files { get; } = [];

    /// <summary>
    /// Indexed directories that have no folder-tree node of their own, because FLP only creates one
    /// for a folder that needs to hold children. They are leaves here — real directories with a cost
    /// but nothing beneath them.
    /// </summary>
    public List<CostRow> EmptyFolders { get; } = [];

    /// <summary>Depth below the tree root, so console output can be indented and pruned.</summary>
    public int Depth { get; set; }

    /// <summary>
    /// Bytes for this folder's own index documents — its folder-tree entry plus the item and meta
    /// documents FLP writes for a directory. Held separately from the subtree total so it can be
    /// shown, or emitted as its own row, rather than vanishing into the aggregate.
    /// </summary>
    public long OwnExclusiveBytes { get; set; }

    public long OwnApportionedBytes { get; set; }

    /// <summary>The folder's own last-modified time, as recorded in the index.</summary>
    public DateTime? LastChange { get; set; }

    /// <summary>Exclusive bytes for this folder, its files and everything beneath it.</summary>
    public long SubtreeExclusiveBytes { get; set; }

    /// <summary>As above, but including each item's apportioned share of the joint dictionary.</summary>
    public long SubtreeApportionedBytes { get; set; }

    /// <summary>Files anywhere beneath this folder — WinDirStat needs the recursive count.</summary>
    public int SubtreeFileCount { get; set; }

    public int SubtreeFolderCount { get; set; }

    /// <summary>Bytes from files directly in this folder, excluding subfolders.</summary>
    public long DirectFileBytes => Files.Sum(f => f.ExclusiveBytes);
}

/// <summary>
/// Rolls per-file index cost up the folder hierarchy.
///
/// <see cref="IndexCostAnalyzer"/> produces one flat row per file or folder; this assembles them into
/// the tree FLP's folder documents describe and accumulates subtree totals, which is what makes
/// "should I stop indexing this folder?" answerable. Nothing here is estimated — a folder's total is
/// the sum of the byte-exact figures beneath it.
/// </summary>
public sealed class CostTree
{
    private readonly Dictionary<string, CostNode> _nodes = new(StringComparer.Ordinal);

    private CostTree()
    {
    }

    /// <summary>Folders with no parent in the index — one per indexed drive or share.</summary>
    public List<CostNode> Roots { get; } = [];

    /// <summary>Rows that could not be placed under any folder, so they are never silently lost.</summary>
    public List<CostRow> Orphans { get; } = [];

    public IReadOnlyCollection<CostNode> Nodes => _nodes.Values;

    public CostNode? Find(string folderId) => _nodes.GetValueOrDefault(folderId);

    public static CostTree Build(IndexCostReport report)
    {
        var tree = new CostTree();
        FolderTree folders = report.Folders;

        // A node per folder the index knows about, named by its resolved full path.
        foreach (string folderId in folders.FolderIds)
        {
            tree._nodes[folderId] = new CostNode
            {
                FolderId = folderId,
                Path = folders.ResolveFolderPath(folderId),
            };
        }

        foreach (CostNode node in tree._nodes.Values)
        {
            string? parentId = folders.ParentOf(node.FolderId);
            if (parentId is not null && tree._nodes.TryGetValue(parentId, out CostNode? parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else
            {
                tree.Roots.Add(node);
            }
        }

        // Attach cost rows. A folder row contributes that folder's own bytes; a file row becomes a
        // leaf of its parent folder.
        foreach (CostRow row in report.Rows)
        {
            if (row.Owner.StartsWith('<'))
                continue; // synthetic rows: index metadata, deleted documents

            if (row.IsFolder && row.OwnFolderId.Length > 0
                && tree._nodes.TryGetValue(row.OwnFolderId, out CostNode? own))
            {
                own.OwnExclusiveBytes += row.ExclusiveBytes;
                own.OwnApportionedBytes += row.ApportionedBytes;
                own.LastChange ??= row.LastChange;
                continue;
            }

            if (row.ParentFolderId.Length > 0
                && tree._nodes.TryGetValue(row.ParentFolderId, out CostNode? parent))
            {
                // A directory with no folder-tree node of its own holds nothing, so it belongs here
                // as a leaf rather than as a node with no children.
                if (row.IsFolder)
                    parent.EmptyFolders.Add(row);
                else
                    parent.Files.Add(row);
                continue;
            }

            tree.Orphans.Add(row);
        }

        foreach (CostNode root in tree.Roots)
            Accumulate(root, depth: 0);

        tree.Roots.Sort((a, b) => b.SubtreeApportionedBytes.CompareTo(a.SubtreeApportionedBytes));
        return tree;
    }

    /// <summary>
    /// Post-order walk: children first, then fold them plus this folder's own files and entry cost
    /// into the subtree totals. Iterative rather than recursive so a pathologically deep tree cannot
    /// overflow the stack.
    /// </summary>
    private static void Accumulate(CostNode root, int depth)
    {
        var order = new List<CostNode>();
        var stack = new Stack<CostNode>();
        stack.Push(root);
        root.Depth = depth;

        while (stack.Count > 0)
        {
            CostNode node = stack.Pop();
            order.Add(node);
            foreach (CostNode child in node.Children)
            {
                child.Depth = node.Depth + 1;
                stack.Push(child);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            CostNode node = order[i];

            long exclusive = node.OwnExclusiveBytes;
            long apportioned = node.OwnApportionedBytes;
            int files = 0;
            int subfolders = 0;

            foreach (CostRow file in node.Files)
            {
                exclusive += file.ExclusiveBytes;
                apportioned += file.ApportionedBytes;
                files++;
            }

            foreach (CostRow empty in node.EmptyFolders)
            {
                exclusive += empty.ExclusiveBytes;
                apportioned += empty.ApportionedBytes;
                subfolders++;
            }

            foreach (CostNode child in node.Children)
            {
                exclusive += child.SubtreeExclusiveBytes;
                apportioned += child.SubtreeApportionedBytes;
                files += child.SubtreeFileCount;
                subfolders += child.SubtreeFolderCount + 1;
            }

            node.SubtreeExclusiveBytes = exclusive;
            node.SubtreeApportionedBytes = apportioned;
            node.SubtreeFileCount = files;
            node.SubtreeFolderCount = subfolders;

            node.Children.Sort((a, b) => b.SubtreeApportionedBytes.CompareTo(a.SubtreeApportionedBytes));
            node.Files.Sort((a, b) => b.ApportionedBytes.CompareTo(a.ApportionedBytes));
            node.EmptyFolders.Sort((a, b) => b.ApportionedBytes.CompareTo(a.ApportionedBytes));
        }
    }

    /// <summary>Every folder, largest subtree first.</summary>
    public IEnumerable<CostNode> ByCostDescending() =>
        _nodes.Values.OrderByDescending(n => n.SubtreeApportionedBytes);

    /// <summary>Pre-order walk from each root — the order WinDirStat requires (parents first).</summary>
    public IEnumerable<CostNode> PreOrder()
    {
        var stack = new Stack<CostNode>();
        for (int i = Roots.Count - 1; i >= 0; i--)
            stack.Push(Roots[i]);

        while (stack.Count > 0)
        {
            CostNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }
}
