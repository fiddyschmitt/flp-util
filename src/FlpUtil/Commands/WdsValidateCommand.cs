using FlpUtil.Cli;
using FlpUtil.Export;

namespace FlpUtil.Commands;

/// <summary>
/// Validates any WinDirStat saved-results file against the loader's real rules — the check
/// WinDirStat itself never performs out loud. Useful for files from other tools, hand edits, or
/// diagnosing why a file opens empty.
/// </summary>
public static class WdsValidateCommand
{
    public static int Run(string filePath, IProgressSink? progress = null)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"flp-util: file not found: {filePath}");
            return 1;
        }

        WinDirStatValidation validation = WinDirStatValidator.Validate(filePath, progress);

        Console.WriteLine($"Validating {Path.GetFullPath(filePath)} against WinDirStat's load rules:");
        Console.WriteLine($"  data rows       {validation.DataRows,12:N0}");
        Console.WriteLine($"  attached        {validation.AttachedRows,12:N0}");
        Console.WriteLine($"  dropped         {validation.DroppedRows,12:N0}   "
            + (validation.DroppedRows == 0 ? "none" : "these rows would be SILENTLY lost"));
        Console.WriteLine($"  child sums      {(validation.ChildSumsMatch ? "exact" : "MISMATCH"),12}");
        Console.WriteLine($"  root sizes      {validation.RootLogicalBytes:N0} logical, {validation.RootPhysicalBytes:N0} physical");

        foreach (string warning in validation.Warnings)
            Console.WriteLine($"    - {warning}");
        foreach (string error in validation.Errors)
            Console.Error.WriteLine($"  error: {error}");

        if (!validation.Ok || !validation.ChildSumsMatch)
        {
            Console.Error.WriteLine("  WinDirStat would not load this file faithfully.");
            return 1;
        }

        Console.WriteLine("  OK - WinDirStat will load every row.");
        return 0;
    }
}
