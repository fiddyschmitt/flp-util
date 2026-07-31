using System.Text;
using FlpUtil.Cli;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace FlpUtil.Index;

/// <summary>Bytes attributed to one owner (a file, a folder, or the index itself).</summary>
public sealed record CostRow
{
    public required string Owner { get; init; }
    public bool IsFolder { get; set; }
    public int DocCount { get; set; }

    /// <summary>Stored field data and its index (<c>.fdt</c> + <c>.fdx</c>).</summary>
    public long StoredBytes { get; set; }

    /// <summary>Norms (<c>.nrm</c>) — a flat cost per document per indexed field.</summary>
    public long NormBytes { get; set; }

    /// <summary>Posting entries in <c>.frq</c> for every term this owner holds.</summary>
    public long PostingBytes { get; set; }

    /// <summary>Term positions in <c>.prx</c>.</summary>
    public long PositionBytes { get; set; }

    /// <summary>Dictionary entries (<c>.tis</c>) for terms <em>only</em> this owner has.</summary>
    public long SoleTermBytes { get; set; }

    /// <summary>
    /// Dictionary entries this owner shares with others, counted in full rather than divided.
    /// Summing this column across owners deliberately exceeds the real shared total — each
    /// co-owner sees the whole entry, because that is what it would take to remove it.
    /// </summary>
    public long SharedTermBytes { get; set; }

    public int SharedTermCount { get; set; }

    /// <summary>
    /// This owner's share of the joint dictionary entries: each entry split by its co-owner count,
    /// with the remainder distributed a byte at a time so the column sums to
    /// <see cref="IndexCostReport.SharedDictionaryBytes"/> exactly. Unlike
    /// <see cref="SharedTermBytes"/> this is an apportionment, not a removal cost.
    /// </summary>
    public long SharedTermBytesShare { get; set; }

    /// <summary>Bytes that exist solely because of this owner, and vanish with it.</summary>
    public long ExclusiveBytes =>
        StoredBytes + NormBytes + PostingBytes + PositionBytes + SoleTermBytes;

    /// <summary>Exclusive bytes plus this owner's apportioned share of the joint dictionary.</summary>
    public long ApportionedBytes => ExclusiveBytes + SharedTermBytesShare;

    /// <summary>
    /// This folder's own <c>fldrid</c>, set only from a folder-tree document. Empty for files.
    /// Kept separate from <see cref="ParentFolderId"/> because a directory has both — its own id and
    /// the id of the folder containing it — and conflating them would corrupt the hierarchy.
    /// </summary>
    public string OwnFolderId { get; set; } = string.Empty;

    /// <summary>Id of the folder containing this owner, from its <c>{fldrid}:{name}</c> item key.</summary>
    public string ParentFolderId { get; set; } = string.Empty;

    /// <summary>Last-modified time recorded in the index, for tools that want to show a date.</summary>
    public DateTime? LastChange { get; set; }

    /// <summary>Raw hex <c>attrx</c> mask as FLP stored it.</summary>
    public string? RawAttributes { get; set; }
}

public sealed record SegmentCheck(string File, long Actual, long Computed)
{
    public long Residual => Actual - Computed;
}

public sealed class IndexCostReport
{
    public required IReadOnlyList<CostRow> Rows { get; init; }
    public required IReadOnlyList<SegmentCheck> Checks { get; init; }
    public required long TotalStoreBytes { get; init; }
    public required int MaxDoc { get; init; }
    public required int NormFieldCount { get; init; }
    public required bool HasPayloads { get; init; }

    /// <summary>Shared dictionary bytes, counted once — the genuinely joint part of the index.</summary>
    public required long SharedDictionaryBytes { get; init; }

    public required long SoleDictionaryBytes { get; init; }

    /// <summary>
    /// Skip-list entries across all terms. Skip lists are interleaved into <c>.frq</c> but belong to
    /// the term rather than any document; this count is what identifies the leftover <c>.frq</c>
    /// bytes as skip data instead of a gap in the model.
    /// </summary>
    public required long SkipEntries { get; init; }

    /// <summary>The folder hierarchy, so callers can roll cost up the tree.</summary>
    public required FolderTree Folders { get; init; }

    /// <summary>
    /// Bytes belonging to no document at all — per-term skip lists, <c>.tii</c>, <c>.fnm</c> and
    /// per-segment file headers. Reported as its own quantity rather than smeared across rows.
    /// </summary>
    public long UnattributedBytes => Checks.Sum(c => c.Actual) - Checks.Sum(c => c.Computed);

    public long ActualStoreBytes => Checks.Sum(c => c.Actual);
}

/// <summary>
/// Attributes index bytes to the file that caused them.
///
/// Most of a Lucene index is cleanly divisible: positions are delta-encoded within each
/// (term, document) pair, each posting is its own VInt, stored fields and norms are per document.
/// The exception is the term dictionary — a term's text is written once no matter how many
/// documents hold it — so those bytes are reported as shared rather than split, and a term's
/// dictionary entry is only charged exclusively when exactly one document has it.
///
/// Every model is verified: computed bytes per segment file are compared against the file's actual
/// length, and the difference is reported as a residual instead of being absorbed.
/// </summary>
public sealed class IndexCostAnalyzer(FlpIndexReader reader, IProgressSink? progress = null)
{
    private readonly IProgressSink _progress = progress ?? NullProgress.Instance;

    private const string IndexOwner = "<index metadata>";
    private const string UnknownOwner = "<unattributed>";
    private const string DeletedOwner = "<deleted documents - reclaimed on optimise>";

    public IndexCostReport Analyze()
    {
        DirectoryReader raw = reader.Raw;
        int maxDoc = raw.MaxDoc;

        // Per-document accumulators, later folded into per-owner rows.
        var storedBytes = new long[maxDoc];
        var normBytes = new long[maxDoc];
        var postingBytes = new long[maxDoc];
        var positionBytes = new long[maxDoc];
        var soleTermBytes = new long[maxDoc];
        var sharedTermBytes = new long[maxDoc];
        var sharedTermShare = new long[maxDoc];
        var sharedTermCount = new int[maxDoc];
        var isFolder = new bool[maxDoc];
        var ownFolderIds = new string[maxDoc];
        var parentFolderIds = new string[maxDoc];
        var lastChange = new DateTime?[maxDoc];
        var rawAttributes = new string?[maxDoc];
        Array.Fill(ownFolderIds, string.Empty);
        Array.Fill(parentFolderIds, string.Empty);

        bool hasPayloads = false;
        long computedStored = 0, computedPostings = 0, computedPositions = 0, computedDictionary = 0;
        long computedNorms = 0, skipEntries = 0;
        long soleDictionary = 0, sharedDictionary = 0;
        int normFieldCount = 0;

        // ---- pass one: stored fields, norms, and the folder tree needed to name owners -------
        var folders = new FolderTree();
        var docKeys = new (DocKind Kind, string Key)[maxDoc];

        // An item's `id` is a lookup key, so on a case-insensitive index FLP stores the name in it
        // case-folded. The `name` field keeps the real casing, but only item documents have it -
        // meta documents carry just `mid`. Mapping key -> real name lets both resolve to the same,
        // correctly-cased path.
        var realNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (IProgressScope docProgress = _progress.Begin("reading documents", maxDoc))
        foreach (AtomicReaderContext leaf in raw.Leaves)
        {
            AtomicReader atomic = leaf.AtomicReader;
            FieldInfos fieldInfos = atomic.FieldInfos;
            var liveDocs = atomic.LiveDocs;

            // Norms are a byte per document per normed field, written per segment — so a segment
            // with fewer normed fields charges less. Counting globally would overstate it.
            int leafNormFields = fieldInfos.Count(f => f.IsIndexed && f.HasNorms);
            normFieldCount = Math.Max(normFieldCount, leafNormFields);

            for (int local = 0; local < atomic.MaxDoc; local++)
            {
                int global = leaf.DocBase + local;
                docProgress.Report(global + 1);

                // Deleted documents still occupy every byte they ever did until a merge drops
                // them, so they are measured and given their own row rather than ignored.
                bool deleted = liveDocs is not null && !liveDocs.Get(local);

                Document document = atomic.Document(local);
                storedBytes[global] = LuceneFormat.StoredFieldsLength(StoredFieldSizes(document, fieldInfos))
                    + 8; // .fdx holds one Int64 offset per document
                normBytes[global] = leafNormFields;
                computedStored += storedBytes[global];
                computedNorms += leafNormFields;

                if (deleted)
                {
                    docKeys[global] = (DocKind.Deleted, string.Empty);
                    continue;
                }

                var doc = ToIndexDoc(global, document);
                if (FlpSchema.IsFolderDoc(doc))
                    folders.Add(doc);
                docKeys[global] = Classify(doc);
                isFolder[global] = IsFolderEvidence(doc);
                (ownFolderIds[global], parentFolderIds[global]) = FolderIdsOf(docKeys[global]);

                if (docKeys[global].Kind == DocKind.Item)
                {
                    if (doc.Get(FlpSchema.ItemName) is { Length: > 0 } realName)
                        realNames[docKeys[global].Key] = realName;

                    // Only item documents carry the file's own metadata; keep enough of it for
                    // downstream tools to show a date and attributes alongside the byte counts.
                    lastChange[global] = FieldDecoders.TryParseFileTime(
                        doc.Get(FlpSchema.Modified), FlpEncoding.DecimalFileTime);
                    rawAttributes[global] = doc.Get(FlpSchema.Attributes);
                }
            }
        }

        // Resolve each document to its owning row up front. Holding a row reference per document
        // rather than a resolved path string per document matters on a large index: an item and its
        // meta document name the same file, so the strings would otherwise be duplicated millions
        // of times over.
        var rows = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);
        var rowByDoc = new CostRow[maxDoc];
        using (IProgressScope resolveProgress = _progress.Begin("resolving paths", maxDoc))
        {
            for (int docId = 0; docId < maxDoc; docId++)
            {
                string owner = ResolveOwner(docKeys[docId], folders, realNames);
                if (!rows.TryGetValue(owner, out CostRow? row))
                    rows[owner] = row = new CostRow { Owner = owner };
                rowByDoc[docId] = row;
                resolveProgress.Report(docId + 1);
            }
        }

        // ---- pass two: postings, positions and the term dictionary --------------------------
        var termDocs = new List<int>();

        // Lucene 3.x records a term count for the dictionary as a whole but not per field, so
        // Terms.Count is -1 and this phase usually has no total. Rather than invent a percentage,
        // report which segment and field is being walked - that is what tells you where you are.
        using IProgressScope termProgress = _progress.Begin("measuring terms", CountTerms(raw));

        // Holds the previous term's bytes for prefix elision. TermsEnum reuses its own buffer, so a
        // copy is unavoidable — but into one growable scratch buffer, not a fresh array per term:
        // dictionaries run to millions of terms.
        byte[] previousTerm = new byte[128];
        int previousTermLength = 0;

        int segmentNumber = 0;
        foreach (AtomicReaderContext leaf in raw.Leaves)
        {
            segmentNumber++;
            AtomicReader atomic = leaf.AtomicReader;
            Fields fields = atomic.Fields;
            if (fields is null)
                continue;

            int fieldCount = fields.Count;
            int fieldNumberInSegment = 0;

            foreach (string fieldName in fields)
            {
                fieldNumberInSegment++;
                Terms terms = fields.GetTerms(fieldName);
                if (terms is null)
                    continue;

                FieldInfo info = atomic.FieldInfos.FieldInfo(fieldName);
                int fieldNumber = info?.Number ?? 0;
                bool hasFreqs = terms.HasFreqs;
                bool hasPositions = terms.HasPositions;
                bool fieldPayloads = terms.HasPayloads;
                hasPayloads |= fieldPayloads;
                termProgress.Detail($"{fieldName} (field {fieldNumberInSegment}"
                    + (fieldCount > 0 ? $"/{fieldCount}" : string.Empty)
                    + $", segment {segmentNumber}/{raw.Leaves.Count})");

                // .tis elides the prefix shared with the previous term, and resets at field
                // boundaries; the pointer deltas are the previous term's data lengths.
                previousTermLength = 0;
                long previousFreqBytes = 0, previousProxBytes = 0, previousSkipBytes = 0;

                TermsEnum termsEnum = terms.GetEnumerator();
                DocsAndPositionsEnum? positionsEnum = null;
                DocsEnum? docsEnum = null;

                while (termsEnum.MoveNext())
                {
                    var term = termsEnum.Term;
                    int docFrequency = termsEnum.DocFreq;
                    skipEntries += LuceneFormat.SkipEntryCount(docFrequency);
                    termProgress.Tick();

                    termDocs.Clear();
                    long termFreqBytes = 0, termProxBytes = 0;

                    if (hasPositions)
                    {
                        positionsEnum = termsEnum.DocsAndPositions(null, positionsEnum);
                        docsEnum = positionsEnum;
                    }
                    else
                    {
                        docsEnum = termsEnum.Docs(null, docsEnum, hasFreqs ? DocsFlags.FREQS : DocsFlags.NONE);
                        positionsEnum = null;
                    }

                    int lastDoc = 0;
                    while (docsEnum!.NextDoc() != DocIdSetIterator.NO_MORE_DOCS)
                    {
                        int local = docsEnum.DocID;
                        int global = leaf.DocBase + local;
                        int frequency = hasFreqs ? docsEnum.Freq : 1;

                        int frqBytes = LuceneFormat.PostingLength(local - lastDoc, frequency, hasFreqs);
                        lastDoc = local;
                        postingBytes[global] += frqBytes;
                        termFreqBytes += frqBytes;

                        if (positionsEnum is not null)
                        {
                            long prxBytes = MeasurePositions(positionsEnum, frequency, fieldPayloads);
                            positionBytes[global] += prxBytes;
                            termProxBytes += prxBytes;
                        }

                        termDocs.Add(global);
                    }

                    computedPostings += termFreqBytes;
                    computedPositions += termProxBytes;

                    int prefix = LuceneFormat.SharedPrefixLength(
                        previousTerm.AsSpan(0, previousTermLength),
                        term.Bytes.AsSpan(term.Offset, term.Length));
                    long entryBytes = LuceneFormat.TermEntryLength(
                        prefix, term.Length - prefix, fieldNumber, docFrequency,
                        previousFreqBytes, previousProxBytes, previousSkipBytes);
                    computedDictionary += entryBytes;

                    if (termDocs.Count == 1)
                    {
                        soleTermBytes[termDocs[0]] += entryBytes;
                        soleDictionary += entryBytes;
                    }
                    else
                    {
                        // Genuinely joint: the term text exists once. Charge every co-owner the
                        // full entry, and record the real total separately so nothing is
                        // double-counted at the index level.
                        sharedDictionary += entryBytes;

                        // Alongside that, an apportionment that does sum to the real total: an equal
                        // integer split, with the remainder handed out a byte at a time so no byte
                        // is invented or lost to rounding.
                        long each = entryBytes / termDocs.Count;
                        long remainder = entryBytes % termDocs.Count;

                        for (int i = 0; i < termDocs.Count; i++)
                        {
                            int docId = termDocs[i];
                            sharedTermBytes[docId] += entryBytes;
                            sharedTermShare[docId] += each + (i < remainder ? 1 : 0);
                            sharedTermCount[docId]++;
                        }
                    }

                    if (previousTerm.Length < term.Length)
                        previousTerm = new byte[Math.Max(previousTerm.Length * 2, term.Length)];
                    term.Bytes.AsSpan(term.Offset, term.Length).CopyTo(previousTerm);
                    previousTermLength = term.Length;

                    previousFreqBytes = termFreqBytes;
                    previousProxBytes = termProxBytes;
                    previousSkipBytes = 0;
                }
            }
        }

        // ---- fold per-document numbers into per-owner rows ----------------------------------
        for (int docId = 0; docId < maxDoc; docId++)
        {
            CostRow row = rowByDoc[docId];

            // A directory contributes several documents - a folder-tree entry plus item and meta
            // documents - and they can arrive in any order, so folder-ness is OR'd in rather than
            // taken from whichever document happened to create the row.
            if (isFolder[docId])
                row.IsFolder = true;

            if (ownFolderIds[docId].Length > 0)
                row.OwnFolderId = ownFolderIds[docId];
            if (parentFolderIds[docId].Length > 0)
                row.ParentFolderId = parentFolderIds[docId];
            row.LastChange ??= lastChange[docId];
            row.RawAttributes ??= rawAttributes[docId];

            row.DocCount++;
            row.StoredBytes += storedBytes[docId];
            row.NormBytes += normBytes[docId];
            row.PostingBytes += postingBytes[docId];
            row.PositionBytes += positionBytes[docId];
            row.SoleTermBytes += soleTermBytes[docId];
            row.SharedTermBytes += sharedTermBytes[docId];
            row.SharedTermBytesShare += sharedTermShare[docId];
            row.SharedTermCount += sharedTermCount[docId];
        }

        var (checks, totalStore) = Reconcile(
            raw, computedStored, computedPostings, computedPositions, computedDictionary, computedNorms);

        return new IndexCostReport
        {
            Rows = [.. rows.Values.OrderByDescending(r => r.ExclusiveBytes)],
            Checks = checks,
            TotalStoreBytes = totalStore,
            MaxDoc = maxDoc,
            NormFieldCount = normFieldCount,
            HasPayloads = hasPayloads,
            SharedDictionaryBytes = sharedDictionary,
            SoleDictionaryBytes = soleDictionary,
            SkipEntries = skipEntries,
            Folders = folders,
        };
    }

    /// <summary>
    /// Total number of terms, so the term pass can show a percentage. The Lucene 3.x dictionary
    /// records its term count, but <c>Terms.Count</c> is allowed to return -1; when any field
    /// declines to answer we report progress without a total rather than inventing one.
    /// </summary>
    private static long CountTerms(DirectoryReader raw)
    {
        long total = 0;
        foreach (AtomicReaderContext leaf in raw.Leaves)
        {
            Fields fields = leaf.AtomicReader.Fields;
            if (fields is null)
                continue;

            foreach (string field in fields)
            {
                Terms terms = fields.GetTerms(field);
                if (terms is null)
                    continue;
                if (terms.Count < 0)
                    return -1;
                total += terms.Count;
            }
        }

        return total;
    }

    /// <summary>Walks one document's positions, measuring the bytes they occupy in <c>.prx</c>.</summary>
    private static long MeasurePositions(DocsAndPositionsEnum positions, int frequency, bool hasPayloads)
    {
        long bytes = 0;
        int lastPosition = 0;
        int lastPayloadLength = -1;

        for (int i = 0; i < frequency; i++)
        {
            int position = positions.NextPosition();
            if (position < 0)
                break;

            int payloadLength = 0;
            if (hasPayloads && positions.GetPayload() is { } payload)
                payloadLength = payload.Length;

            bool payloadLengthChanged = hasPayloads && payloadLength != lastPayloadLength;
            bytes += LuceneFormat.PositionLength(position - lastPosition, hasPayloads, payloadLength, payloadLengthChanged);

            lastPosition = position;
            lastPayloadLength = payloadLength;
        }

        return bytes;
    }

    /// <summary>
    /// Byte length of each stored value as <c>.fdt</c> holds it: text is length-prefixed UTF-8,
    /// binary keeps its own length. Falling back to the string form for numerics matches how
    /// Lucene 3.0 wrote them.
    /// </summary>
    private static IEnumerable<(int FieldNumber, int Utf8Length)> StoredFieldSizes(Document document, FieldInfos fieldInfos)
    {
        foreach (IIndexableField field in document.Fields)
        {
            int number = fieldInfos.FieldInfo(field.Name)?.Number ?? 0;

            if (field.GetStringValue() is { } text)
            {
                yield return (number, Encoding.UTF8.GetByteCount(text));
                continue;
            }

            if (field.GetBinaryValue() is { } binary)
            {
                yield return (number, binary.Length);
                continue;
            }

            yield return (number, 0);
        }
    }

    /// <summary>
    /// Compares each computed total against the segment file's real length. A correct model leaves
    /// a residual of zero for <c>.prx</c>/<c>.fdx</c>/<c>.nrm</c>, and only per-term skip lists and
    /// file headers elsewhere.
    /// </summary>
    private (IReadOnlyList<SegmentCheck> Checks, long Total) Reconcile(
        DirectoryReader raw,
        long stored, long postings, long positions, long dictionary, long norms)
    {
        var actual = new Dictionary<string, long>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        void CountFile(LuceneDirectory source, string file)
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".fdt" or ".fdx" or ".nrm" or ".frq" or ".prx" or ".tis" or ".tii" or ".fnm"))
                return;

            // A shared doc store is referenced by several segments, so names are deduplicated.
            if (!counted.Add(file))
                return;

            long length = source.FileLength(file);
            actual[extension] = actual.GetValueOrDefault(extension) + length;
            total += length;
        }

        foreach (AtomicReaderContext leaf in raw.Leaves)
        {
            if (leaf.Reader is not SegmentReader segment)
                continue;

            SegmentInfo info = segment.SegmentInfo.Info;
            LuceneDirectory source = reader.Store;
            CompoundFileDirectory? compound = null;

            try
            {
                if (info.UseCompoundFile)
                {
                    compound = new CompoundFileDirectory(source, info.Name + ".cfs", IOContext.READ_ONCE, false);
                    source = compound;
                }

                // For a compound segment `source` is that segment's .cfs, so every entry belongs to
                // it. For a loose segment `source` is the whole store, which also holds every other
                // segment's files - so restrict to this segment's own prefix or the totals would be
                // counted once per segment.
                string prefix = info.Name + ".";

                foreach (string file in source.ListAll())
                {
                    if (!info.UseCompoundFile && !file.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    CountFile(source, file);
                }
            }
            finally
            {
                compound?.Dispose();
            }
        }

        // Lucene 3.x lets several segments share one doc store, and when it is compound the stored
        // fields live in <name>.cfx rather than in any segment's .cfs. Missing these makes the
        // .fdt/.fdx actuals read as zero while the computed figure is right, so they have to be
        // opened separately. Lucene deletes unreferenced files, so every .cfx present is in use.
        foreach (string file in reader.Store.ListAll())
        {
            if (!file.EndsWith(".cfx", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var docStore = new CompoundFileDirectory(reader.Store, file, IOContext.READ_ONCE, false);
                foreach (string entry in docStore.ListAll())
                    CountFile(docStore, entry);
            }
            catch (IOException)
            {
                // An unreadable doc store shows up as a residual rather than being silently ignored.
            }
        }

        // .fdx is folded into the stored-field figure (8 bytes per document, measured above).
        long storedActual = actual.GetValueOrDefault(".fdt") + actual.GetValueOrDefault(".fdx");
        long dictActual = actual.GetValueOrDefault(".tis");

        return (
        [
            new SegmentCheck(".prx  positions", actual.GetValueOrDefault(".prx"), positions),
            new SegmentCheck(".frq  postings", actual.GetValueOrDefault(".frq"), postings),
            new SegmentCheck(".fdt+.fdx stored", storedActual, stored),
            new SegmentCheck(".nrm  norms", actual.GetValueOrDefault(".nrm"), norms),
            new SegmentCheck(".tis  dictionary", dictActual, dictionary),
            new SegmentCheck(".tii  dict index", actual.GetValueOrDefault(".tii"), 0),
            new SegmentCheck(".fnm  field infos", actual.GetValueOrDefault(".fnm"), 0),
        ], total);
    }

    private enum DocKind { Deleted, Item, Meta, Folder, IndexMeta, Other }

    /// <summary>
    /// Whether a document says its owner is a directory: a folder-tree entry always is, and an item
    /// document is judged the same way <see cref="Commands.ExportCommand"/> judges it, so the two
    /// commands agree on what counts as a file.
    /// </summary>
    /// <summary>
    /// Splits a document's folder relationship into (own id, parent id). A folder-tree document
    /// declares the folder's own id; item and meta documents carry the id of the folder containing
    /// them, embedded in their <c>{fldrid}:{name}</c> key.
    /// </summary>
    private static (string Own, string Parent) FolderIdsOf((DocKind Kind, string Key) doc) => doc.Kind switch
    {
        DocKind.Folder => (doc.Key, string.Empty),
        DocKind.Item or DocKind.Meta => (string.Empty, FlpSchema.SplitItemId(doc.Key).FolderId),
        _ => (string.Empty, string.Empty),
    };

    private static bool IsFolderEvidence(IndexDoc doc)
    {
        if (FlpSchema.IsFolderDoc(doc))
            return true;

        return doc.Get(FlpSchema.ItemType) == FlpSchema.ItemTypeFolder
            || FieldDecoders.HasAttribute(doc.Get(FlpSchema.Attributes), FileAttributes.Directory);
    }

    private static (DocKind, string) Classify(IndexDoc doc)
    {
        if (FlpSchema.IsIndexDoc(doc))
            return (DocKind.IndexMeta, string.Empty);
        if (FlpSchema.IsFolderDoc(doc))
            return (DocKind.Folder, doc.Get(FlpSchema.FolderId) ?? string.Empty);
        if (FlpSchema.IsItemDoc(doc))
            return (DocKind.Item, doc.Get(FlpSchema.ItemId) ?? string.Empty);
        if (FlpSchema.IsMetaDoc(doc))
            return (DocKind.Meta, doc.Get(FlpSchema.MetaId) ?? string.Empty);
        return (DocKind.Other, string.Empty);
    }

    /// <summary>
    /// Item and meta documents share the key <c>{folderId}:{name}</c>, so both roll up to the same
    /// file — which is what makes a per-file total meaningful.
    /// </summary>
    private static string ResolveOwner(
        (DocKind Kind, string Key) doc,
        FolderTree folders,
        Dictionary<string, string> realNames)
    {
        switch (doc.Kind)
        {
            case DocKind.Deleted:
                return DeletedOwner;

            case DocKind.IndexMeta:
                return IndexOwner;

            case DocKind.Folder:
                return folders.ResolveFolderPath(doc.Key);

            case DocKind.Item:
            case DocKind.Meta:
            {
                (string folderId, string keyName) = FlpSchema.SplitItemId(doc.Key);
                string name = realNames.GetValueOrDefault(doc.Key, keyName);
                return folders.ResolveItemPath(folderId, name);
            }

            default:
                return UnknownOwner;
        }
    }

    private static IndexDoc ToIndexDoc(int docId, Document document)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (IIndexableField field in document.Fields)
        {
            if (!fields.TryGetValue(field.Name, out var values))
                fields[field.Name] = values = [];
            values.Add(field.GetStringValue() ?? string.Empty);
        }

        return new IndexDoc { DocId = docId, Fields = fields };
    }
}
