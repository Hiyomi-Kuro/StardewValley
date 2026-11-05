using System.Text;

namespace ModFramework.Runtime;

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
/// <c>Assembly</c> table row 0 (name, version, culture, public key). Nothing is executed and
/// no static constructor can run.
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

    /// <summary>Raw public key blob; empty means the assembly is not strong-name signed.</summary>
    internal byte[] PublicKey = Array.Empty<byte>();

    internal bool IsStrongNamed => PublicKey.Length > 0;

    internal string VersionText => Major + "." + Minor + "." + Build + "." + Revision;

    /// <summary>
    /// Deterministic, path-free identity text. The public key is represented by its SHA-256
    /// (or by a length marker when hashing is unavailable) because the *token* is defined as
    /// the last 8 bytes of SHA-1 over the key and SHA-1 is not in the trimmed surface either.
    /// Only equality matters here, and hashing the key preserves it.
    /// </summary>
    internal string Describe(string publicKeyFingerprint)
    {
        return Name + ", Version=" + VersionText +
               ", Culture=" + (Culture.Length == 0 ? "neutral" : Culture) +
               ", PublicKey=" + (IsStrongNamed ? publicKeyFingerprint : "none");
    }
}

/// <summary>
/// Minimal ECMA-335 metadata reader: enough to answer "which assembly is this file?" and
/// "is this file a managed assembly at all?".
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

    internal static bool TryReadIdentity(byte[] bytes, out AssemblyFileIdentity identity, out string error)
    {
        identity = new AssemblyFileIdentity();
        error = "";

        int metadataOffset;
        if (!TryLocateMetadata(bytes, out metadataOffset, out error))
        {
            return false;
        }

        Heaps heaps;
        TableHeader header;
        if (!TryReadTableHeader(bytes, metadataOffset, out heaps, out header, out error))
        {
            return false;
        }

        if (header.RowCount[AssemblyTable] == 0)
        {
            error = "the file has no Assembly row (it is a netmodule, not an assembly)";
            return false;
        }

        if (!TryReadAssemblyRow(bytes, heaps, header, out identity, out error))
        {
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ PE

    private static bool TryLocateMetadata(byte[] bytes, out int metadataOffset, out string error)
    {
        metadataOffset = 0;
        error = "";
        if (bytes.Length < 0x40)
        {
            error = "the file is too small to be a PE image";
            return false;
        }
        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            error = "missing the MZ DOS signature";
            return false;
        }

        var peOffset = ReadInt32(bytes, 0x3C);
        if (peOffset < 0 || peOffset > bytes.Length - 24)
        {
            error = "the PE header offset is outside the file";
            return false;
        }
        if (bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E' ||
            bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            error = "missing the PE signature";
            return false;
        }

        var sectionCount = ReadUInt16(bytes, peOffset + 6);
        var optionalHeaderSize = ReadUInt16(bytes, peOffset + 20);
        var optionalHeader = peOffset + 24;
        if (optionalHeaderSize < 2 || optionalHeader > bytes.Length - optionalHeaderSize)
        {
            error = "the optional header is outside the file";
            return false;
        }

        var magic = ReadUInt16(bytes, optionalHeader);
        int dataDirectory;
        if (magic == 0x10B) // PE32
        {
            dataDirectory = optionalHeader + 96;
        }
        else if (magic == 0x20B) // PE32+
        {
            dataDirectory = optionalHeader + 112;
        }
        else
        {
            error = "unknown PE optional header magic 0x" + magic.ToString("x4");
            return false;
        }

        // Data directory entry 14 is the CLI header.
        var cliEntry = dataDirectory + 14 * 8;
        if (cliEntry + 8 > bytes.Length)
        {
            error = "the data directory does not fit in the file";
            return false;
        }
        var cliRva = (int)ReadUInt32(bytes, cliEntry);
        var cliSize = (int)ReadUInt32(bytes, cliEntry + 4);
        if (cliRva == 0 || cliSize == 0)
        {
            error = "the file has no CLI header, so it is not a managed assembly";
            return false;
        }

        var sectionTable = optionalHeader + optionalHeaderSize;
        var cliOffset = RvaToFileOffset(bytes, sectionTable, sectionCount, cliRva, cliSize);
        if (cliOffset < 0)
        {
            error = "the CLI header RVA does not map to any section";
            return false;
        }

        var metadataRva = (int)ReadUInt32(bytes, cliOffset + 8);
        var metadataSize = (int)ReadUInt32(bytes, cliOffset + 12);
        if (metadataRva == 0 || metadataSize == 0)
        {
            error = "the CLI header declares no metadata";
            return false;
        }

        metadataOffset = RvaToFileOffset(bytes, sectionTable, sectionCount, metadataRva, metadataSize);
        if (metadataOffset < 0)
        {
            error = "the metadata RVA does not map to any section";
            return false;
        }
        if (metadataOffset + 16 > bytes.Length)
        {
            error = "the metadata root is outside the file";
            return false;
        }
        if (ReadUInt32(bytes, metadataOffset) != MetadataSignature)
        {
            error = "missing the BSJB metadata signature";
            return false;
        }
        return true;
    }

    private static int RvaToFileOffset(byte[] bytes, int sectionTable, int sectionCount, int rva, int size)
    {
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sectionTable + i * 40;
            if (section + 40 > bytes.Length)
            {
                return -1;
            }
            var virtualSize = (int)ReadUInt32(bytes, section + 8);
            var virtualAddress = (int)ReadUInt32(bytes, section + 12);
            var rawSize = (int)ReadUInt32(bytes, section + 16);
            var rawPointer = (int)ReadUInt32(bytes, section + 20);
            var span = virtualSize > rawSize ? virtualSize : rawSize;
            if (rva < virtualAddress || rva >= virtualAddress + span)
            {
                continue;
            }
            var offset = rawPointer + (rva - virtualAddress);
            if (offset < 0 || offset >= bytes.Length)
            {
                return -1;
            }
            return offset;
        }
        return -1;
    }

    // ------------------------------------------------------------ metadata

    private sealed class Heaps
    {
        internal int StringsOffset;
        internal int StringsSize;
        internal int BlobOffset;
        internal int BlobSize;
        internal int StringIndexSize = 2;
        internal int BlobIndexSize = 2;
        internal int GuidIndexSize = 2;
    }

    private sealed class TableHeader
    {
        internal readonly int[] RowCount = new int[64];
        internal int RowsStart;
    }

    private static bool TryReadTableHeader(byte[] bytes, int metadataOffset, out Heaps heaps, out TableHeader header, out string error)
    {
        heaps = new Heaps();
        header = new TableHeader();
        error = "";

        var versionLength = (int)ReadUInt32(bytes, metadataOffset + 12);
        if (versionLength < 0 || versionLength > 1024)
        {
            error = "the metadata version string length is out of range";
            return false;
        }
        var position = metadataOffset + 16 + versionLength;
        if (position + 4 > bytes.Length)
        {
            error = "the metadata stream header is truncated";
            return false;
        }

        var streamCount = ReadUInt16(bytes, position + 2);
        position += 4;

        var tablesOffset = -1;
        var tablesSize = 0;
        for (var i = 0; i < streamCount; i++)
        {
            if (position + 8 > bytes.Length)
            {
                error = "a metadata stream header is truncated";
                return false;
            }
            var offset = (int)ReadUInt32(bytes, position);
            var size = (int)ReadUInt32(bytes, position + 4);
            position += 8;

            var nameStart = position;
            var nameEnd = nameStart;
            while (nameEnd < bytes.Length && nameEnd < nameStart + 32 && bytes[nameEnd] != 0)
            {
                nameEnd++;
            }
            if (nameEnd >= bytes.Length)
            {
                error = "a metadata stream name is not terminated";
                return false;
            }
            var name = Encoding.UTF8.GetString(bytes, nameStart, nameEnd - nameStart);
            position = Align4(nameEnd + 1);

            var absolute = metadataOffset + offset;
            if (absolute < 0 || absolute > bytes.Length)
            {
                error = "metadata stream \"" + name + "\" is outside the file";
                return false;
            }

            if (name == "#~" || name == "#-")
            {
                tablesOffset = absolute;
                tablesSize = size;
            }
            else if (name == "#Strings")
            {
                heaps.StringsOffset = absolute;
                heaps.StringsSize = size;
            }
            else if (name == "#Blob")
            {
                heaps.BlobOffset = absolute;
                heaps.BlobSize = size;
            }
        }

        if (tablesOffset < 0)
        {
            error = "the metadata has no #~ table stream";
            return false;
        }
        if (tablesSize <= 0 || tablesOffset + tablesSize > bytes.Length)
        {
            error = "the #~ table stream is outside the file";
            return false;
        }

        // Table stream header: Reserved(4) Major(1) Minor(1) HeapSizes(1) Reserved(1)
        //                      Valid(8) Sorted(8) RowCounts(4 each)
        if (tablesOffset + 24 > bytes.Length)
        {
            error = "the #~ header is truncated";
            return false;
        }
        var heapSizes = bytes[tablesOffset + 6];
        heaps.StringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        heaps.GuidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
        heaps.BlobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;

        var position2 = tablesOffset + 24;
        var valid = ReadUInt64(bytes, tablesOffset + 8);
        for (var table = 0; table < 64; table++)
        {
            if ((valid & (1UL << table)) == 0)
            {
                continue;
            }
            if (position2 + 4 > bytes.Length)
            {
                error = "the #~ row counts are truncated";
                return false;
            }
            header.RowCount[table] = (int)ReadUInt32(bytes, position2);
            position2 += 4;
        }
        header.RowsStart = position2;

        // Walking to table 0x20 must stay inside the stream.
        var consumed = position2 - tablesOffset;
        if (consumed > tablesSize)
        {
            error = "the #~ header is larger than its stream";
            return false;
        }
        return true;
    }

    private static bool TryReadAssemblyRow(byte[] bytes, Heaps heaps, TableHeader header, out AssemblyFileIdentity identity, out string error)
    {
        identity = new AssemblyFileIdentity();
        error = "";

        var position = header.RowsStart;
        for (var table = 0; table < AssemblyTable; table++)
        {
            var rows = header.RowCount[table];
            if (rows == 0)
            {
                continue;
            }
            var rowSize = RowSize(table, heaps, header);
            if (rowSize <= 0)
            {
                error = "unsupported metadata table 0x" + table.ToString("x2");
                return false;
            }
            var advance = (long)rowSize * rows;
            if (advance <= 0 || position + advance > bytes.Length)
            {
                error = "the #~ table data is truncated (table 0x" + table.ToString("x2") + ")";
                return false;
            }
            position += (int)advance;
        }

        var assemblyRow = 4 + 2 + 2 + 2 + 2 + 4 + heaps.BlobIndexSize + heaps.StringIndexSize + heaps.StringIndexSize;
        if (position + assemblyRow > bytes.Length)
        {
            error = "the Assembly table row is truncated";
            return false;
        }

        identity.Major = ReadUInt16(bytes, position + 4);
        identity.Minor = ReadUInt16(bytes, position + 6);
        identity.Build = ReadUInt16(bytes, position + 8);
        identity.Revision = ReadUInt16(bytes, position + 10);

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
        identity.Name = name;

        string culture;
        if (!TryReadString(bytes, heaps, cultureIndex, out culture, out error))
        {
            return false;
        }
        identity.Culture = culture;

        byte[] publicKey;
        if (!TryReadBlob(bytes, heaps, publicKeyIndex, out publicKey, out error))
        {
            return false;
        }
        identity.PublicKey = publicKey;
        return true;
    }

    private static bool TryReadString(byte[] bytes, Heaps heaps, int index, out string value, out string error)
    {
        value = "";
        error = "";
        if (index == 0)
        {
            return true;
        }
        var start = heaps.StringsOffset + index;
        if (heaps.StringsSize > 0 && index >= heaps.StringsSize)
        {
            error = "a #Strings index is outside the heap";
            return false;
        }
        if (start < 0 || start >= bytes.Length)
        {
            error = "a #Strings index is outside the file";
            return false;
        }
        var end = start;
        while (end < bytes.Length && bytes[end] != 0)
        {
            end++;
        }
        if (end >= bytes.Length)
        {
            error = "a #Strings entry is not terminated";
            return false;
        }
        value = Encoding.UTF8.GetString(bytes, start, end - start);
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
        if (heaps.BlobSize > 0 && index >= heaps.BlobSize)
        {
            error = "a #Blob index is outside the heap";
            return false;
        }
        var position = heaps.BlobOffset + index;
        if (position < 0 || position >= bytes.Length)
        {
            error = "a #Blob index is outside the file";
            return false;
        }

        int length;
        if (!TryReadCompressedUInt(bytes, ref position, out length))
        {
            error = "a #Blob length prefix is invalid";
            return false;
        }
        if (length < 0 || position + length > bytes.Length)
        {
            error = "a #Blob entry runs past the end of the file";
            return false;
        }
        if (length == 0)
        {
            return true;
        }
        var result = new byte[length];
        Array.Copy(bytes, position, result, 0, length);
        value = result;
        return true;
    }

    private static bool TryReadCompressedUInt(byte[] bytes, ref int position, out int value)
    {
        value = 0;
        if (position >= bytes.Length)
        {
            return false;
        }
        var first = bytes[position];
        if ((first & 0x80) == 0)
        {
            value = first;
            position += 1;
            return true;
        }
        if ((first & 0xC0) == 0x80)
        {
            if (position + 2 > bytes.Length)
            {
                return false;
            }
            value = ((first & 0x3F) << 8) | bytes[position + 1];
            position += 2;
            return true;
        }
        if ((first & 0xE0) == 0xC0)
        {
            if (position + 4 > bytes.Length)
            {
                return false;
            }
            value = ((first & 0x1F) << 24) | (bytes[position + 1] << 16) |
                    (bytes[position + 2] << 8) | bytes[position + 3];
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
        var max = 0;
        for (var i = 0; i < tables.Length; i++)
        {
            var rows = header.RowCount[tables[i]];
            if (rows > max)
            {
                max = rows;
            }
        }
        var limit = 1 << (16 - CodedTagBits[kind]);
        return max < limit ? 2 : 4;
    }

    // ------------------------------------------------------------- primitives

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private static int ReadIndex(byte[] bytes, int offset, int size)
    {
        return size == 4 ? (int)ReadUInt32(bytes, offset) : ReadUInt16(bytes, offset);
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        return (int)ReadUInt32(bytes, offset);
    }

    private static int ReadUInt16(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            return 0;
        }
        return bytes[offset] | (bytes[offset + 1] << 8);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            return 0;
        }
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        var low = ReadUInt32(bytes, offset);
        var high = ReadUInt32(bytes, offset + 4);
        return ((ulong)high << 32) | low;
    }
}
