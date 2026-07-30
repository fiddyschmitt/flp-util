using System.Xml.Linq;
using Microsoft.Win32;

namespace FlpUtil.Flp;

/// <summary>One index as registered with FileLocator Pro.</summary>
public sealed record FlpIndexRef(string Name, string Path, string Id, bool ReadOnly, string ConfigFile);

/// <summary>
/// Locates FileLocator Pro's own configuration so <c>--name</c> can be resolved to an index store.
///
/// FLP records one <c>idx_{guid}.xml</c> per index in its config folder; the folder itself is
/// described by the <c>Folders</c> value under HKCU\SOFTWARE\Mythicsoft\FileLocatorPro\Core, using
/// <c>$(ApplicationData)</c> to mean %APPDATA%\Mythicsoft\FileLocatorPro.
/// </summary>
public static class FlpConfig
{
    private const string DefaultAppDataLeaf = @"Mythicsoft\FileLocatorPro";

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DefaultAppDataLeaf);

    public static string ConfigFolder
    {
        get
        {
            string appData = AppDataFolder;
            string? pattern = ReadFolderPattern("ConfigFile");
            return pattern is null
                ? Path.Combine(appData, "config")
                : Path.GetFullPath(pattern.Replace("$(ApplicationData)", appData, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Installed program directory, or null when FileLocator Pro cannot be found.</summary>
    public static string? InstallFolder
    {
        get
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     })
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                string candidate = Path.Combine(root, "Mythicsoft", "FileLocator Pro");
                if (File.Exists(Path.Combine(candidate, "flpidx.exe")))
                    return candidate;
            }

            return null;
        }
    }

    /// <summary>
    /// Locates a WinDirStat executable so a generated treemap file can be opened directly. Portable
    /// copies are common, so an explicit <c>WINDIRSTAT_PATH</c> (file or folder) wins over the
    /// installed locations.
    /// </summary>
    public static string? FindWinDirStat()
    {
        var candidates = new List<string>();

        if (Environment.GetEnvironmentVariable("WINDIRSTAT_PATH") is { Length: > 0 } configured)
        {
            candidates.Add(configured);
            candidates.Add(Path.Combine(configured, "WinDirStat.exe"));
        }

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                 })
        {
            if (!string.IsNullOrEmpty(root))
                candidates.Add(Path.Combine(root, "WinDirStat", "WinDirStat.exe"));
        }

        return candidates.FirstOrDefault(c => c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(c));
    }

    /// <summary>Every index registered with FileLocator Pro for the current user.</summary>
    public static IReadOnlyList<FlpIndexRef> ListIndexes()
    {
        string configFolder = ConfigFolder;
        if (!Directory.Exists(configFolder))
            return [];

        var results = new List<FlpIndexRef>();
        foreach (string file in Directory.EnumerateFiles(configFolder, "idx_*.xml"))
        {
            if (TryReadIndexRef(file) is { } indexRef)
                results.Add(indexRef);
        }

        return [.. results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Resolves an index store folder from either an explicit <c>--path</c> or an FLP index
    /// <c>--name</c>. Name matching is case-insensitive even though flpidx.exe's own is not.
    /// </summary>
    public static string ResolveIndexPath(string? path, string? name)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(path);

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Specify either --path <index store> or --name <FLP index name>.");

        var matches = ListIndexes()
            .Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0].Path,
            0 => throw new InvalidOperationException(
                $"No FileLocator Pro index named '{name}'. Run 'flp-util index list' to see what is registered."),
            _ => throw new InvalidOperationException(
                $"'{name}' matches {matches.Count} registered indexes; use --path to disambiguate."),
        };
    }

    private static FlpIndexRef? TryReadIndexRef(string file)
    {
        try
        {
            XElement? section = XDocument.Load(file).Root?
                .Elements("section")
                .FirstOrDefault(s => (string?)s.Attribute("name") == "idx");
            if (section is null)
                return null;

            string? name = section.Element("name")?.Value;
            string? path = section.Element("path")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return null;

            return new FlpIndexRef(
                Name: name,
                Path: path,
                Id: section.Element("id")?.Value ?? string.Empty,
                ReadOnly: section.Element("readonly")?.Attribute("n")?.Value is not (null or "0"),
                ConfigFile: file);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadFolderPattern(string element)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Mythicsoft\FileLocatorPro\Core");
            if (key?.GetValue("Folders") is not string xml)
                return null;

            return XDocument.Parse(xml).Root?
                .Elements("section")
                .FirstOrDefault(s => (string?)s.Attribute("name") == "FOLDERS")?
                .Element(element)?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.Xml.XmlException or IOException)
        {
            return null;
        }
    }
}
