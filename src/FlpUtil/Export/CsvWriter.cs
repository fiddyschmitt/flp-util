using System.Text;

namespace FlpUtil.Export;

/// <summary>
/// RFC 4180 CSV writer. UTF-8 and CRLF line endings, so embedded newlines in a quoted field stay
/// unambiguous. A BOM is written by default so Excel opens non-ASCII file names correctly; consumers
/// that predate BOM handling can turn it off.
/// </summary>
public sealed class CsvWriter(Stream stream, char delimiter = ',', bool writeBom = true) : IDisposable
{
    private readonly StreamWriter _writer =
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: writeBom), bufferSize: 1 << 15)
        {
            NewLine = "\r\n",
        };

    public void WriteRow(IEnumerable<string?> fields)
    {
        bool first = true;
        foreach (string? field in fields)
        {
            if (!first)
                _writer.Write(delimiter);
            first = false;
            WriteField(field);
        }

        _writer.Write(_writer.NewLine);
    }

    /// <summary>Same output as <see cref="WriteRow(IEnumerable{string?})"/>, for callers hot enough
    /// to reuse a row buffer instead of allocating one per row.</summary>
    public void WriteRow(ReadOnlySpan<string?> fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                _writer.Write(delimiter);
            WriteField(fields[i]);
        }

        _writer.Write(_writer.NewLine);
    }

    private void WriteField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        bool needsQuotes = value.IndexOf(delimiter) >= 0
            || value.IndexOf('"') >= 0
            || value.IndexOf('\r') >= 0
            || value.IndexOf('\n') >= 0
            // Leading/trailing whitespace is meaningful in file names; quoting preserves it.
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]);

        if (!needsQuotes)
        {
            _writer.Write(value);
            return;
        }

        _writer.Write('"');
        _writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        _writer.Write('"');
    }

    public void Dispose() => _writer.Dispose();
}
