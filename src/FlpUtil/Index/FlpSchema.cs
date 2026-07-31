namespace FlpUtil.Index;

/// <summary>
/// The layout of a FileLocator Pro index (store version 9), as observed in a real index.
///
/// FLP writes three kinds of document, all in the same Lucene index:
///
///   * <b>folder</b>   — <c>fldrid</c>, <c>fldrpid</c>, <c>fldrnm</c>, <c>fldrkey</c>.
///                       These form the directory tree; a root has <c>fldrpid</c> = "root" and a
///                       name like <c>\\?\C:</c>.
///   * <b>item</b>     — <c>id</c>, <c>name</c>, <c>sizenr</c>, <c>modft</c>, <c>createft</c>,
///                       <c>attrx</c>, <c>itemtype</c>, <c>exinfo</c>. One per indexed file *and*
///                       per indexed directory. No path: <c>id</c> is <c>{fldrid}:{name}</c>, so
///                       the parent folder id is embedded in the id.
///   * <b>meta</b>     — <c>mid</c>, <c>idxdt</c>, <c>idxfl</c>, <c>idxtrm</c>, <c>moddt</c>.
///                       Indexing status for an item, joined on <c>mid</c> == <c>id</c>.
///
/// Plus exactly one index-level document carrying <c>idxv</c>, <c>idxprms</c>, <c>idxdtstr</c>
/// and <c>ncid</c>.
/// </summary>
public static class FlpSchema
{
    // ---- folder documents -------------------------------------------------
    public const string FolderId = "fldrid";
    public const string FolderParentId = "fldrpid";
    public const string FolderName = "fldrnm";
    public const string RootParentId = "root";

    // ---- item documents ---------------------------------------------------
    public const string ItemId = "id";
    public const string ItemName = "name";
    public const string Size = "sizenr";
    public const string Modified = "modft";
    public const string Created = "createft";
    public const string Attributes = "attrx";
    public const string ItemType = "itemtype";

    // ---- meta documents ---------------------------------------------------
    public const string MetaId = "mid";
    public const string IndexedDate = "idxdt";
    public const string IndexFlags = "idxfl";
    public const string TermCount = "idxtrm";

    // ---- index-level document --------------------------------------------
    public const string IndexVersion = "idxv";

    /// <summary>
    /// <c>itemtype</c> value seen on directory items. Files are 1; files <em>inside</em> a
    /// container (archive members, email attachments) are 17.
    ///
    /// Containers themselves appear twice: as a plain file item, and as a folder node whose
    /// <c>fldrkey</c> ends in <c>*2*</c>, holding an empty-named child keyed <c>*4*</c> (the
    /// interior root) whose descendants are keyed <c>*8*</c>. Filesystem directories are
    /// keyed <c>*1*</c>.
    /// </summary>
    public const string ItemTypeFolder = "4";

    /// <summary>
    /// Encoding per field. Verified against the filesystem for size/attributes/timestamps; the
    /// hex fields are unprefixed, e.g. <c>attrx</c>=<c>20</c> is Archive, not decimal 20.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, FlpEncoding> Encodings =
        new Dictionary<string, FlpEncoding>(StringComparer.Ordinal)
        {
            [Size] = FlpEncoding.DecimalNumber,
            [Modified] = FlpEncoding.DecimalFileTime,
            [Created] = FlpEncoding.DecimalFileTime,
            [IndexedDate] = FlpEncoding.DecimalFileTime,
            ["moddt"] = FlpEncoding.HexFileTime,
            ["idxdtstr"] = FlpEncoding.HexFileTime,
            [Attributes] = FlpEncoding.HexAttributes,
            [IndexFlags] = FlpEncoding.HexFlags,
            [TermCount] = FlpEncoding.HexNumber,
            ["exinfo"] = FlpEncoding.HexNumber,
            [ItemType] = FlpEncoding.DecimalNumber,
        };

    public static FlpEncoding EncodingOf(string field) =>
        Encodings.TryGetValue(field, out var encoding) ? encoding : FlpEncoding.Text;

    public static bool IsFolderDoc(IndexDoc doc) => doc.Has(FolderId);

    public static bool IsItemDoc(IndexDoc doc) => doc.Has(ItemId);

    public static bool IsMetaDoc(IndexDoc doc) => doc.Has(MetaId);

    public static bool IsIndexDoc(IndexDoc doc) => doc.Has(IndexVersion);

    /// <summary>
    /// Splits an item id (<c>{fldrid}:{name}</c>) into its parts. Windows file names cannot contain
    /// ':', so the first colon is always the separator.
    /// </summary>
    public static (string FolderId, string Name) SplitItemId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return (string.Empty, string.Empty);

        int colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (string.Empty, id) : (id[..colon], id[(colon + 1)..]);
    }
}
