using System.IO;

namespace ModFramework.Runtime;

/// <summary>Outcome of comparing the content of two assembly files.</summary>
internal enum ContentVerdict
{
    /// <summary>The full byte sequences are identical.</summary>
    Same = 0,

    /// <summary>The files provably differ (different length, or a different byte).</summary>
    Different = 1,

    /// <summary>Equality could not be proven: the comparison failed, short-read or the file changed.</summary>
    Unverifiable = 2,
}

/// <summary>
/// Decides whether two assembly files have identical content.
///
/// The security rule (Phase 1.1.1, see <c>docs/audit/phase1.1.1-report.md</c> §B) is that a
/// digest may only ever be used as a cheap <b>negative</b> filter:
/// <list type="bullet">
///   <item>
///     a digest <i>difference</i> is conclusive for <i>any</i> deterministic function of the
///     bytes: identical bytes cannot produce different digests. That direction is safe even
///     without a cryptographic hash.
///   </item>
///   <item>
///     a digest <i>equality</i> is never conclusive: only a full byte comparison decides
///     "same". A weak digest (FNV-1a 64, CRC) is therefore never a sharing permission, and a
///     deliberate collision cannot turn two different files into one shared assembly.
///   </item>
///   <item>
///     when the byte comparison cannot complete (IO error, short read, the file changed while it
///     was being read) the answer is <see cref="ContentVerdict.Unverifiable"/>, which callers
///     must treat as "not shared" - fail closed.
///   </item>
/// </list>
///
/// Memory: two fixed buffers of <see cref="BufferSize"/> bytes; a file is never held whole.
/// </summary>
internal static class ContentIdentity
{
    internal const int BufferSize = 16 * 1024;

    /// <summary>
    /// Test seam: replaces the file opener so IO failures, short reads and mid-comparison size
    /// changes are reachable deterministically. <c>null</c> in production.
    /// </summary>
    internal static Func<string, Stream>? OpenForTests;

    /// <summary>
    /// Compares two files byte by byte. <paramref name="leftLength"/> and
    /// <paramref name="rightLength"/> are the lengths recorded when each claim was read, so a
    /// file that changed after the preflight started is detected instead of being compared
    /// against stale metadata.
    /// </summary>
    internal static ContentVerdict CompareFiles(
        string leftPath,
        long leftLength,
        string rightPath,
        long rightLength,
        out string reason)
    {
        reason = "";
        if (leftLength != rightLength)
        {
            reason = "the files have different lengths (" + leftLength + " vs " + rightLength + ")";
            return ContentVerdict.Different;
        }
        if (leftLength < 0)
        {
            reason = "a recorded length is invalid";
            return ContentVerdict.Unverifiable;
        }
        if (leftLength == 0)
        {
            // Two empty files are identical for content purposes; an empty file cannot be a
            // managed assembly anyway, so this cannot be reached from a successful claim.
            return ContentVerdict.Same;
        }

        Stream? left = null;
        Stream? right = null;
        try
        {
            left = Open(leftPath);
            right = Open(rightPath);
            if (left.Length != leftLength || right.Length != rightLength)
            {
                reason = "a file changed size while it was being compared";
                return ContentVerdict.Unverifiable;
            }

            var bufferLeft = new byte[BufferSize];
            var bufferRight = new byte[BufferSize];
            var remaining = leftLength;
            while (remaining > 0)
            {
                var want = (int)(remaining < BufferSize ? remaining : BufferSize);
                var readLeft = ReadFully(left, bufferLeft, want);
                var readRight = ReadFully(right, bufferRight, want);
                if (readLeft != want || readRight != want)
                {
                    reason = "a file ended before its recorded length";
                    return ContentVerdict.Unverifiable;
                }
                for (var i = 0; i < want; i++)
                {
                    if (bufferLeft[i] != bufferRight[i])
                    {
                        reason = "the contents differ at offset " + (leftLength - remaining + i);
                        return ContentVerdict.Different;
                    }
                }
                remaining -= want;
            }

            // No extra byte may remain on either side.
            if (left.ReadByte() != -1 || right.ReadByte() != -1)
            {
                reason = "a file grew while it was being compared";
                return ContentVerdict.Unverifiable;
            }
            return ContentVerdict.Same;
        }
        catch (Exception ex)
        {
            reason = PathScrubber.Describe(ex);
            return ContentVerdict.Unverifiable;
        }
        finally
        {
            Close(left);
            Close(right);
        }
    }

    /// <summary>
    /// Proves that a file holds exactly <paramref name="expected"/>, byte for byte (Phase 1.1.2).
    ///
    /// Used right after the snapshot is written: the buffer that was parsed and hashed by the
    /// preflight is still in memory, so "the staged copy is the preflighted content" is decided by
    /// a full comparison rather than by a digest.
    /// </summary>
    internal static bool VerifyExactBytes(string path, byte[] expected, out string error)
    {
        error = "";
        Stream? stream = null;
        try
        {
            // Deliberately not Open(): this compares a file the loader itself just created, so the
            // test seam (which models a third party changing a declared file) must not apply.
            stream = File.OpenRead(path);
            if (stream.Length != expected.Length)
            {
                error = "the file is " + stream.Length + " bytes, expected " + expected.Length;
                return false;
            }

            var buffer = new byte[BufferSize];
            var offset = 0;
            while (offset < expected.Length)
            {
                var want = expected.Length - offset < BufferSize ? expected.Length - offset : BufferSize;
                var read = ReadFully(stream, buffer, want);
                if (read != want)
                {
                    error = "the file ended before its recorded length";
                    return false;
                }
                for (var i = 0; i < want; i++)
                {
                    if (buffer[i] != expected[offset + i])
                    {
                        error = "the contents differ at offset " + (offset + i);
                        return false;
                    }
                }
                offset += want;
            }

            if (stream.ReadByte() != -1)
            {
                error = "the file has more bytes than expected";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = PathScrubber.Describe(ex);
            return false;
        }
        finally
        {
            Close(stream);
        }
    }

    /// <summary>
    /// Length of an existing file, or a path-free reason why it could not be read. Needed for
    /// the host image, whose length was not recorded by the preflight.
    /// </summary>
    internal static bool TryGetLength(string path, out long length, out string reason)
    {
        length = 0;
        reason = "";
        Stream? stream = null;
        try
        {
            stream = Open(path);
            length = stream.Length;
            if (length < 0)
            {
                reason = "the file reports a negative length";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = PathScrubber.Describe(ex);
            return false;
        }
        finally
        {
            Close(stream);
        }
    }

    private static Stream Open(string path)
    {
        var open = OpenForTests;
        return open != null ? open(path) : File.OpenRead(path);
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static void Close(Stream? stream)
    {
        if (stream == null)
        {
            return;
        }
        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
            // A failed close cannot change the verdict that was already reached.
        }
    }
}
