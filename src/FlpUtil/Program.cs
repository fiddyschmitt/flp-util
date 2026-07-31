using System.Text;
using FlpUtil.Cli;
using FlpUtil.Commands;
using FlpUtil.Export;
using FlpUtil.Flp;

namespace FlpUtil;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            return Dispatch(CommandLine.Parse(args));
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine($"flp-util: {ex.Message}");
            Console.Error.WriteLine("Run 'flp-util help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"flp-util: {ex.Message}");
            return 1;
        }
    }

    private static int Dispatch(CommandLine cmd)
    {
        if (cmd.Verbs.Count == 0 || cmd.HasVerb("help") || cmd.HasFlag("help") || cmd.HasFlag("h"))
            return PrintUsage();

        // Progress goes to stderr, so redirecting stdout to a file still gives clean output.
        IProgressSink progress = cmd.HasFlag("quiet") ? NullProgress.Instance : new ConsoleProgress();

        if (cmd.HasVerb("index", "list"))
            return IndexListCommand.Run();

        if (cmd.HasVerb("index", "info"))
            return IndexInfoCommand.Run(ResolvePath(cmd), progress);

        if (cmd.HasVerb("index", "dump"))
            return IndexDumpCommand.Run(
                ResolvePath(cmd),
                take: cmd.GetInt("take") ?? 5,
                onlyDocId: cmd.GetInt("doc"),
                whereField: cmd.GetString("where"),
                whereValue: cmd.GetString("value"));

        if (cmd.HasVerb("index", "values"))
            return IndexValuesCommand.Run(
                ResolvePath(cmd),
                field: cmd.GetRequiredString("field"),
                take: cmd.GetInt("take") ?? 20,
                progress);

        if (cmd.HasVerb("index", "cost"))
            return IndexCostCommand.Run(
                ResolvePath(cmd),
                outputPath: cmd.GetString("out"),
                top: cmd.GetInt("top") ?? 15,
                delimiter: cmd.GetDelimiter("delimiter", ','),
                byFolder: cmd.HasFlag("by-folder"),
                depth: cmd.GetInt("depth") ?? 0,
                progress);

        if (cmd.HasVerb("wds", "validate"))
            return WdsValidateCommand.Run(cmd.GetRequiredString("file"), progress);

        if (cmd.HasVerb("index", "treemap"))
            return IndexTreemapCommand.Run(
                ResolvePath(cmd),
                outputPath: cmd.GetRequiredString("out"),
                label: cmd.GetString("label"),
                open: cmd.HasFlag("open"),
                progress);

        if (cmd.HasVerb("export"))
            return ExportCommand.Run(new ExportOptions
            {
                IndexPath = ResolvePath(cmd),
                OutputPath = cmd.GetRequiredString("out"),
                IncludeFolders = cmd.HasFlag("include-folders"),
                Raw = cmd.HasFlag("raw"),
                Delimiter = cmd.GetDelimiter("delimiter", ','),
                MultiValueSeparator = cmd.GetString("multi-value-sep") ?? "|",
            }, progress);

        Console.Error.WriteLine($"flp-util: unknown command '{string.Join(' ', cmd.Verbs)}'.");
        return PrintUsage(toStdErr: true);
    }

    private static string ResolvePath(CommandLine cmd) =>
        FlpConfig.ResolveIndexPath(cmd.GetString("path"), cmd.GetString("name"));

    private static int PrintUsage(bool toStdErr = false)
    {
        TextWriter output = toStdErr ? Console.Error : Console.Out;
        output.WriteLine("""
            flp-util - utilities for FileLocator Pro

            Usage:
              flp-util index list
                  List the indexes FileLocator Pro has registered.

              flp-util index info   (--path <store> | --name <index>)
                  Document counts, index settings, store files and the real stored-field schema.

              flp-util index dump   (--path <store> | --name <index>) [options]
                  Print raw stored fields, uninterpreted. Useful for inspecting the index.
                    --take <n>        documents to print (default 5)
                    --doc <id>        print one specific document id
                    --where <field>   only documents that have this field...
                    --value <text>    ...and whose value contains this text

              flp-util index values (--path <store> | --name <index>) --field <name> [--take <n>]
                  Distinct values of one stored field, with counts and decoded meaning.

              flp-util index cost   (--path <store> | --name <index>) [options]
                  How many index bytes each file, or each folder, is responsible for.
                    --by-folder       rank folders by subtree cost instead of listing files
                    --depth <n>       with --by-folder, only folders at or above this depth
                    --top <n>         rows to print (default 15)
                    --out <file.csv>  also write the full per-owner breakdown

              flp-util index treemap (--path <store> | --name <index>) --out <file.csv> [options]
                  Write index cost as a WinDirStat saved-results file, then verify it against
                  WinDirStat's own load rules.
                    --label <text>    name for the root node (default "FLP index: <store>")
                    --open            launch WinDirStat with /loadfrom on the result

              flp-util wds validate --file <file.csv>
                  Check any WinDirStat saved-results file against the loader's real rules
                  (dropped rows, child sums, root-child crash) - feedback WinDirStat never gives.

              flp-util export       (--path <store> | --name <index>) --out <file.csv> [options]
                  Export every indexed item, with all metadata, to CSV.
                    --include-folders     also emit a row per folder (default: files only)
                    --raw                 raw stored fields only, no decoded columns
                    --delimiter <c|tab>   field delimiter (default ,)
                    --multi-value-sep <s> joins repeated field values (default |)

            Index selection:
              --path <store>   path to the index store folder
              --name <index>   name of an index registered with FileLocator Pro

            Global:
              --quiet          suppress progress reporting (progress goes to stderr, so stdout
                               stays clean when redirected either way)
            """);
        return toStdErr ? 2 : 0;
    }
}
