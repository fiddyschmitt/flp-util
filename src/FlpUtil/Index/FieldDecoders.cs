using System.Globalization;

namespace FlpUtil.Index;

/// <summary>How FLP encoded a stored field's text.</summary>
public enum FlpEncoding
{
    Text,
    DecimalNumber,
    HexNumber,

    /// <summary>Windows FILETIME written in decimal, e.g. <c>134267767343779969</c>.</summary>
    DecimalFileTime,

    /// <summary>The same FILETIME written in hex without a prefix, e.g. <c>1dd03d2b14f9081</c>.</summary>
    HexFileTime,

    /// <summary>Hex Windows file-attribute mask, e.g. <c>20</c> = Archive.</summary>
    HexAttributes,

    /// <summary>Hex status bitmask.</summary>
    HexFlags,
}

/// <summary>
/// Turns FLP's raw stored values into readable ones.
///
/// FLP writes every field as text but is not consistent about the base: sizes and the
/// <c>*ft</c> timestamps are decimal, while attributes, flags, term counts and the <c>*dt</c>
/// timestamps are unprefixed hex. Each encoding below was verified against the filesystem
/// (see README), and <see cref="FlpSchema"/> records which field uses which.
///
/// Every decoder returns null when it cannot make sense of its input; the caller then leaves the
/// friendly column blank and falls back to the raw column, so a schema change can never silently
/// corrupt an export.
/// </summary>
public static class FieldDecoders
{
    public static long? TryParseNumber(string? raw, FlpEncoding encoding)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        if (encoding is FlpEncoding.DecimalNumber or FlpEncoding.DecimalFileTime)
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : null;
        }

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : null;
    }

    /// <summary>Decodes a Windows FILETIME field (100 ns ticks since 1601-01-01 UTC).</summary>
    public static DateTime? TryParseFileTime(string? raw, FlpEncoding encoding)
    {
        long? ticks = TryParseNumber(raw, encoding);
        if (ticks is null or <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(ticks.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>ISO-8601 UTC to the tick — the only unambiguous thing to put in a CSV.</summary>
    public static string FormatTimestamp(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Renders the hex <c>attrx</c> mask as Windows attribute names.</summary>
    public static string FormatFileAttributes(string? raw)
    {
        long? mask = TryParseNumber(raw, FlpEncoding.HexAttributes);
        if (mask is null or 0)
            return string.Empty;

        // Enum.ToString prints unknown bits as a number, which is still information worth keeping.
        return ((FileAttributes)mask.Value).ToString().Replace(", ", "|", StringComparison.Ordinal);
    }

    public static bool HasAttribute(string? raw, FileAttributes attribute)
    {
        long? mask = TryParseNumber(raw, FlpEncoding.HexAttributes);
        return mask is not null && (mask.Value & (long)attribute) != 0;
    }

    public static IndexFlags DecodeIndexFlags(string? raw)
    {
        long? mask = TryParseNumber(raw, FlpEncoding.HexFlags);
        return mask is null ? IndexFlags.Unknown : new IndexFlags(mask.Value);
    }
}

/// <summary>
/// Decoded view of FLP's per-item <c>idxfl</c> status bitmask.
///
/// Only bit 0 has been verified: it is set on items whose content was indexed and clear on
/// name-only items. Anything else is surfaced through <see cref="OtherBits"/> rather than guessed
/// at, so an unrecognised flag shows up in the export instead of being silently dropped.
/// </summary>
public readonly struct IndexFlags(long mask)
{
    public const long ContentIndexed = 0x01;

    public static IndexFlags Unknown => new(-1);

    public bool IsKnown => mask >= 0;

    public long Mask => mask;

    public bool HasContent => IsKnown && (mask & ContentIndexed) != 0;

    public string OtherBits
    {
        get
        {
            if (!IsKnown)
                return string.Empty;

            long rest = mask & ~ContentIndexed;
            if (rest == 0)
                return string.Empty;

            var names = new List<string>();
            for (int bit = 0; bit < 63; bit++)
            {
                if ((rest & (1L << bit)) != 0)
                    names.Add($"bit{bit}");
            }

            return string.Join('|', names);
        }
    }
}
