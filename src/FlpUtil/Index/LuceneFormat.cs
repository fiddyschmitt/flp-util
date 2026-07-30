namespace FlpUtil.Index;

/// <summary>
/// Byte-exact models of the Lucene 3.0 on-disk encodings we need in order to attribute index bytes
/// to the document that caused them. Everything here is derived from the format specification, and
/// every model is checked against the real segment sizes by
/// <see cref="IndexCostAnalyzer"/>'s reconciliation — a wrong model shows up as an unexplained
/// residual rather than a plausible-looking number.
/// </summary>
public static class LuceneFormat
{
    /// <summary>Default <c>skipInterval</c> for the Lucene 3.x term dictionary.</summary>
    public const int SkipInterval = 16;

    /// <summary>Bytes a Lucene VInt occupies: 7 payload bits per byte, high bit as continuation.</summary>
    public static int VIntLength(long value)
    {
        ulong bits = (ulong)value;
        int length = 1;
        while ((bits & ~0x7FUL) != 0)
        {
            bits >>= 7;
            length++;
        }

        return length;
    }

    /// <summary>
    /// Bytes one posting occupies in <c>.frq</c>.
    ///
    /// With frequencies, the doc delta is shifted left one bit and the low bit flags
    /// <c>freq == 1</c>, in which case the frequency itself is not written. Without frequencies
    /// (DOCS_ONLY) the delta is written plain.
    /// </summary>
    public static int PostingLength(int docDelta, int frequency, bool hasFrequencies)
    {
        if (!hasFrequencies)
            return VIntLength(docDelta);

        int bytes = VIntLength(((long)docDelta << 1) | (frequency == 1 ? 1L : 0L));
        return frequency == 1 ? bytes : bytes + VIntLength(frequency);
    }

    /// <summary>
    /// Bytes one position occupies in <c>.prx</c>. Position deltas restart at zero for every
    /// document. With payloads the delta is shifted left one bit, the low bit flagging that a new
    /// payload length follows.
    /// </summary>
    public static int PositionLength(int positionDelta, bool hasPayloads, int payloadLength, bool payloadLengthChanged)
    {
        if (!hasPayloads)
            return VIntLength(positionDelta);

        int bytes = VIntLength(((long)positionDelta << 1) | (payloadLengthChanged ? 1L : 0L));
        if (payloadLengthChanged)
            bytes += VIntLength(payloadLength);
        return bytes + payloadLength;
    }

    /// <summary>
    /// Bytes one document occupies in <c>.fdt</c>: a field count, then per stored field a field
    /// number, a bits byte, and the value length-prefixed with its UTF-8 byte count.
    /// </summary>
    public static long StoredFieldsLength(IEnumerable<(int FieldNumber, int Utf8Length)> fields)
    {
        long bytes = 0;
        int count = 0;
        foreach (var (fieldNumber, utf8Length) in fields)
        {
            count++;
            bytes += VIntLength(fieldNumber) + 1 + VIntLength(utf8Length) + utf8Length;
        }

        return VIntLength(count) + bytes;
    }

    /// <summary>
    /// Bytes one term occupies in <c>.tis</c>: shared-prefix length, suffix, field number,
    /// document frequency, and the pointer deltas into <c>.frq</c>/<c>.prx</c> — each delta being
    /// the size of the previous term's data in that file. The skip pointer delta is only written
    /// for terms above <see cref="SkipInterval"/>.
    /// </summary>
    public static long TermEntryLength(
        int sharedPrefixLength,
        int suffixByteLength,
        int fieldNumber,
        int docFrequency,
        long previousTermFreqBytes,
        long previousTermProxBytes,
        long previousTermSkipBytes)
    {
        long bytes = VIntLength(sharedPrefixLength)
            + VIntLength(suffixByteLength)
            + suffixByteLength
            + VIntLength(fieldNumber)
            + VIntLength(docFrequency)
            + VIntLength(previousTermFreqBytes)
            + VIntLength(previousTermProxBytes);

        if (docFrequency > SkipInterval)
            bytes += VIntLength(previousTermSkipBytes);

        return bytes;
    }

    /// <summary>
    /// Number of skip entries Lucene writes for a term's posting list. Skip lists are a per-term
    /// acceleration structure interleaved into <c>.frq</c> — they belong to the term, not to any one
    /// document, so counting them is how we prove the leftover <c>.frq</c> bytes are skip data and
    /// not a hole in the model.
    /// </summary>
    public static long SkipEntryCount(int docFrequency, int maxSkipLevels = 10)
    {
        if (docFrequency <= SkipInterval)
            return 0;

        int levels = (int)(Math.Log((double)docFrequency / SkipInterval) / Math.Log(SkipInterval)) + 1;
        levels = Math.Min(levels, maxSkipLevels);

        long entries = 0;
        long stride = SkipInterval;
        for (int level = 0; level < levels; level++)
        {
            entries += docFrequency / stride;
            if (stride > docFrequency)
                break;
            stride *= SkipInterval;
        }

        return entries;
    }

    /// <summary>Length of the byte prefix two adjacent terms share, which <c>.tis</c> elides.</summary>
    public static int SharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        int limit = Math.Min(previous.Length, current.Length);
        int shared = 0;
        while (shared < limit && previous[shared] == current[shared])
            shared++;
        return shared;
    }
}
