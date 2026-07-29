using FlpUtil.Flp;
using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>Lists the indexes FileLocator Pro knows about, with a live doc count for each.</summary>
public static class IndexListCommand
{
    public static int Run()
    {
        var indexes = FlpConfig.ListIndexes();
        if (indexes.Count == 0)
        {
            Console.WriteLine($"No FileLocator Pro indexes registered (looked in {FlpConfig.ConfigFolder}).");
            return 0;
        }

        foreach (var index in indexes)
        {
            Console.WriteLine(index.Name);
            Console.WriteLine($"  path     : {index.Path}");
            Console.WriteLine($"  id       : {index.Id}");
            Console.WriteLine($"  readonly : {index.ReadOnly}");
            Console.WriteLine($"  items    : {DescribeStore(index.Path)}");
            Console.WriteLine();
        }

        return 0;
    }

    private static string DescribeStore(string path)
    {
        try
        {
            using var reader = new FlpIndexReader(path);
            return $"{reader.NumDocs:N0} live, {reader.NumDeletedDocs:N0} deleted";
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetType().Name}: {ex.Message}>";
        }
    }
}
