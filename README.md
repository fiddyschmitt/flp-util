# flp-util

Utilities for [FileLocator Pro](https://www.mythicsoft.com/filelocatorpro/) (FLP). A .NET 10 console
app. The first feature exports every item in an FLP index, with all of its metadata, to CSV.

```
dotnet run --project src/FlpUtil -- export --name "my-index" --out files.csv
```

## How this works

FLP itself is native C++ — none of its binaries are managed assemblies, so there is nothing to
reflect over. But its `Credits.txt` says the indexer is built on **Lucene++**, a C++ port of Java
Lucene 3.x, and that is confirmed by the store on disk: `segments.gen`, `segments_N`, `_N.cfs`, with
a segments header of `-9` (`FORMAT_DIAGNOSTICS`, i.e. Lucene 3.0).

So an FLP index store is a **plain Lucene 3.0 index**, and `Lucene.Net 4.8.0-beta*` can read it
directly through its read-only `Lucene3xCodec` — `DirectoryReader.Open` selects that codec
automatically when it sees a pre-4.0 header. No format reverse-engineering, no unsafe parsing.

Everything that touches Lucene is confined to [`FlpIndexReader`](src/FlpUtil/Index/FlpIndexReader.cs).
The index is opened read-only with `NoLockFactory`, so running an export while `flpidx.exe` is
updating the same index is safe (verified — see below).

## Index layout

Documented in [`FlpSchema`](src/FlpUtil/Index/FlpSchema.cs). FLP writes **three kinds of document**
into one Lucene index, and an item's metadata is split across two of them:

| Kind | Key fields | Notes |
|---|---|---|
| folder | `fldrid`, `fldrpid`, `fldrnm`, `fldrkey` | The directory tree. A root has `fldrpid` = `root` and a name in Win32 long-path form (`\\?\C:`). |
| item | `id`, `name`, `sizenr`, `modft`, `createft`, `attrx`, `itemtype`, `exinfo` | One per indexed **file and directory**. |
| meta | `mid`, `idxdt`, `idxfl`, `idxtrm`, `moddt`, `intid` | Indexing status for an item. |

Plus exactly one index-level document (`idxv`, `idxprms`, `idxdtstr`, `ncid`).

Two things make this awkward, and are the bulk of what `flp-util` does for you:

1. **No item carries a path.** An item's `id` is `{fldrid}:{name}`, so the parent folder id is
   embedded in the id and the folder documents have to be assembled into a tree and walked upwards.
   (`filepid` exists but is indexed-only, not stored, so it cannot be read back.)
2. **Item and meta documents must be joined** on `id` == `mid` to get name/size/dates *and*
   indexing status on one row.

### Field encodings

FLP stores everything as text but is not consistent about the base — half the numeric fields are
decimal and half are unprefixed hex:

| Field | Encoding | Meaning |
|---|---|---|
| `sizenr` | decimal | size in bytes |
| `modft`, `createft`, `idxdt` | decimal | Windows FILETIME (100 ns ticks since 1601-01-01 UTC) |
| `moddt`, `idxdtstr` | **hex** | the same FILETIME, e.g. `1dd03d2b14f9081` |
| `attrx` | **hex** | Windows file-attribute mask — `20` is Archive, not decimal 20 |
| `idxfl` | **hex** | indexing status bits |
| `idxtrm` | **hex** | number of indexed terms |
| `itemtype` | decimal | `1` = file, `4` = directory |

Only **bit 0** of `idxfl` has been verified: set on items whose content was indexed, clear on
name-only items. Other bits were not observed in any test index, so they are deliberately *not*
guessed at — anything unrecognised is reported in the `OtherFlagBits` column rather than dropped.

Every decoder returns blank rather than a wrong value when it cannot parse its input, and the raw
value is always emitted too, so a future FLP schema change cannot silently corrupt an export.

## Commands

```
flp-util index list
    List the indexes FileLocator Pro has registered (read from %APPDATA%\Mythicsoft\
    FileLocatorPro\config\idx_*.xml, located via HKCU\SOFTWARE\Mythicsoft\FileLocatorPro\Core).

flp-util index info   (--path <store> | --name <index>)
    Document counts, index settings, store files, and the real stored-field schema.

flp-util index dump   (--path <store> | --name <index>) [--take n] [--doc id]
                      [--where <field>] [--value <text>]
    Print raw stored fields, uninterpreted.

flp-util index values (--path <store> | --name <index>) --field <name> [--take n]
    Distinct values of one field with counts and decoded meaning. This is how the encodings
    above were established.

flp-util export       (--path <store> | --name <index>) --out <file.csv>
                      [--include-folders] [--raw] [--delimiter <c|tab>] [--multi-value-sep <s>]
    Export every indexed item with all metadata to CSV.
```

### CSV output

Decoded columns first, then every raw stored field as `raw_<field>` — nothing in the index is
dropped:

```
FullPath, Folder, Name, Extension, IsFolder, SizeBytes, Modified, Created, IndexedDate,
ContentIndexed, OtherFlagBits, TermCount, Attributes, ItemType, FolderId, ItemId, DocId, MetaDocId,
raw_attrx, raw_createft, raw_exinfo, raw_id, raw_idxdt, raw_idxfl, raw_idxtrm, raw_intid,
raw_itemtype, raw_mid, raw_moddt, raw_modft, raw_name, raw_sizenr
```

Timestamps are ISO-8601 UTC to the tick. Output is RFC 4180 with a UTF-8 BOM so Excel handles
non-ASCII file names. Directories are excluded unless `--include-folders` is passed; `--raw` drops
the decoded block.

## Creating an index

`flp-util` only reads indexes — create them with FLP's own tool:

```powershell
& "C:\Program Files\Mythicsoft\FileLocator Pro\flpidx.exe" `
    -create -name "my-index" -path "D:\indexes\my-index" `
    -d "C:\Docs;C:\Projects" -fd -fnc -i
```

`-fd` content-indexes standard document types; `-fnc` adds every other file by name only, which is
what makes the export a complete file listing. Note `flpidx.exe` writes **UTF-16LE** to stdout, so
redirect to a file and read it back with `Get-Content -Encoding Unicode`.

## Verification

Against an index of `C:\Users\Smith\Desktop\dev\go` + `C:\Users\Smith\Downloads`
(15,006 Lucene documents = 6,863 items + 6,863 meta + 1,279 folders + 1 index doc):

| Check | Result |
|---|---|
| CSV rows vs files on disk | 5,583 vs 5,583, no duplicates, no differences either way |
| `SizeBytes` vs `FileInfo.Length` | 0 mismatches across all 5,583 rows |
| `Modified` vs `LastWriteTimeUtc` | 0 mismatches across all 5,583 rows (exact ticks) |
| `Created` vs `CreationTimeUtc` | 0 mismatches across all 5,583 rows |
| `Attributes` vs `FileInfo.Attributes` | 0 mismatches (400 sampled) |
| `--include-folders` rows | all 1,280 resolve to real directories |
| `ContentIndexed` | Y only for FLP's standard document types (txt/html/png/zip/doc/…); term counts average 2,075 vs 3.3 for name-only items |
| `OtherFlagBits` | empty on every row — no unrecognised flag bits |
| Deepest path | 9 levels, resolves correctly |
| Export during `flpidx -update` | 6 concurrent exports, 0 failures |

## Notes and limits

- Windows-only (`net10.0-windows`) — FLP is a Windows product and the index list comes from HKCU.
- The export holds the folder tree and the meta lookup in memory (roughly one small dictionary per
  indexed item); rows themselves are streamed. Fine for millions of items, but not zero-allocation.
- Deleted documents are skipped via `MultiFields.GetLiveDocs`; FLP deletes and re-adds a document
  whenever a file changes, so an index updated in place accumulates dead slots.
- **Not yet read:** FLP's SQLite side-databases under
  `%APPDATA%\Mythicsoft\FileLocatorPro\IndexLog\{index-guid}\idxmonitor.db`, which back Index
  Manager's *Update Log* tab (per-file indexing duration, action type, warnings and errors). Joining
  those in would be the natural next feature.
