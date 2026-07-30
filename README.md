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

flp-util index cost   (--path <store> | --name <index>) [--out <file.csv>] [--top n]
                      [--by-folder] [--depth n]
    How many index bytes each file, or each folder subtree, is responsible for. See below.

flp-util index treemap (--path <store> | --name <index>) --out <file.csv> [--label <t>] [--open]
    Write index cost as a WinDirStat saved-results file and verify it. See below.

flp-util export       (--path <store> | --name <index>) --out <file.csv>
                      [--include-folders] [--raw] [--delimiter <c|tab>] [--multi-value-sep <s>]
    Export every indexed item with all metadata to CSV.

Global:
  --quiet             suppress progress reporting (progress goes to stderr regardless)
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

## Per-file index cost (`index cost`)

`TermCount` is a poor proxy for index space: it tracks total term *occurrences*, so it overstates
repetitive files, understates high-cardinality ones (GUIDs, hashes, minified JS), and ignores the
flat per-document floor entirely. `index cost` measures the bytes instead.

Each file gets **three buckets rather than one number**, because one of them genuinely cannot be
divided:

- **Exclusive** — bytes that exist solely because of this file and vanish with it: its positions
  (`.prx`), its posting entries (`.frq`), its stored fields (`.fdt`/`.fdx`), its norms (`.nrm`), and
  the dictionary entries for terms no other file has.
- **Shared** — term-dictionary entries it holds jointly with other files. A term's text is written
  **once** regardless of how many documents contain it, so removing any one holder saves nothing.
  This is reported *whole*, with a co-owner count, and never divided — splitting it N ways would be
  arithmetic dressed up as measurement.
- **Belongs to no file** — per-term skip lists, `.tii`, `.fnm`, per-segment file headers.

### Why the numbers are trustworthy

Every byte is derived from the Lucene 3.0 encoding ([`LuceneFormat`](src/FlpUtil/Index/LuceneFormat.cs)),
not estimated — `.frq` folds `freq == 1` into the doc-delta's low bit, `.prx` deltas restart per
document, `.tis` elides the prefix shared with the previous term. The command then compares its
computed total against each segment file's **actual** length and prints the difference:

```
segment                    actual       computed     residual
.prx  positions         3,027,154      3,027,154            0   exact
.frq  postings            916,033        822,855       93,178   +10.17% = per-term skip lists
.fdt+.fdx stored        1,505,437      1,505,421           16   +0.00% = per-segment file headers
.nrm  norms               210,093        210,085            8   +0.00% = per-segment file headers
.tis  dictionary          980,370        979,972          398   +0.04% = file header + skip pointers
.tii  dict index           13,121              0       13,121   index-wide, not per-file
.fnm  field infos             276              0          276   index-wide, not per-file

Accounting:
  exclusive to one owner             6,220,076    93.5%
  shared term dictionary               325,411     4.9%   (joint - not divided)
  belongs to no file                   106,997     1.6%   (skip lists, .tii, .fnm, headers)
  actual index size                  6,652,484   balances exactly
```

`.prx` — 45% of the index — reconciles to **zero**. No residual is left unexplained: the `.frq`
leftover is identified by counting skip-list entries (27,762 entries at 3.4 bytes each), and the
`.fdt`/`.nrm` residuals are exactly 4 bytes per segment file header. The books balance to the byte,
so no cost is hidden in a rounding.

Deleted documents get their own row — they occupy every byte they ever did until a merge drops them.

### Caveats

- A file's exclusive bytes are its share *of the index as it stands*. They are not a perfect
  counterfactual: `.frq` doc-deltas and `.tis` prefixes are relative to neighbours, so deleting a
  file would shift a few bytes onto its neighbour.
- Per-document accumulators are held in memory (six arrays of `maxDoc`), and the analysis walks
  every term's posting list once.

## Per-folder cost, and the WinDirStat view

`index cost --by-folder` ranks folders by what their whole subtree costs, which is the view that
answers *"which folder should I stop indexing?"* — a tree of huge log files full of near-random
tokens is expensive to index and worthless to search.

For navigating that interactively, `index treemap` writes a **WinDirStat saved-results file**, so
WinDirStat's tree/list view and treemap can be pointed at index cost instead of disk usage:

```
flp-util index treemap --name "my-index" --out index-cost.wds.csv --open
WinDirStat.exe /loadfrom index-cost.wds.csv
```

- `Logical Size` = exclusive bytes (what excluding it reclaims); `Physical Size` = exclusive plus the
  item's apportioned share of the joint dictionary. WinDirStat's logical/physical treemap option
  toggles which drives the view, so both live in one file.
- Each folder gets a synthetic `<folder entry>` leaf holding its own index documents, so **every
  folder's size equals the sum of its children exactly**.
- The extension pane becomes an instant breakdown of cost by file type.

### The WinDirStat results format

Reverse-engineered from `windirstat/CsvLoader.cpp`, `Item.h`, `Constants.h` and
`res/langs/lang_en.txt` at tag `release/v2.7.0`; the load path is byte-identical to `master`.
Implemented in [`WinDirStatFormat`](src/FlpUtil/Export/WinDirStatFormat.cs) /
[`WinDirStatWriter`](src/FlpUtil/Export/WinDirStatWriter.cs).

UTF-8, CRLF, no BOM needed (2.7+ skips one if present). Header — all nine required, matched by name
so order is free:

```
"Name","Files","Folders","Logical Size","Physical Size","Attributes","Last Change","WinDirStat Attributes","Index"
"C:\dir\file.txt",0,0,1438,1438,"A",2026-07-11T12:58:47Z,0x00000008,0x0000000000000000
```

| Rule | Why it matters |
|---|---|
| `Name` is the **full path** | WinDirStat splits at the last `\`: prefix = parent lookup key, suffix = display name. Root and drive rows use the whole value as the display name. |
| **Parents before children** | The parent is looked up in a map built from earlier rows. A child listed first is silently **dropped**. |
| **Containers need `Files + Folders > 0`** | `GetItemsCount() > 0` gates parent registration. A folder reporting zero items becomes a dead end and everything beneath it is dropped. |
| **Sizes are not aggregated on load** | `AddChild(child, addOnly: true)` skips the upward size propagation, so every folder row must carry its own subtree total. |
| **Only drives may be children of the root** | An `IT_MYCOMPUTER` root with a file or directory child **crashes WinDirStat** with an access violation. |
| `WinDirStat Attributes` is the `ITEMTYPE` hex | `0x10000001` root, `0x00000002` drive, `0x00000004` directory, `0x00000008` file. |
| `Attributes` is a subset of `RHSACEOZ` | Reparse `@` is deliberately absent — WinDirStat does not emit it either. |
| Quoted fields end at the next `"` | There is no escaped-quote handling, so a value containing `"` corrupts every later column. Windows paths cannot contain one, and quoting `Name` is required because paths may contain commas. |
| `Last Change` is `YYYY-MM-DDTHH:MM:SSZ` | Anything else silently decodes to the zero FILETIME, which displays as 1601. |

That last-but-one rule was found the hard way. WinDirStat gives **no feedback** on a bad file — it
either opens empty or segfaults — so `index treemap` always re-reads its own output through
[`WinDirStatValidator`](src/FlpUtil/Export/WinDirStatValidator.cs), a deliberate port of
WinDirStat's load rules (including its quirky field splitter, because a file only a *correct* CSV
parser can read is a file WinDirStat cannot read). It refuses to report success if any row would be
dropped.

### What the treemap omits

Bytes that belong to no path are left out rather than parked somewhere convenient — they cannot be
reclaimed by excluding a folder, and attaching them to a drive would overstate it:

```
root Physical Size      6,544,194  (folders and files)
omitted                   108,290  (index metadata, deleted documents, skip lists, .tii, .fnm, headers)
actual store size       6,652,484  = root + omitted, exactly
```

## Creating an index

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
| `index cost` accounting | balances to the byte: 6,652,484 computed = 6,652,484 actual |
| `index cost` file set | identical to `export`'s 5,583 paths, zero difference |
| `index cost` `.prx` model | exact, zero residual on 45% of the index |
| `index treemap` folder totals | 1,286 folders cross-checked by path-based summing of `cost.csv` against the id-based tree in the file — 0 mismatches |
| `index treemap` conformance | 8,150 rows, 0 dropped, every folder equals the sum of its children |
| `index treemap` totals | root Physical + omitted = 6,652,484, the real store size |
| Loads in WinDirStat 2.7.0.2722 | yes — verified via `/loadfrom`, tree and treemap populate, 6,862 files, 6.24 MiB physical / 5.93 MiB logical |

### Also verified on a large, multi-segment index

233,954 indexed items → **490,970 documents across 6 segments**, a 315 MB store with a shared
compound doc store (`.cfx`):

| Check | Result |
|---|---|
| Whole pipeline | 14 s for analyse + roll-up + write + verify |
| `.prx` model | still **exact** — 0 residual on 55,347,629 bytes |
| `.nrm` residual | 24 bytes = 6 segments × 4-byte header, exactly |
| `.fdt`+`.fdx` residual | 8 bytes = 2 doc-store file headers, exactly |
| `.frq` residual | 3,582,238 = 1,050,173 skip entries at 3.4 bytes — the same rate as the small index |
| Attribution | 98.4% to documents, same as the small index |
| `index treemap` | 257,024 rows, 0 dropped, child sums exact, root + omitted = 329,509,811 = the real store size |
| WinDirStat load | 257k-row / 46 MB file loads and populates |

Two bugs only this index could expose, both now fixed:

- **Loose segments double-counted.** For a non-compound segment the reconciliation listed the whole
  store directory, so every other segment's files were counted again. A single-segment compound index
  hid it entirely.
- **Shared compound doc stores were invisible.** Lucene 3.x may put several segments' stored fields in
  a `<name>.cfx` instead of any segment's `.cfs`. Those were never opened, so `.fdt`/`.fdx` actuals
  read as **zero** against a correct computed figure — which the totals check caught as a 54 MB
  over-count rather than letting it pass.

## Large indexes

Every long pass reports progress, because on a big index these run for minutes:

```
  reading documents: 245,485/490,970 (50.0%) eta 00:02 [107,162/s]
  measuring terms: 3,000,000 [841,673/s] content (field 1/17, segment 4/6)
  writing rows: 128,512/257,024 (50.0%) eta 00:00
  verifying: 24,138,752/48,277,099 (50.0%) eta 00:00
```

- Progress goes to **stderr**, so `flp-util index cost > report.txt` keeps a clean report while you
  still watch progress — and piping stdout doesn't corrupt anything either.
- On a console it rewrites one line with a rate and an ETA. When stderr is redirected it switches to
  plain milestone lines every 15s, since carriage returns are useless in a log file.
- Updates are rate-limited (200 ms) so reporting never becomes the bottleneck.
- `--quiet` turns it off.

Phases you will see: `reading documents` → `resolving paths` → `measuring terms` (usually the
longest, since it walks every term's posting list and every position) → `writing rows` → `verifying`.

`measuring terms` shows no percentage: Lucene 3.x records a term count for the dictionary as a whole
but not per field, so `Terms.Count` is -1. Rather than invent a percentage it reports which field and
segment is being walked, which is what actually tells you where you are.

### Memory

- `index cost` holds a handful of arrays sized by document count (~50 bytes/document) plus one row
  per distinct path. A document's owning row is stored **by reference**, not as a resolved path
  string per document — an item and its meta document name the same file, so storing strings would
  duplicate every path.
- `export` holds the folder tree plus a meta record per item; rows themselves stream.
- The WinDirStat file is written and verified by **streaming**, never loaded into memory.

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
