using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// ECMA-335 II.23.1.2 <c>Assembly.Flags</c>.
/// </summary>
internal static class AssemblyFlagBits
{
    /// <summary>
    /// Bit 0x0001. When set, the <c>Assembly.PublicKey</c> blob holds a <b>full public key</b>;
    /// when clear it holds a <b>public key token</b>. Treating every non-empty blob as a full key
    /// is the defect this bit exists to prevent.
    /// </summary>
    internal const int PublicKey = 0x0001;

    /// <summary>Bits 0x0E00: the assembly contains Windows Runtime content.</summary>
    internal const int ContentTypeMask = 0x0E00;
}

/// <summary>
/// Assembly identity read straight out of the file bytes, without loading the assembly.
///
/// This exists because the shipped runtime does <b>not</b> contain
/// <c>AssemblyName.GetAssemblyName(string)</c>: verified by compiling against the game's own
/// 65 reference assemblies (see <c>docs/audit/phase1.1-report.md</c> §3). Loading the file to
/// get its name would defeat the whole point of a preflight, because
/// <c>Assembly.LoadFrom</c> is exactly the global side effect the preflight must run before.
///
/// What is read: PE headers, the CLI header, the metadata root and the ECMA-335
/// <c>Assembly</c> table row 0 (flags, name, version, culture, public key). Nothing is executed
/// and no static constructor can run.
/// </summary>
internal sealed class AssemblyFileIdentity
{
    internal string Name = "";

    internal int Major;

    internal int Minor;

    internal int Build;

    internal int Revision;

    /// <summary>Culture name; empty means neutral.</summary>
    internal string Culture = "";

    /// <summary>Raw ECMA-335 <c>Assembly.Flags</c> value (see <see cref="AssemblyFlagBits"/>).</summary>
    internal int Flags;

    /// <summary>
    /// Full public key, present only when <see cref="AssemblyFlagBits.PublicKey"/> is set in
    /// <see cref="Flags"/> and the blob is non-empty.
    /// </summary>
    internal byte[] PublicKey = Array.Empty<byte>();

    /// <summary>
    /// Public key <i>token</i>, present only when the flag is clear and the blob is non-empty.
    /// Never populated from a full key: deriving a token needs SHA-1, which this reader does not
    /// use, so a mixed representation is compared as "not proven equal" instead of guessed.
    /// </summary>
    internal byte[] PublicKeyToken = Array.Empty<byte>();

    internal bool IsStrongNamed => PublicKey.Length > 0 || PublicKeyToken.Length > 0;

    internal bool HasFullPublicKey => PublicKey.Length > 0;

    internal string VersionText => Major + "." + Minor + "." + Build + "." + Revision;

    /// <summary>
    /// Deterministic, path-free identity text for the report. It is <b>display only</b>: identity
    /// equality is decided from the raw key bytes in
    /// <see cref="AssemblyIdentityComparison"/>, never from this string.
    /// </summary>
    internal string Describe()
    {
        return Name + ", Version=" + VersionText +
               ", Culture=" + (Culture.Length == 0 ? "neutral" : Culture) +
               ", PublicKey=" + DescribeKey();
    }

    private string DescribeKey()
    {
        if (PublicKey.Length > 0)
        {
            return "full(" + PublicKey.Length + " bytes)";
        }
        if (PublicKeyToken.Length > 0)
        {
            return "token:" + ToHex(PublicKeyToken);
        }
        return "none";
    }

    internal static string ToHex(byte[] value)
    {
        var sb = new StringBuilder(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            sb.Append(value[i].ToString("x2"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Minimal ECMA-335 metadata reader: enough to answer "which assembly is this file?" and
/// "is this file a managed assembly at all?".
///
/// <para><b>Boundary model.</b> Every read is constrained by the container that owns the field,
/// not by the file length. From the outside in:</para>
/// <code>
/// file
///  └─ DOS header / PE signature / COFF header      (must fit the file)
///      └─ optional header                           (must fit the file; its declared size owns the
///          │                                         data-directory array)
///          └─ data directory entry 14               (must exist per NumberOfRvaAndSizes and lie
///              │                                     inside the optional header)
///              └─ section raw data                  (the *whole* request must be file-backed)
///                  └─ CLI header                    (at least the 72-byte IMAGE_COR20_HEADER)
///                      └─ metadata root             (fully file-backed, BSJB signature)
///                          └─ stream header/name    (inside the metadata root)
///                              └─ stream Offset+Size (inside the metadata root)
///                                  ├─ #~ table stream  (row counts, row sizes, the Assembly row)
///                                  └─ #Strings/#Blob   (index, terminator and payload inside the heap)
/// </code>
/// <para>
/// All arithmetic is 64-bit; a negated or overflowing offset can never be mistaken for a valid
/// one. Unsupported or contradictory structures are refused with a stable message instead of a
/// guess, and no message ever contains a host path.
/// </para>
/// </summary>
internal static class ManagedAssemblyReader
{
    /// <summary>
    /// Hard cap on a single declared assembly file. A mod assembly larger than this is
    /// refused rather than buffered: the preflight keeps one file in memory at a time.
    /// </summary>
    internal const int MaxAssemblyBytes = 64 * 1024 * 1024;

    private const uint MetadataSignature = 0x424A5342; // 'BSJB'
    private const int AssemblyTable = 0x20;
    private const int MinOptionalHeaderSize = 2;
    private const int MinCliHeaderSize = 72;
    private const int MaxSections = 96;                // PE/COFF hard limit
    private const int MaxStreamNameBytes = 32;         // ECMA-335: name including NUL
    private const int MaxMetadataVersionBytes = 1024;

    internal static bool TryReadIdentity(byte[] bytes, out AssemblyFileIdentity identity, out string error)
    {
        identity = new AssemblyFileIdentity();
        error = "";

        long metadataOffset;
        long metadataSize;
        if (!TryLocateMetadata(bytes, out metadataOffset, out metadataSize, out error))
        {
            return false;
        }
        return TryReadMetadata(bytes, metadataOffset, metadataSize, out identity, out error);
    }

    // ------------------------------------------------------------------ PE

    private static bool TryLocateMetadata(byte[] bytes, out long metadataOffset, out long metadataSize, out string error)
    {
        metadataOffset = 0;
        metadataSize = 0;
        error = "";

        var fileLength = (long)bytes.Length;
        if (fileLength < 0x40)
        {
            error = "the file is too small to be a PE image";
            return false;
        }
        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            error = "missing the MZ DOS signature";
            return false;
        }

        var peOffset = (long)ReadUInt32(bytes, 0x3C);
        if (peOffset < 0x40 || peOffset + 24 > fileLength)
        {
            error = "the PE header offset is outside the file";
            return false;
        }
        if (bytes[(int)peOffset] != (byte)'P' || bytes[(int)peOffset + 1] != (byte)'E' ||
            bytes[(int)peOffset + 2] != 0 || bytes[(int)peOffset + 3] != 0)        {
            error = "missing the PE signature";
            return false;
        }

        var sectionCount = (int)ReadUInt16(bytes, peOffset + 6);
        var optionalHeaderSize = (int)ReadUInt16(bytes, peOffset + 20);
        var optionalHeader = peOffset + 24;

        if (optionalHeaderSize < MinOptionalHeaderSize)
        {
            error = "the optional header is truncated (SizeOfOptionalHeader=" + optionalHeaderSize + ")";
            return false;
        }
        if (optionalHeader + optionalHeaderSize > fileLength)
        {
            error = "the optional header is outside the file";
            return false;
        }

        var magic = ReadUInt16(bytes, optionalHeader);
        long dataDirectoryInOptional;
        long rvaCountInOptional;
        if (magic == 0x10B)
        {
            dataDirectoryInOptional = 96;
            rvaCountInOptional = 92;
        }
        else if (magic == 0x20B)
        {
            dataDirectoryInOptional = 112;
            rvaCountInOptional = 108;
        }
        else
        {
            error = "unknown PE optional header magic 0x" + magic.ToString("x4");
            return false;
        }

        // The declared optional-header size owns the data-directory array: the count field and
        // the array itself must both be inside it, not merely inside the file.
        if (rvaCountInOptional + 4 > optionalHeaderSize)
        {
            error = "the optional header is too small to hold NumberOfRvaAndSizes";
            return false;
        }
        var numberOfRvaAndSizes = (long)ReadUInt32(bytes, optionalHeader + rvaCountInOptional);
        if (dataDirectoryInOptional + numberOfRvaAndSizes * 8 > optionalHeaderSize)
        {
            error = "the data directory array is outside the optional header";
            return false;
        }

        // Data directory entry 14 is the CLI header, and it must be one of the declared entries.
        const long cliDirectoryIndex = 14;
        if (numberOfRvaAndSizes <= cliDirectoryIndex)
        {
            error = "the optional header declares no CLI data directory " +
                    "(NumberOfRvaAndSizes=" + numberOfRvaAndSizes + ")";
            return false;
        }
        var cliEntry = optionalHeader + dataDirectoryInOptional + cliDirectoryIndex * 8;
        if (cliEntry + 8 > optionalHeader + optionalHeaderSize)
        {
            error = "the CLI data directory entry is outside the optional header";
            return false;
        }

        var cliRva = (long)ReadUInt32(bytes, cliEntry);
        var cliSize = (long)ReadUInt32(bytes, cliEntry + 4);
        if (cliRva == 0 || cliSize == 0)
        {
            error = "the file has no CLI header, so it is not a managed assembly";
            return false;
        }

        // The section table itself must be complete before any RVA can be mapped.
        var sectionTable = optionalHeader + optionalHeaderSize;
        if (sectionCount == 0)
        {
            error = "the file declares no sections";
            return false;
        }
        if (sectionCount > MaxSections)
        {
            error = "the file declares an impossible number of sections (" + sectionCount + ")";
            return false;
        }
        if (sectionTable + (long)sectionCount * 40 > fileLength)
        {
            error = "the section table is outside the file";
            return false;
        }

        long cliOffset;
        string mapError;
        if (!TryMapRva(bytes, sectionTable, sectionCount, cliRva, cliSize, out cliOffset, out mapError))
        {
            error = "the CLI header " + mapError;
            return false;
        }
        if (cliSize < MinCliHeaderSize)
        {
            error = "the CLI header is smaller than the " + MinCliHeaderSize + "-byte IMAGE_COR20_HEADER";
            return false;
        }

        // The data directory's size and the header's own cb field must both cover the header;
        // a truncated cb is how a caller would be tricked into reading fields that are not there.
        var declaredHeaderSize = (long)ReadUInt32(bytes, cliOffset);
        if (declaredHeaderSize < MinCliHeaderSize)
        {
            error = "the CLI header declares itself as " + declaredHeaderSize + " bytes, which is smaller than " +
                    MinCliHeaderSize;
            return false;
        }
        if (declaredHeaderSize > cliSize)
        {
            error = "the CLI header declares " + declaredHeaderSize + " bytes but its data directory entry only maps " +
                    cliSize;
            return false;
        }

        var metadataRva = (long)ReadUInt32(bytes, cliOffset + 8);
        var declinedSize = (long)ReadUInt32(bytes, cliOffset + 12);
        if (metadataRva == 0 || declinedSize == 0)
        {
            error = "the CLI header declares no metadata";
            return false;
        }
        if (!TryMapRva(bytes, sectionTable, sectionCount, metadataRva, declinedSize, out metadataOffset, out mapError))
        {
            error = "the metadata root " + mapError;
            return false;
        }
        if (declinedSize < 20)
        {
            error = "the metadata root is smaller than its fixed header";
            return false;
        }
        if (ReadUInt32(bytes, metadataOffset) != MetadataSignature)
        {
            error = "missing the BSJB metadata signature";
            return false;
        }

        metadataSize = declinedSize;
        return true;
    }

    /// <summary>
    /// Maps a full <c>[rva, rva + size)</c> request to a file offset.
    ///
    /// The whole request must be backed by section <b>raw data</b>: a range that only exists in
    /// the virtual tail of a section (VirtualSize &gt; SizeOfRawData) has no bytes on disk and is
    /// therefore refused, not shortened.
    /// </summary>
    private static bool TryMapRva(
        byte[] bytes,
        long sectionTable,
        int sectionCount,
        long rva,
        long size,
        out long fileOffset,
        out string error)
    {
        fileOffset = 0;
        error = "";
        if (size <= 0)
        {
            error = "has an empty size";
            return false;
        }
        if (rva < 0 || rva > 0xFFFFFFFFL)
        {
            error = "has an out-of-range RVA";
            return false;
        }

        for (var i = 0; i < sectionCount; i++)
        {
            var section = sectionTable + (long)i * 40;
            var virtualSize = (long)ReadUInt32(bytes, section + 8);
            var virtualAddress = (long)ReadUInt32(bytes, section + 12);
            var rawSize = (long)ReadUInt32(bytes, section + 16);
            var rawPointer = (long)ReadUInt32(bytes, section + 20);

            var virtualSpan = virtualSize > rawSize ? virtualSize : rawSize;
            if (rva < virtualAddress || rva >= virtualAddress + virtualSpan)
            {
                continue;
            }

            if (rva + size > virtualAddress + rawSize)
            {
                error = "is not fully backed by section " + i + " raw data";
                return false;
            }

            var offset = rawPointer + (rva - virtualAddress);
            if (offset < 0 || offset + size > bytes.Length)
            {
                error = "is outside the file";
                return false;
            }

            fileOffset = offset;
            return true;
        }

        error = "does not map to any section";
        return false;
    }

    // ------------------------------------------------------------ metadata

    private sealed class Heaps
    {
        internal long StringsOffset = -1;
        internal long StringsSize;
        internal long BlobOffset = -1;
        internal long BlobSize;
        internal int StringIndexSize = 2;
        internal int BlobIndexSize = 2;
        internal int GuidIndexSize = 2;
        internal bool Uncompressed;
    }

    private sealed class TableHeader
    {
        internal readonly long[] RowCount = new long[64];
        internal long RowsStart;
        internal long StreamEnd;
    }

    private static bool TryReadMetadata(
        byte[] bytes,
        long root,
        long rootSize,
        out AssemblyFileIdentity identity,
        out string error)
    {
        identity = new AssemblyFileIdentity();
        error = "";

        var rootEnd = root + rootSize;

        var versionLength = (long)ReadUInt32(bytes, root + 12);
        if (versionLength <= 0 || versionLength > MaxMetadataVersionBytes)
        {
            error = "the metadata version string length is out of range";
            return false;
        }
        var position = root + 16 + versionLength;
        if (position + 4 > rootEnd)
        {
            error = "the metadata stream header is outside the metadata root";
            return false;
        }

        var streamCount = (int)ReadUInt16(bytes, position + 2);
        position += 4;

        var heaps = new Heaps();
        var header = new TableHeader();
        var tablesFound = false;

        for (var i = 0; i < streamCount; i++)
        {
            if (position + 8 > rootEnd)
            {
                error = "a metadata stream header is outside the metadata root";
                return false;
            }
            var streamOffset = (long)ReadUInt32(bytes, position);
            var streamSize = (long)ReadUInt32(bytes, position + 4);
            position += 8;

            var nameStart = position;
            var nameLimit = nameStart + MaxStreamNameBytes;
            if (nameLimit > rootEnd)
            {
                nameLimit = rootEnd;
            }
            var nameEnd = nameStart;
            while (nameEnd < nameLimit && bytes[(int)nameEnd] != 0)
            {
                nameEnd++;
            }
            if (nameEnd >= nameLimit)
            {
                error = "a metadata stream name is not terminated inside the metadata root";
                return false;
            }

            string name;
            string nameError;
            if (!TryDecodeUtf8(bytes, nameStart, (int)(nameEnd - nameStart), out name, out nameError))
            {
                error = "a metadata stream name is not valid UTF-8";
                return false;
            }

            var aligned = Align4(nameEnd + 1);
            if (aligned > rootEnd || aligned < nameStart)
            {
                error = "a metadata stream header is outside the metadata root";
                return false;
            }
            position = aligned;

            // The stream range is relative to the metadata root and must stay inside it.
            if (streamOffset > rootSize || streamSize > rootSize - streamOffset)
            {
                error = "metadata stream \"" + name + "\" extends beyond the metadata root";
                return false;
            }
            var absolute = root + streamOffset;

            if (name == "#~" || name == "#-")
            {
                if (tablesFound)
                {
                    error = "the metadata declares more than one table stream";
                    return false;
                }
                tablesFound = true;
                header.StreamEnd = absolute + streamSize;
                if (!TryReadTableHeader(bytes, absolute, streamSize, heaps, header, out error))
                {
                    return false;
                }
                if (name == "#-")
                {
                    heaps.Uncompressed = true;
                    heaps.StringIndexSize = 4;
                    heaps.GuidIndexSize = 4;
                    heaps.BlobIndexSize = 4;
                }
            }
            else if (name == "#Strings")
            {
                heaps.StringsOffset = absolute;
                heaps.StringsSize = streamSize;
            }
            else if (name == "#Blob")
            {
                heaps.BlobOffset = absolute;
                heaps.BlobSize = streamSize;
            }
        }

        if (!tablesFound)
        {
            error = "the metadata has no #~ table stream";
            return false;
        }

        return TryReadAssemblyRow(bytes, heaps, header, out identity, out error);
    }

    private static bool TryReadTableHeader(
        byte[] bytes,
        long tables,
        long tablesSize,
        Heaps heaps,
        TableHeader header,
        out string error)
    {
        error = "";
        if (tablesSize < 24)
        {
            error = "the #~ table stream is smaller than its fixed header";
            return false;
        }

        var heapSizes = bytes[(int)(tables + 6)];
        heaps.StringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        heaps.GuidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
        heaps.BlobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;

        var tablesEnd = tables + tablesSize;
        var valid = ReadUInt64(bytes, tables + 8);
        var position = tables + 24;
        for (var table = 0; table < 64; table++)
        {
            if ((valid & (1UL << table)) == 0)
            {
                continue;
            }
            if (position + 4 > tablesEnd)
            {
                error = "the #~ row counts are outside the table stream";
                return false;
            }
            // A row count is an unsigned 32-bit value; keep it as long so the product below
            // cannot overflow.
            header.RowCount[table] = (long)ReadUInt32(bytes, position);
            position += 4;
        }

        header.RowsStart = position;
        return true;
    }

    private static bool TryReadAssemblyRow(
        byte[] bytes,
        Heaps heaps,
        TableHeader header,
        out AssemblyFileIdentity identity,
        out string error)
    {
        identity = new AssemblyFileIdentity();
        error = "";

        var rows = header.RowCount[AssemblyTable];
        if (rows == 0)
        {
            error = "the file has no Assembly row (it is a netmodule, not an assembly)";
            return false;
        }
        if (rows != 1)
        {
            error = "the file declares " + rows + " Assembly rows; exactly one is required";
            return false;
        }

        var position = header.RowsStart;
        for (var table = 0; table < AssemblyTable; table++)
        {
            var count = header.RowCount[table];
            if (count == 0)
            {
                continue;
            }
            var rowSize = RowSize(table, heaps, header);
            if (rowSize <= 0)
            {
                error = "unsupported metadata table 0x" + table.ToString("x2");
                return false;
            }
            var advance = (long)rowSize * count;
            if (advance <= 0 || position + advance > header.StreamEnd)
            {
                error = "the #~ table data is truncated (table 0x" + table.ToString("x2") + ")";
                return false;
            }
            position += advance;
        }

        var assemblyRowSize = 16 + heaps.BlobIndexSize + heaps.StringIndexSize + heaps.StringIndexSize;
        if (position + assemblyRowSize > header.StreamEnd)
        {
            error = "the Assembly table row is outside the #~ table stream";
            return false;
        }

        var flags = (int)ReadUInt32(bytes, position + 12);
        var publicKeyIndex = ReadIndex(bytes, position + 16, heaps.BlobIndexSize);
        var nameIndex = ReadIndex(bytes, position + 16 + heaps.BlobIndexSize, heaps.StringIndexSize);
        var cultureIndex = ReadIndex(bytes, position + 16 + heaps.BlobIndexSize + heaps.StringIndexSize, heaps.StringIndexSize);

        string name;
        if (!TryReadString(bytes, heaps, nameIndex, out name, out error))
        {
            return false;
        }
        if (name.Length == 0)
        {
            error = "the Assembly table has an empty name";
            return false;
        }

        string culture;
        if (!TryReadString(bytes, heaps, cultureIndex, out culture, out error))
        {
            return false;
        }

        byte[] publicKeyBlob;
        if (!TryReadBlob(bytes, heaps, publicKeyIndex, out publicKeyBlob, out error))
        {
            return false;
        }

        identity.Major = (int)ReadUInt16(bytes, position + 4);
        identity.Minor = (int)ReadUInt16(bytes, position + 6);
        identity.Build = (int)ReadUInt16(bytes, position + 8);
        identity.Revision = (int)ReadUInt16(bytes, position + 10);
        identity.Flags = flags;
        identity.Name = name;
        identity.Culture = culture == "neutral" ? "" : culture;

        byte[] publicKey;
        byte[] publicKeyToken;
        if (!TryClassifyKeyBlob(flags, publicKeyBlob, out publicKey, out publicKeyToken, out error))
        {
            return false;
        }
        identity.PublicKey = publicKey;
        identity.PublicKeyToken = publicKeyToken;
        return true;
    }

    /// <summary>
    /// ECMA-335 II.23.1.2 <c>Assembly.Flags.PublicKey</c> (0x0001) and II.6.2.1.3 decide what the
    /// <c>Assembly.PublicKey</c> blob <i>is</i>: with the flag set it is the full public key, with
    /// the flag clear it is the 8-byte public key token. Both halves are fixed-size structures, so
    /// a combination that cannot be either is a malformed row, and it is refused instead of being
    /// guessed.
    ///
    /// <para>Exactly four shapes are well formed:</para>
    /// <list type="bullet">
    ///   <item>no flag, empty blob - unsigned assembly;</item>
    ///   <item>no flag, 8-byte blob - the token (<c>PublicKeyToken</c>);</item>
    ///   <item>flag set, blob of 12 or more bytes - a <c>PublicKeyBlob</c>
    ///     (SigAlgID, HashAlgID, cbPublicKey, then the key), kept verbatim and also reduced to its
    ///     token with <see cref="PublicKeyToken.Derive"/> so the two sides are comparable.</item>
    /// </list>
    /// Everything else is rejected: the flag without a blob, the flag with a blob that can only be
    /// a token (8 bytes) or is too short to hold the 12-byte header, and a token blob whose length
    /// is not 8.
    ///
    /// <para><b>Not validated on purpose:</b> the reserved bits of <c>Flags</c>, the
    /// <c>ContentTypeMask</c> bits and the key material itself. The reader only needs the
    /// representation to be consistent; calling a syntactically complete blob "a valid RSA key"
    /// would be a claim it cannot make, and rejecting reserved bits would refuse assemblies the
    /// specification allows.</para>
    /// </summary>
    private static bool TryClassifyKeyBlob(
        int flags,
        byte[] blob,
        out byte[] publicKey,
        out byte[] publicKeyToken,
        out string error)
    {
        publicKey = Array.Empty<byte>();
        publicKeyToken = Array.Empty<byte>();
        error = "";

        var hasKeyFlag = (flags & AssemblyFlagBits.PublicKey) != 0;
        if (blob.Length == 0)
        {
            if (hasKeyFlag)
            {
                error = "the Assembly.PublicKey flag is set but the public key blob is empty";
                return false;
            }
            return true;
        }

        if (hasKeyFlag)
        {
            if (blob.Length == PublicKeyToken.TokenLength)
            {
                error = "the Assembly.PublicKey flag is set but the blob is " + blob.Length +
                        " bytes, which is a public key token, not a full public key";
                return false;
            }
            if (blob.Length < FullPublicKeyHeaderLength)
            {
                error = "the Assembly.PublicKey flag is set but the blob is " + blob.Length +
                        " bytes, too short to be a full public key (" + FullPublicKeyHeaderLength +
                        "-byte header + key)";
                return false;
            }
            publicKey = blob;
            publicKeyToken = PublicKeyToken.Derive(blob);
            return true;
        }

        if (blob.Length != PublicKeyToken.TokenLength)
        {
            error = "the Assembly.PublicKey flag is clear, so the blob must be an " +
                    PublicKeyToken.TokenLength + "-byte public key token, but it is " + blob.Length + " bytes";
            return false;
        }
        publicKeyToken = blob;
        return true;
    }

    /// <summary><c>PublicKeyBlob</c>: SigAlgID (4), HashAlgID (4), cbPublicKey (4).</summary>
    private const int FullPublicKeyHeaderLength = 12;


    private static bool TryReadString(byte[] bytes, Heaps heaps, int index, out string value, out string error)
    {
        value = "";
        error = "";
        if (index == 0)
        {
            return true;
        }
        if (heaps.StringsOffset < 0 || heaps.StringsSize <= 0)
        {
            error = "a #Strings index was requested but the metadata has no #Strings heap";
            return false;
        }
        if (index < 0 || index >= heaps.StringsSize)
        {
            error = "a #Strings index is outside the #Strings heap";
            return false;
        }

        var start = heaps.StringsOffset + index;
        var limit = heaps.StringsOffset + heaps.StringsSize;
        var end = start;
        while (end < limit && bytes[(int)end] != 0)
        {
            end++;
        }
        if (end >= limit)
        {
            error = "a #Strings entry is not terminated inside the #Strings heap";
            return false;
        }

        string decoded;
        string decodeError;
        if (!TryDecodeUtf8(bytes, start, (int)(end - start), out decoded, out decodeError))
        {
            error = "a #Strings entry is not valid UTF-8";
            return false;
        }
        value = decoded;
        return true;
    }

    private static bool TryReadBlob(byte[] bytes, Heaps heaps, int index, out byte[] value, out string error)
    {
        value = Array.Empty<byte>();
        error = "";
        if (index == 0)
        {
            return true;
        }
        if (heaps.BlobOffset < 0 || heaps.BlobSize <= 0)
        {
            error = "a #Blob index was requested but the metadata has no #Blob heap";
            return false;
        }
        if (index < 0 || index >= heaps.BlobSize)
        {
            error = "a #Blob index is outside the #Blob heap";
            return false;
        }

        var heapEnd = heaps.BlobOffset + heaps.BlobSize;
        var position = heaps.BlobOffset + index;
        int length;
        if (!TryReadCompressedUInt(bytes, ref position, heapEnd, out length))
        {
            error = "a #Blob length prefix is invalid";
            return false;
        }
        if (length < 0 || position + length > heapEnd)
        {
            error = "a #Blob entry runs past the end of the #Blob heap";
            return false;
        }
        if (length == 0)
        {
            return true;
        }

        var result = new byte[length];
        Array.Copy(bytes, (int)position, result, 0, length);
        value = result;
        return true;
    }

    private static bool TryReadCompressedUInt(byte[] bytes, ref long position, long limit, out int value)
    {
        value = 0;
        if (position >= limit)
        {
            return false;
        }
        var first = bytes[(int)position];
        if ((first & 0x80) == 0)
        {
            value = first;
            position += 1;
            return true;
        }
        if ((first & 0xC0) == 0x80)
        {
            if (position + 2 > limit)
            {
                return false;
            }
            value = ((first & 0x3F) << 8) | bytes[(int)position + 1];
            position += 2;
            return true;
        }
        if ((first & 0xE0) == 0xC0)
        {
            if (position + 4 > limit)
            {
                return false;
            }
            value = ((first & 0x1F) << 24) | (bytes[(int)position + 1] << 16) |
                    (bytes[(int)position + 2] << 8) | bytes[(int)position + 3];
            position += 4;
            return true;
        }
        return false;
    }

    // -------------------------------------------------------- table layout

    // Coded index kinds, in the order used by Coded().
    private const int TypeDefOrRef = 0;
    private const int HasConstant = 1;
    private const int HasCustomAttribute = 2;
    private const int HasFieldMarshal = 3;
    private const int HasDeclSecurity = 4;
    private const int MemberRefParent = 5;
    private const int HasSemantics = 6;
    private const int MethodDefOrRef = 7;
    private const int MemberForwarded = 8;
    private const int Implementation = 9;
    private const int CustomAttributeType = 10;
    private const int ResolutionScope = 11;

    private static readonly int[][] CodedTables =
    {
        new[] { 0x02, 0x01, 0x1B },                                                     // TypeDefOrRef
        new[] { 0x04, 0x08, 0x17 },                                                     // HasConstant
        new[] { 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11,
                0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B },            // HasCustomAttribute
        new[] { 0x04, 0x08 },                                                           // HasFieldMarshal
        new[] { 0x02, 0x06, 0x20 },                                                     // HasDeclSecurity
        new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B },                                         // MemberRefParent
        new[] { 0x14, 0x17 },                                                           // HasSemantics
        new[] { 0x06, 0x0A },                                                           // MethodDefOrRef
        new[] { 0x04, 0x06 },                                                           // MemberForwarded
        new[] { 0x26, 0x23, 0x27 },                                                     // Implementation
        new[] { 0x06, 0x0A },                                                           // CustomAttributeType
        new[] { 0x00, 0x1A, 0x23, 0x01 },                                               // ResolutionScope
    };

    private static readonly int[] CodedTagBits =
    {
        2, 2, 5, 1, 2, 3, 1, 1, 1, 2, 3, 2,
    };

    private static int RowSize(int table, Heaps heaps, TableHeader header)
    {
        switch (table)
        {
            case 0x00: return 2 + heaps.StringIndexSize + heaps.GuidIndexSize * 3;
            case 0x01: return Coded(ResolutionScope, heaps, header) + heaps.StringIndexSize * 2;
            case 0x02: return 4 + heaps.StringIndexSize * 2 + Coded(TypeDefOrRef, heaps, header) +
                              Simple(0x04, header) + Simple(0x06, header);
            case 0x03: return Simple(0x04, header);
            case 0x04: return 2 + heaps.StringIndexSize + heaps.BlobIndexSize;
            case 0x05: return Simple(0x06, header);
            case 0x06: return 4 + 2 + 2 + heaps.StringIndexSize + heaps.BlobIndexSize + Simple(0x08, header);
            case 0x07: return Simple(0x08, header);
            case 0x08: return 2 + 2 + heaps.StringIndexSize;
            case 0x09: return Simple(0x02, header) + Coded(TypeDefOrRef, heaps, header);
            case 0x0A: return Coded(MemberRefParent, heaps, header) + heaps.StringIndexSize + heaps.BlobIndexSize;
            case 0x0B: return 1 + 1 + Coded(HasConstant, heaps, header) + heaps.BlobIndexSize;
            case 0x0C: return Coded(HasCustomAttribute, heaps, header) + Coded(CustomAttributeType, heaps, header) +
                              heaps.BlobIndexSize;
            case 0x0D: return Coded(HasFieldMarshal, heaps, header) + heaps.BlobIndexSize;
            case 0x0E: return 2 + Coded(HasDeclSecurity, heaps, header) + heaps.BlobIndexSize;
            case 0x0F: return 2 + 4 + Simple(0x02, header);
            case 0x10: return 4 + Simple(0x04, header);
            case 0x11: return heaps.BlobIndexSize;
            case 0x12: return Simple(0x02, header) + Simple(0x14, header);
            case 0x13: return Simple(0x14, header);
            case 0x14: return 2 + heaps.StringIndexSize + Coded(TypeDefOrRef, heaps, header);
            case 0x15: return Simple(0x02, header) + Simple(0x17, header);
            case 0x16: return Simple(0x17, header);
            case 0x17: return 2 + heaps.StringIndexSize + heaps.BlobIndexSize;
            case 0x18: return 2 + Simple(0x06, header) + Coded(HasSemantics, heaps, header);
            case 0x19: return Simple(0x02, header) + Coded(MethodDefOrRef, heaps, header) * 2;
            case 0x1A: return heaps.StringIndexSize;
            case 0x1B: return heaps.BlobIndexSize;
            case 0x1C: return 2 + Coded(MemberForwarded, heaps, header) + heaps.StringIndexSize + Simple(0x1A, header);
            case 0x1D: return 4 + Simple(0x04, header);
            case 0x1E: return 4 + 4;
            case 0x1F: return 4;
            default: return 0;
        }
    }

    private static int Simple(int table, TableHeader header)
    {
        return header.RowCount[table] < 0x10000 ? 2 : 4;
    }

    private static int Coded(int kind, Heaps heaps, TableHeader header)
    {
        var tables = CodedTables[kind];
        var max = 0L;
        for (var i = 0; i < tables.Length; i++)
        {
            var rows = header.RowCount[tables[i]];
            if (rows > max)
            {
                max = rows;
            }
        }
        var limit = 1L << (16 - CodedTagBits[kind]);
        return max < limit ? 2 : 4;
    }

    // ------------------------------------------------------------- primitives

    internal static bool TryDecodeUtf8(byte[] bytes, long start, int count, out string value, out string error)
    {
        value = "";
        error = "";
        if (count < 0 || start < 0 || start + count > bytes.Length)
        {
            error = "the range is outside the file";
            return false;
        }
        if (!IsValidUtf8(bytes, (int)start, count))
        {
            error = "invalid UTF-8";
            return false;
        }
        value = Encoding.UTF8.GetString(bytes, (int)start, count);
        return true;
    }

    /// <summary>
    /// Strict UTF-8 validator. <see cref="Encoding.UTF8"/> silently substitutes U+FFFD for
    /// invalid input, which would turn a corrupt assembly name into a plausible one.
    /// </summary>
    internal static bool IsValidUtf8(byte[] bytes, int start, int count)
    {
        var i = 0;
        while (i < count)
        {
            var first = bytes[start + i];
            if (first < 0x80)
            {
                i++;
                continue;
            }

            int extra;
            var codePoint = 0;
            if ((first & 0xE0) == 0xC0)
            {
                extra = 1;
                codePoint = first & 0x1F;
                if (codePoint < 2)
                {
                    return false; // overlong
                }
            }
            else if ((first & 0xF0) == 0xE0)
            {
                extra = 2;
                codePoint = first & 0x0F;
            }
            else if ((first & 0xF8) == 0xF0)
            {
                extra = 3;
                codePoint = first & 0x07;
            }
            else
            {
                return false;
            }

            if (i + extra > count - 1)
            {
                return false; // truncated sequence
            }

            for (var k = 1; k <= extra; k++)
            {
                var next = bytes[start + i + k];
                if ((next & 0xC0) != 0x80)
                {
                    return false;
                }
                codePoint = (codePoint << 6) | (next & 0x3F);
            }

            if (extra == 2 && codePoint < 0x800)
            {
                return false;
            }
            if (extra == 3 && codePoint < 0x10000)
            {
                return false;
            }
            if (codePoint >= 0xD800 && codePoint <= 0xDFFF)
            {
                return false;
            }
            if (codePoint > 0x10FFFF)
            {
                return false;
            }

            i += extra + 1;
        }
        return true;
    }

    private static long Align4(long value)
    {
        return (value + 3) & ~3L;
    }

    private static int ReadIndex(byte[] bytes, long offset, int size)
    {
        var raw = size == 4 ? (long)ReadUInt32(bytes, offset) : (long)ReadUInt16(bytes, offset);
        return raw > int.MaxValue ? int.MaxValue : (int)raw;
    }

    /// <summary>
    /// Bounds-checked little-endian readers. An out-of-range request yields 0 and is caught by
    /// the caller's own container check, so a read can never escape the array.
    /// </summary>
    private static ushort ReadUInt16(byte[] bytes, long offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            return 0;
        }
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static uint ReadUInt32(byte[] bytes, long offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            return 0;
        }
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) |
                      (bytes[offset + 3] << 24));
    }

    private static ulong ReadUInt64(byte[] bytes, long offset)
    {
        var low = ReadUInt32(bytes, offset);
        var high = ReadUInt32(bytes, offset + 4);
        return ((ulong)high << 32) | low;
    }
}
