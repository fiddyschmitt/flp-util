using System.Globalization;
using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>
/// Distinct values of one stored field, with counts and the decoded interpretation. Useful for
/// working out what an opaque FLP field actually holds (which bits of <c>idxfl</c> occur, which
/// <c>itemtype</c> values exist, and so on).
/// </summary>
public static class IndexValuesCommand
{
    public static int Run(string indexPath, string field, int take)
    {
        using var reader = new FlpIndexReader(indexPath);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;

        foreach (var doc in reader.ReadAll())
        {
            foreach (string value in doc.GetAll(field))
            {
                counts[value] = counts.GetValueOrDefault(value) + 1;
                total++;
            }
        }

        if (total == 0)
        {
            Console.WriteLine($"No document stores a field named '{field}'.");
            return 1;
        }

        FlpEncoding encoding = FlpSchema.EncodingOf(field);
        Console.WriteLine($"{field}: {counts.Count:N0} distinct value(s) across {total:N0} document(s), encoding {encoding}");
        Console.WriteLine();

        var ordered = counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal);

        foreach (var (value, count) in ordered.Take(take))
            Console.WriteLine($"  {count,10:N0}  {Display(value),-40}  {Decode(value, encoding)}");

        if (counts.Count > take)
            Console.WriteLine($"  ... {counts.Count - take:N0} more (raise --take to see them)");

        return 0;
    }

    private static string Display(string value)
    {
        string oneLine = value.ReplaceLineEndings("\\n");
        return oneLine.Length <= 40 ? oneLine : oneLine[..39] + "…";
    }

    private static string Decode(string value, FlpEncoding encoding) => encoding switch
    {
        FlpEncoding.HexAttributes => FieldDecoders.FormatFileAttributes(value),
        FlpEncoding.DecimalFileTime or FlpEncoding.HexFileTime =>
            FieldDecoders.FormatTimestamp(FieldDecoders.TryParseFileTime(value, encoding)),
        FlpEncoding.HexFlags => DescribeFlags(value),
        FlpEncoding.HexNumber or FlpEncoding.DecimalNumber =>
            FieldDecoders.TryParseNumber(value, encoding)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        _ => string.Empty,
    };

    private static string DescribeFlags(string value)
    {
        var flags = FieldDecoders.DecodeIndexFlags(value);
        if (!flags.IsKnown)
            return string.Empty;

        var parts = new List<string> { $"0x{flags.Mask:x}" };
        if (flags.HasContent)
            parts.Add("ContentIndexed");
        if (flags.OtherBits.Length > 0)
            parts.Add(flags.OtherBits);
        return string.Join(' ', parts);
    }
}
