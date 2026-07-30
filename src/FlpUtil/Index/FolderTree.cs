namespace FlpUtil.Index;

/// <summary>
/// Rebuilds full paths from FLP's normalised folder storage.
///
/// FLP stores no path on an item. Each item's id is <c>{fldrid}:{name}</c>, and folders live in
/// their own documents keyed by <c>fldrid</c> with <c>fldrpid</c> pointing at their parent. A root
/// folder holds the drive or UNC prefix in its name (as the Win32 long-path form
/// <c>\\?\C:</c>) and has <c>fldrpid</c> = "root", so walking the chain upwards reconstructs the
/// original path.
/// </summary>
public sealed class FolderTree
{
    private readonly Dictionary<string, Node> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);

    private sealed record Node(string Name, string? ParentId);

    public int Count => _folders.Count;

    /// <summary>Every folder id in the index.</summary>
    public IEnumerable<string> FolderIds => _folders.Keys;

    /// <summary>Parent folder id, or null when this is a root (its <c>fldrpid</c> was "root").</summary>
    public string? ParentOf(string folderId) =>
        _folders.TryGetValue(folderId, out Node? node) ? node.ParentId : null;

    /// <summary>Bare folder name as stored, e.g. <c>Desktop</c> or the root's <c>\\?\C:</c>.</summary>
    public string NameOf(string folderId) =>
        _folders.TryGetValue(folderId, out Node? node) ? node.Name : string.Empty;

    public bool Contains(string folderId) => _folders.ContainsKey(folderId);

    public void Add(IndexDoc doc)
    {
        string? id = doc.Get(FlpSchema.FolderId);
        if (string.IsNullOrEmpty(id))
            return;

        string? parentId = doc.Get(FlpSchema.FolderParentId);
        _folders[id] = new Node(
            Name: doc.Get(FlpSchema.FolderName) ?? string.Empty,
            ParentId: string.IsNullOrEmpty(parentId) || parentId == FlpSchema.RootParentId ? null : parentId);
    }

    /// <summary>Full path of a folder, e.g. <c>C:\Users\Smith\Desktop</c>. Empty when unknown.</summary>
    public string ResolveFolderPath(string? folderId)
    {
        if (string.IsNullOrEmpty(folderId))
            return string.Empty;

        if (_pathCache.TryGetValue(folderId, out string? cached))
            return cached;

        var segments = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = folderId;
        string problem = string.Empty;

        while (current is not null)
        {
            if (!visited.Add(current))
            {
                // Only reachable from a corrupt index, but silently emitting a truncated path
                // would be worse than saying so.
                problem = "<cycle>";
                break;
            }

            if (!_folders.TryGetValue(current, out Node? node))
            {
                problem = $"<unresolved:{current}>";
                break;
            }

            if (node.Name.Length > 0)
                segments.Add(node.Name);
            current = node.ParentId;
        }

        segments.Reverse();
        string path = Join(segments);
        if (problem.Length > 0)
            path = path.Length > 0 ? $"{problem}\\{path}" : problem;

        _pathCache[folderId] = path;
        return path;
    }

    public string ResolveItemPath(string folderId, string name)
    {
        string folder = ResolveFolderPath(folderId);
        if (folder.Length == 0)
            return name;
        if (name.Length == 0)
            return folder;
        return folder.EndsWith('\\') ? folder + name : $"{folder}\\{name}";
    }

    private static string Join(List<string> segments)
    {
        if (segments.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(NormalizeRoot(segments[0]));
        for (int i = 1; i < segments.Count; i++)
        {
            if (builder.Length > 0 && builder[^1] != '\\')
                builder.Append('\\');
            builder.Append(segments[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// FLP stores roots in Win32 long-path form (<c>\\?\C:</c>, <c>\\?\UNC\server\share</c>).
    /// Strip the prefix so the CSV holds paths a user can paste into Explorer; the untouched value
    /// is still available in the raw <c>fldrnm</c> column.
    /// </summary>
    private static string NormalizeRoot(string root)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";

        if (root.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + root[uncPrefix.Length..];

        if (root.StartsWith(devicePrefix, StringComparison.Ordinal))
            return root[devicePrefix.Length..];

        return root;
    }
}
