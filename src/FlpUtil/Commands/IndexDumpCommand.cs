using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>
/// Prints raw stored fields document by document. This is the schema-discovery / calibration tool:
/// it makes no attempt to interpret values, so it shows exactly what FLP wrote.
/// </summary>
public static class IndexDumpCommand
{
    public static int Run(string indexPath, int take, int? onlyDocId, string? whereField, string? whereValue)
    {
        using var reader = new FlpIndexReader(indexPath);

        int shown = 0;
        foreach (var doc in reader.ReadAll())
        {
            if (onlyDocId is { } wanted && doc.DocId != wanted)
                continue;

            if (whereField is not null)
            {
                var values = doc.GetAll(whereField);
                bool match = whereValue is null
                    ? values.Count > 0
                    : values.Any(v => v.Contains(whereValue, StringComparison.OrdinalIgnoreCase));
                if (!match)
                    continue;
            }

            Console.WriteLine($"--- doc {doc.DocId} ---");
            foreach (var (name, values) in doc.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                foreach (string value in values)
                    Console.WriteLine($"  {name,-14} = {Truncate(value)}");
            }

            Console.WriteLine();

            if (++shown >= take)
                break;
        }

        if (shown == 0)
            Console.WriteLine("No matching documents.");

        return 0;
    }

    private static string Truncate(string value)
    {
        const int limit = 300;
        string oneLine = value.ReplaceLineEndings("\\n");
        return oneLine.Length <= limit ? oneLine : $"{oneLine[..limit]}… ({value.Length:N0} chars)";
    }
}
