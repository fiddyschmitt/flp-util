using System.Globalization;

namespace FlpUtil.Export;

/// <summary>
/// The WinDirStat 2.x saved-results format, as read out of the WinDirStat source
/// (<c>windirstat/CsvLoader.cpp</c>, <c>Item.h</c>, <c>Constants.h</c>, <c>res/langs/lang_en.txt</c>
/// at tag <c>release/v2.7.0</c>). The load path there is byte-identical to <c>master</c>, so this
/// matches both.
///
/// Load it with <c>WinDirStat.exe /loadfrom &lt;file&gt;</c>. Note that WinDirStat gives no feedback
/// when a file is rejected — <c>LoadResults</c> returns null and the window simply opens empty — which
/// is why <see cref="WinDirStatValidator"/> exists.
/// </summary>
public static class WinDirStatFormat
{
    // ---- header ------------------------------------------------------------
    // Matched by name, so column order is free; all of these are required (Owner is the only
    // optional column and we do not emit it).
    public const string ColumnName = "Name";
    public const string ColumnFiles = "Files";
    public const string ColumnFolders = "Folders";
    public const string ColumnLogicalSize = "Logical Size";
    public const string ColumnPhysicalSize = "Physical Size";
    public const string ColumnAttributes = "Attributes";
    public const string ColumnLastChange = "Last Change";

    /// <summary>Built by WinDirStat as app title + " " + "Attributes".</summary>
    public const string ColumnItemType = "WinDirStat Attributes";

    public const string ColumnIndex = "Index";

    public static readonly string[] RequiredColumns =
    [
        ColumnName, ColumnFiles, ColumnFolders, ColumnLogicalSize, ColumnPhysicalSize,
        ColumnAttributes, ColumnLastChange, ColumnItemType, ColumnIndex,
    ];

    // ---- ITEMTYPE bits (windirstat/Item.h) ---------------------------------
    public const uint ItMyComputer = 1u << 0;
    public const uint ItDrive = 1u << 1;
    public const uint ItDirectory = 1u << 2;
    public const uint ItFile = 1u << 3;
    public const uint ItfRootItem = 1u << 28;

    /// <summary>Mask isolating the item kind from the hash and flag bits.</summary>
    public static uint ItMask() => 0x0000FFFF;

    /// <summary>
    /// Pseudo container at the top of the tree; its name is shown verbatim, not as a path.
    ///
    /// IMPORTANT: an <c>IT_MYCOMPUTER</c> root may contain <b>only drives</b>. Hanging a file or a
    /// directory directly off it makes WinDirStat 2.7.0 die with an access violation (0xC0000005)
    /// during load — verified by bisecting hand-built files. Its own loader accepts the rows; the
    /// crash happens afterwards, presumably because nothing downstream expects a leaf whose ancestor
    /// chain contains no drive. <see cref="WinDirStatValidator"/> enforces this.
    /// </summary>
    public const uint TypeRoot = ItMyComputer | ItfRootItem;

    /// <summary>Attaches straight to the root, and registers both <c>C:\</c> and <c>C:</c> as parent keys.</summary>
    public const uint TypeDrive = ItDrive;

    public const uint TypeDirectory = ItDirectory;
    public const uint TypeFile = ItFile;

    /// <summary>Formats an item type the way the loader parses it (<c>wcstoull(..., 16)</c>).</summary>
    public static string FormatItemType(uint type) =>
        "0x" + type.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Formats the NTFS file index column. We have no file ids, so this is always zero.</summary>
    public static string FormatIndex(ulong index) =>
        "0x" + index.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>The exact shape <c>FromTimeString</c> expects; anything else decodes to a zero time.</summary>
    public static string FormatTimestamp(DateTime? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Renders a Windows attribute mask the way WinDirStat's <c>FormatAttributes</c> does — and only
    /// the flags its <c>ParseAttributes</c> reads back, so nothing round-trips to a different value.
    /// Reparse points are deliberately absent: WinDirStat does not emit <c>@</c> here either.
    /// </summary>
    public static string FormatAttributes(long mask)
    {
        if (mask <= 0)
            return string.Empty;

        var attributes = (FileAttributes)mask;
        var text = new System.Text.StringBuilder(8);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) text.Append('R');
        if (attributes.HasFlag(FileAttributes.Hidden)) text.Append('H');
        if (attributes.HasFlag(FileAttributes.System)) text.Append('S');
        if (attributes.HasFlag(FileAttributes.Archive)) text.Append('A');
        if (attributes.HasFlag(FileAttributes.Compressed)) text.Append('C');
        if (attributes.HasFlag(FileAttributes.Encrypted)) text.Append('E');
        if (attributes.HasFlag(FileAttributes.Offline)) text.Append('O');
        if (attributes.HasFlag(FileAttributes.SparseFile)) text.Append('Z');
        return text.ToString();
    }

    /// <summary>
    /// WinDirStat's field splitter ends a quoted value at the next <c>"</c> — it has no notion of an
    /// escaped quote, so a value containing one would corrupt every following column. Windows paths
    /// cannot contain <c>"</c>, so this should never fire; it is here so that if it ever does, it is
    /// an error rather than a silently mangled file.
    /// </summary>
    public static bool IsSafeValue(string? value) =>
        value is null || !value.Contains('"', StringComparison.Ordinal);
}
