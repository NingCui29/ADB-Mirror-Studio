namespace AdbMirrorStudio.Infrastructure.Processes;

internal static class BoundedTextTailReader
{
    public static async Task<string> ReadAsync(
        TextReader reader,
        int maxCharacters = 65_536,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        var readBuffer = new char[Math.Min(4096, maxCharacters)];
        var tail = new char[maxCharacters];
        var writeIndex = 0;
        var stored = 0;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A descendant may retain a pipe after its parent exits. Preserve
                // the diagnostics already received when the caller stops draining.
                break;
            }
            if (read == 0) break;

            var sourceOffset = 0;
            if (read >= maxCharacters)
            {
                sourceOffset = read - maxCharacters;
                read = maxCharacters;
                writeIndex = 0;
                stored = 0;
            }

            var firstCopy = Math.Min(read, maxCharacters - writeIndex);
            Array.Copy(readBuffer, sourceOffset, tail, writeIndex, firstCopy);
            var secondCopy = read - firstCopy;
            if (secondCopy > 0) Array.Copy(readBuffer, sourceOffset + firstCopy, tail, 0, secondCopy);
            writeIndex = (writeIndex + read) % maxCharacters;
            stored = Math.Min(maxCharacters, stored + read);
        }

        if (stored < maxCharacters) return new string(tail, 0, stored);
        return string.Concat(
            new string(tail, writeIndex, maxCharacters - writeIndex),
            new string(tail, 0, writeIndex));
    }
}
