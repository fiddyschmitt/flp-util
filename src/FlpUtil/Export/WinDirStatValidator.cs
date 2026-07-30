using System.Globalization;
using System.Text;

namespace FlpUtil.Export;

public sealed class WinDirStatValidation
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public int DataRows { get; set; }
    public int AttachedRows { get; set; }
    public int DroppedRows { get; set; }
    public long RootLogicalBytes { get; set; }
    public long RootPhysicalBytes { get; set; }
    public bool ChildSumsMatch { get; set; }

    public bool Ok => Errors.Count == 0 && DroppedRows == 0;
}

/// <summary>
/// Re-reads a generated file the way WinDirStat does, and reports what WinDirStat would not:
/// when <c>LoadResults</c> fails or drops rows it returns silently and the window just opens empty.
///
/// This deliberately mirrors <c>LoadResultsCsv</c> and <c>BuildAndAttachItem</c> from
/// <c>windirstat/CsvLoader.cpp</c> — including its field splitter, which ends a quoted value at the
/// next <c>"</c> and has no concept of an escaped quote — rather than using a well-behaved CSV
/// parser. A file that only a correct parser can read is a file WinDirStat cannot read.
/// </summary>
public static class WinDirStatValidator
{
    public static WinDirStatValidation Validate(string path)
    {
        var result = new WinDirStatValidation();

        // WinDirStat reads bytes as UTF-8 and skips a BOM if present.
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            result.Errors.Add("File is empty.");
            return result;
        }

        List<string> header = SplitLine(lines[0].TrimStart('﻿'));
        var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Count; i++)
            columnIndex.TryAdd(header[i], i);

        foreach (string required in WinDirStatFormat.RequiredColumns)
        {
            if (!columnIndex.ContainsKey(required))
                result.Errors.Add($"Header is missing the required column '{required}'.");
        }

        if (result.Errors.Count > 0)
            return result;

        int maxRequired = WinDirStatFormat.RequiredColumns.Max(c => columnIndex[c]);

        // Mirrors WinDirStat's parentMap: a folder is only registered once it has been seen, so a
        // child listed before its parent is unattachable.
        var parents = new Dictionary<string, (long Logical, long Physical)>(StringComparer.OrdinalIgnoreCase);
        var childSums = new Dictionary<string, (long Logical, long Physical)>(StringComparer.OrdinalIgnoreCase);
        var childless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int roots = 0;
        string? rootName = null;

        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            if (lines[lineNumber].Length == 0)
                continue; // WinDirStat skips blank lines

            List<string> fields = SplitLine(lines[lineNumber]);
            result.DataRows++;

            if (fields.Count <= maxRequired)
            {
                result.DroppedRows++;
                Note(result, $"line {lineNumber + 1}: only {fields.Count} fields, need more than {maxRequired}.");
                continue;
            }

            string name = fields[columnIndex[WinDirStatFormat.ColumnName]];
            uint itemType = ParseHex32(fields[columnIndex[WinDirStatFormat.ColumnItemType]]);
            long logical = ParseLong(fields[columnIndex[WinDirStatFormat.ColumnLogicalSize]]);
            long physical = ParseLong(fields[columnIndex[WinDirStatFormat.ColumnPhysicalSize]]);
            long files = ParseLong(fields[columnIndex[WinDirStatFormat.ColumnFiles]]);
            long folders = ParseLong(fields[columnIndex[WinDirStatFormat.ColumnFolders]]);

            if (!WinDirStatFormat.IsSafeValue(name))
            {
                result.Errors.Add($"line {lineNumber + 1}: name contains a double quote, which WinDirStat cannot parse.");
                continue;
            }

            bool isRoot = (itemType & WinDirStatFormat.ItfRootItem) != 0;
            bool isDrive = (itemType & WinDirStatFormat.ItMask()) == WinDirStatFormat.ItDrive;
            bool isContainer = isRoot || isDrive || (itemType & WinDirStatFormat.ItDirectory) != 0;

            if (isRoot)
            {
                roots++;
                rootName = name;
                result.RootLogicalBytes = logical;
                result.RootPhysicalBytes = physical;
            }
            else if (isDrive)
            {
                if (rootName is null)
                {
                    result.DroppedRows++;
                    Note(result, $"line {lineNumber + 1}: drive '{name}' appears before any root item.");
                    continue;
                }

                Accumulate(childSums, rootName, logical, physical);
            }
            else
            {
                int separator = name.LastIndexOf('\\');
                string parentPath = separator < 0 ? string.Empty : name[..separator];

                // WinDirStat access-violates on a non-drive child of the pseudo root. Its loader
                // accepts the row; the crash comes later, so this has to be caught here.
                if (rootName is not null && string.Equals(parentPath, rootName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"line {lineNumber + 1}: '{name}' hangs directly off the root item. "
                        + "Only drives may be children of an IT_MYCOMPUTER root - anything else crashes "
                        + "WinDirStat with an access violation.");
                    continue;
                }

                if (parentPath.Length == 0 || !parents.ContainsKey(parentPath))
                {
                    result.DroppedRows++;
                    Note(result, childless.Contains(parentPath)
                        ? $"line {lineNumber + 1}: parent '{parentPath}' of '{name}' reports 0 files and 0 folders, "
                            + "so WinDirStat never registered it as a parent."
                        : $"line {lineNumber + 1}: parent '{parentPath}' of '{name}' was not defined by an earlier row.");
                    continue;
                }

                Accumulate(childSums, parentPath, logical, physical);
            }

            result.AttachedRows++;

            // GetItemsCount() > 0 gates parent registration. A container reporting zero items is
            // fine as long as nothing beneath it follows; it is only a fault if a later row needs
            // it as a parent, which shows up as a drop below.
            if (isContainer)
            {
                if (files + folders <= 0)
                {
                    childless.Add(name);
                }
                else
                {
                    parents[name] = (logical, physical);
                    if (isDrive && name.Length >= 2)
                        parents[name[..2]] = (logical, physical);
                }
            }
        }

        if (roots != 1)
            result.Errors.Add($"Expected exactly one row flagged ITF_ROOTITEM, found {roots}.");

        // The point of the synthetic <folder entry> rows: every container's size should be exactly
        // the sum of its children.
        result.ChildSumsMatch = true;
        foreach (var (parentPath, declared) in parents)
        {
            if (parentPath.Length == 2 && parentPath[1] == ':')
                continue; // the drive's alias key, already checked under its full name

            if (!childSums.TryGetValue(parentPath, out var summed))
                continue;

            if (summed.Logical != declared.Logical || summed.Physical != declared.Physical)
            {
                result.ChildSumsMatch = false;
                Note(result, $"'{parentPath}': children sum to {summed.Logical:N0}/{summed.Physical:N0} "
                    + $"but the row declares {declared.Logical:N0}/{declared.Physical:N0}.");
            }
        }

        return result;
    }

    private static void Accumulate(Dictionary<string, (long Logical, long Physical)> sums,
        string key, long logical, long physical)
    {
        sums.TryGetValue(key, out var current);
        sums[key] = (current.Logical + logical, current.Physical + physical);
    }

    private static void Note(WinDirStatValidation result, string message)
    {
        const int limit = 12;
        if (result.Warnings.Count < limit)
            result.Warnings.Add(message);
        else if (result.Warnings.Count == limit)
            result.Warnings.Add("... further messages suppressed.");
    }

    /// <summary>
    /// WinDirStat's own field splitter, faithfully: a quoted field ends at the next <c>"</c>, with no
    /// unescaping, and an unquoted field ends at the next comma.
    /// </summary>
    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        for (int pos = 0; pos < line.Length; pos++)
        {
            int comma = line.IndexOf(',', pos);
            int end = comma < 0 ? line.Length : comma;

            bool quoted = line[pos] == '"';
            if (quoted)
            {
                pos++;
                end = line.IndexOf('"', pos);
                if (end < 0)
                    return fields; // WinDirStat aborts the whole load here
            }

            fields.Add(line[pos..end]);
            pos = end + (quoted ? 1 : 0);
        }

        return fields;
    }

    private static uint ParseHex32(string value)
    {
        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0;
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
}
