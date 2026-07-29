using System.Xml.Linq;
using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>Summarises an index store: document counts, the on-disk files, and the real schema.</summary>
public static class IndexInfoCommand
{
    public static int Run(string indexPath)
    {
        using var reader = new FlpIndexReader(indexPath);

        Console.WriteLine($"Index store : {reader.IndexPath}");
        Console.WriteLine($"Documents   : {reader.NumDocs:N0} live, {reader.NumDeletedDocs:N0} deleted, {reader.MaxDoc:N0} slots");
        Console.WriteLine($"Segments    : {string.Join(", ", reader.SegmentNames)}");
        Console.WriteLine();

        PrintSettings(reader.IndexPath);

        Console.WriteLine("Store files:");
        foreach (var file in new DirectoryInfo(reader.IndexPath).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {file.Name,-32} {file.Length,14:N0} bytes");
        Console.WriteLine();

        // Stored-only fields never show up in the indexed-field list, so scan the documents for
        // the authoritative column set.
        var storedFields = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in reader.ReadAll())
        {
            foreach (var name in doc.Fields.Keys)
                storedFields[name] = storedFields.GetValueOrDefault(name) + 1;
        }

        var indexedFields = reader.IndexedFieldNames.ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"Stored fields ({storedFields.Count}):");
        foreach (var (name, count) in storedFields)
        {
            string marker = indexedFields.Contains(name) ? " (also indexed)" : string.Empty;
            Console.WriteLine($"  {name,-20} {count,10:N0} docs{marker}");
        }

        var indexedOnly = indexedFields.Except(storedFields.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (indexedOnly.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Indexed but not stored ({indexedOnly.Count}): {string.Join(", ", indexedOnly)}");
        }

        return 0;
    }

    private static void PrintSettings(string indexPath)
    {
        string settingsFile = Path.Combine(indexPath, "index_settings.xml");
        if (!File.Exists(settingsFile))
            return;

        try
        {
            XElement? parms = XDocument.Load(settingsFile).Root?
                .Elements("section")
                .FirstOrDefault(s => (string?)s.Attribute("name") == "IdxParm");
            if (parms is null)
                return;

            Console.WriteLine("Index settings (index_settings.xml):");
            foreach (var element in parms.Elements())
            {
                string value = element.Attribute("n")?.Value ?? element.Value;
                Console.WriteLine($"  {element.Name.LocalName,-18} {value}");
            }

            Console.WriteLine();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            Console.WriteLine($"Index settings: <unreadable: {ex.Message}>");
            Console.WriteLine();
        }
    }
}
