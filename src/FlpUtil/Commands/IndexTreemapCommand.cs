using System.Diagnostics;
using FlpUtil.Cli;
using FlpUtil.Export;
using FlpUtil.Flp;
using FlpUtil.Index;

namespace FlpUtil.Commands;

/// <summary>
/// Writes the index's cost as a WinDirStat saved-results file, so its tree/list view and treemap can
/// be used to find the folders that cost the most to index.
/// </summary>
public static class IndexTreemapCommand
{
    public static int Run(string indexPath, string outputPath, string? label, bool open,
        IProgressSink? progress = null)
    {
        IProgressSink sink = progress ?? NullProgress.Instance;
        using var reader = new FlpIndexReader(indexPath);

        Console.WriteLine($"Analysing {reader.IndexPath}");
        IndexCostReport report = new IndexCostAnalyzer(reader, sink).Analyze();
        CostTree tree = CostTree.Build(report);

        Console.WriteLine($"  {tree.Nodes.Count:N0} folders, {tree.Roots.Count:N0} root(s)");
        if (tree.MergedContainerNodes > 0)
            Console.WriteLine($"  {tree.MergedContainerNodes:N0} container node(s) unified with their interior roots");
        if (tree.GraftedRoots > 0)
            Console.WriteLine($"  {tree.GraftedRoots:N0} container interior tree(s) grafted at their filesystem path");
        if (tree.Orphans.Count > 0)
        {
            Console.WriteLine($"  warning: {tree.Orphans.Count:N0} row(s) could not be placed in the folder tree "
                + "and are omitted from the treemap.");
        }

        string rootLabel = label ?? DefaultLabel(reader.IndexPath);
        WinDirStatWriteResult written = WinDirStatWriter.Write(outputPath, report, tree, rootLabel, sink);
        string fullPath = Path.GetFullPath(outputPath);

        Console.WriteLine();
        Console.WriteLine($"Wrote {written.TotalRows:N0} rows to {fullPath}");
        Console.WriteLine($"  {written.FolderRows:N0} folders, {written.FileRows:N0} files, "
            + $"{written.SyntheticRows:N0} synthetic");
        Console.WriteLine($"  root Logical Size  {written.RootLogicalBytes,14:N0}  (exclusive bytes)");
        Console.WriteLine($"  root Physical Size {written.RootPhysicalBytes,14:N0}  (exclusive + apportioned shared)");
        Console.WriteLine($"  omitted            {written.OmittedBytes,14:N0}  (belongs to no path)");
        foreach (string reason in written.OmittedReasons)
            Console.WriteLine($"      {reason}");

        long accounted = written.RootPhysicalBytes + written.OmittedBytes;
        Console.WriteLine($"  actual store size  {report.ActualStoreBytes,14:N0}  "
            + (accounted == report.ActualStoreBytes
                ? "= root + omitted, exactly"
                : $"MISMATCH: root + omitted = {accounted:N0}"));

        if (written.UnsafeValues > 0)
        {
            Console.Error.WriteLine($"  error: {written.UnsafeValues} value(s) contain a double quote, "
                + "which WinDirStat's parser cannot handle.");
        }

        Console.WriteLine();
        if (!Verify(fullPath, written, sink))
            return 1;

        Console.WriteLine();
        Console.WriteLine("Load it with:");
        Console.WriteLine($"  WinDirStat.exe /loadfrom \"{fullPath}\"");
        Console.WriteLine("Toggle Options > treemap logical/physical size to switch between exclusive and apportioned bytes.");

        if (open)
            Launch(fullPath);

        return 0;
    }

    /// <summary>
    /// Names the root node after the index as FileLocator Pro knows it, falling back to the store
    /// folder — a store directory is often called something like <c>.flpindex</c>, which makes a poor
    /// label on its own.
    /// </summary>
    private static string DefaultLabel(string indexPath)
    {
        string full = Path.GetFullPath(indexPath).TrimEnd('\\');

        string? registered = FlpConfig.ListIndexes()
            .FirstOrDefault(i => string.Equals(
                Path.GetFullPath(i.Path).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
            ?.Name;

        return $"FLP index cost: {registered ?? Path.GetFileName(full)}";
    }

    /// <summary>
    /// Re-reads the file with WinDirStat's own rules. Worth doing every time: WinDirStat drops
    /// unattachable rows and rejects malformed files without saying anything at all.
    /// </summary>
    private static bool Verify(string path, WinDirStatWriteResult written, IProgressSink sink)
    {
        WinDirStatValidation validation = WinDirStatValidator.Validate(path, sink);

        Console.WriteLine("Conformance check (WinDirStat's own load rules):");
        Console.WriteLine($"  data rows       {validation.DataRows,10:N0}");
        Console.WriteLine($"  attached        {validation.AttachedRows,10:N0}");
        Console.WriteLine($"  dropped         {validation.DroppedRows,10:N0}   "
            + (validation.DroppedRows == 0 ? "none" : "THESE WOULD BE SILENTLY LOST"));
        Console.WriteLine($"  child sums      {(validation.ChildSumsMatch ? "     exact" : "  MISMATCH"),10}   "
            + "every folder equals the sum of its children");
        Console.WriteLine($"  root physical   {validation.RootPhysicalBytes,10:N0}   "
            + (validation.RootPhysicalBytes == written.RootPhysicalBytes
                ? "matches what we wrote"
                : $"expected {written.RootPhysicalBytes:N0}"));

        foreach (string warning in validation.Warnings)
            Console.WriteLine($"    - {warning}");
        foreach (string error in validation.Errors)
            Console.Error.WriteLine($"  error: {error}");

        if (!validation.Ok)
        {
            Console.Error.WriteLine("  The file would not load correctly. Not reporting success.");
            return false;
        }

        Console.WriteLine("  OK - WinDirStat will load every row.");
        return true;
    }

    private static void Launch(string path)
    {
        string? exe = FlpConfig.FindWinDirStat();
        if (exe is null)
        {
            Console.Error.WriteLine("Could not find WinDirStat.exe; pass its folder in WINDIRSTAT_PATH or open the file manually.");
            return;
        }

        Console.WriteLine($"Launching {exe}");
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "/loadfrom", path },
            UseShellExecute = false,
        });
    }
}
