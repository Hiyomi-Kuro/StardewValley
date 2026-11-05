using System;
using System.Collections.Generic;
using System.Text;

namespace ModFramework.Tests;

/// <summary>
/// Where every structural field of a <see cref="SyntheticPe"/> image lives, as an absolute file
/// offset. Tests patch these offsets to produce the exact malformations the PE/CLI reader must
/// reject; nothing else in the image changes, so each negative sample differs from the valid
/// baseline by one documented field.
/// </summary>
internal sealed class PeAnchors
{
    internal int CoffSectionCount;
    internal int CoffOptionalHeaderSize;
    internal int OptionalMagic;
    internal int OptionalRvaCount;
    internal int SectionTable;
    internal int SectionVirtualSize;
    internal int SectionVirtualAddress;
    internal int SectionRawSize;
    internal int SectionRawPointer;
    internal int CliDirEntry;
    internal int CliCb;
    internal int CliMetadataRva;
    internal int CliMetadataSize;
    internal int MetadataRoot;
    internal int MetadataSize;
    internal int MetadataVersionLength;
    internal int MetadataStreamCount;

    /// <summary>Absolute offset of each stream header's <c>Offset</c> field, in file order.</summary>
    internal int[] StreamOffsetField = Array.Empty<int>();

    /// <summary>Absolute offset of each stream header's <c>Size</c> field, in file order.</summary>
    internal int[] StreamSizeField = Array.Empty<int>();

    /// <summary>Absolute offset of each stream header's name bytes, in file order.</summary>
    internal int[] StreamNameField = Array.Empty<int>();

    internal string[] StreamNames = Array.Empty<string>();

    internal int TablesData;
    internal int TablesValidMask;
    internal int TablesAssemblyRowCount;
    internal int TablesRowsStart;
    internal int AssemblyRow;
    internal int AssemblyFlags;
    internal int AssemblyPublicKeyIndex;
    internal int AssemblyNameIndex;
    internal int AssemblyCultureIndex;
    internal int StringsData;
    internal int StringsSize;
    internal int BlobData;
    internal int BlobSize;
    internal int BlobLengthPrefix;
}

/// <summary>
/// Builds a minimal but structurally complete managed PE image, plus the anchors above.
///
/// Why hand-built: the reader has to reject truncated, overlapping and out-of-container
/// structures that no compiler emits. A real assembly cannot express "SizeOfRawData covers only
/// the first byte of the CLI header" or "the #Strings heap ends inside a name", so those cases
/// have to be synthesized byte by byte.
///
/// Nothing built here is ever loaded into the AppDomain - the bytes are only handed to the
/// metadata reader.
/// </summary>
internal static class SyntheticPe
{
    internal const int FileSize = 0x1200;
    internal const int PeSignature = 0x40;
    internal const int CoffHeader = 0x44;          // 20 bytes
    internal const int OptionalHeader = 0x58;      // 0x44 + 20
    internal const int RawPointer = 0x200;
    internal const int SectionRva = 0x2000;
    internal const int DefaultRawSize = 0x1000;
    internal const int CliHeaderAt = RawPointer;   // RVA 0x2000
    internal const int MetadataAt = 0x300;         // RVA 0x2100
    internal const int MetadataVersionStringLength = 12;

    internal sealed class Spec
    {
        internal string AssemblyName = "TestAsm";
        internal byte[] PublicKey = Array.Empty<byte>();
        internal ushort AssemblyFlags;
        internal bool Pe32Plus;
        internal bool FourByteHeapIndexes;
        internal int[] StreamOrder = { 0, 1, 2 };   // 0 = #~, 1 = #Strings, 2 = #Blob
        internal uint TableRowCount = 1;
    }

    internal sealed class Built
    {
        internal byte[] Image = Array.Empty<byte>();
        internal PeAnchors A = new();
    }

    internal static Built Create(Action<Spec> configure = null)
    {
        var spec = new Spec();
        configure?.Invoke(spec);
        return Build(spec);
    }

    internal static Built Build(Spec spec)
    {
        var image = new byte[FileSize];
        var a = new PeAnchors();

        var optionalHeaderSize = spec.Pe32Plus ? 240 : 224;
        var rvaCountOffsetInOptional = spec.Pe32Plus ? 108 : 92;
        var dataDirectoryInOptional = spec.Pe32Plus ? 112 : 96;
        var sectionTable = OptionalHeader + optionalHeaderSize;

        // ---- DOS header ----------------------------------------------------
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteU32(image, 0x3C, (uint)PeSignature);

        // ---- PE signature + COFF header ------------------------------------
        image[PeSignature] = (byte)'P';
        image[PeSignature + 1] = (byte)'E';
        WriteU16(image, CoffHeader + 0, 0x014C);            // Machine = i386 (the value is not interpreted)
        WriteU16(image, CoffHeader + 2, 1);                 // NumberOfSections
        WriteU16(image, CoffHeader + 16, (ushort)optionalHeaderSize);
        a.CoffSectionCount = CoffHeader + 2;
        a.CoffOptionalHeaderSize = CoffHeader + 16;

        // ---- optional header ----------------------------------------------
        WriteU16(image, OptionalHeader + 0, spec.Pe32Plus ? (ushort)0x020B : (ushort)0x010B);
        WriteU32(image, OptionalHeader + rvaCountOffsetInOptional, 16);
        a.OptionalMagic = OptionalHeader;
        a.OptionalRvaCount = OptionalHeader + rvaCountOffsetInOptional;

        // CLI header is data directory entry 14.
        var cliDir = OptionalHeader + dataDirectoryInOptional + 14 * 8;
        WriteU32(image, cliDir, SectionRva);                // RVA -> file RawPointer
        WriteU32(image, cliDir + 4, 72);                    // size of IMAGE_COR20_HEADER
        a.CliDirEntry = cliDir;

        // ---- section table -------------------------------------------------
        WriteName(image, sectionTable, ".text");
        WriteU32(image, sectionTable + 8, DefaultRawSize);  // VirtualSize
        WriteU32(image, sectionTable + 12, SectionRva);     // VirtualAddress
        WriteU32(image, sectionTable + 16, DefaultRawSize); // SizeOfRawData
        WriteU32(image, sectionTable + 20, RawPointer);     // PointerToRawData
        a.SectionTable = sectionTable;
        a.SectionVirtualSize = sectionTable + 8;
        a.SectionVirtualAddress = sectionTable + 12;
        a.SectionRawSize = sectionTable + 16;
        a.SectionRawPointer = sectionTable + 20;

        // ---- metadata block (built first: the CLI header needs its size) ----
        var metadata = BuildMetadata(spec, MetadataAt, a);

        // ---- CLI header ----------------------------------------------------
        WriteU32(image, CliHeaderAt + 0, 72);               // cb
        WriteU16(image, CliHeaderAt + 4, 2);                // MajorRuntimeVersion
        WriteU16(image, CliHeaderAt + 6, 5);                // MinorRuntimeVersion
        WriteU32(image, CliHeaderAt + 8, (uint)(MetadataAt - RawPointer) + SectionRva); // MetaData RVA
        WriteU32(image, CliHeaderAt + 12, (uint)metadata.Length);                        // MetaData size
        WriteU32(image, CliHeaderAt + 16, 1);               // Flags = ILONLY
        a.CliCb = CliHeaderAt;
        a.CliMetadataRva = CliHeaderAt + 8;
        a.CliMetadataSize = CliHeaderAt + 12;

        Array.Copy(metadata, 0, image, MetadataAt, metadata.Length);
        a.MetadataRoot = MetadataAt;
        a.MetadataSize = metadata.Length;

        var built = new Built();
        built.Image = image;
        built.A = a;
        return built;
    }

    // ------------------------------------------------------------------ metadata

    private static byte[] BuildMetadata(Spec spec, int metadataAt, PeAnchors a)
    {
        var nameBytes = Encoding.UTF8.GetBytes(spec.AssemblyName);

        // #Strings: index 0 is the empty string, the assembly name starts at 1.
        var strings = new List<byte> { 0 };
        strings.AddRange(nameBytes);
        strings.Add(0);

        // #Blob: index 0 is empty; a public key (when present) starts at 1.
        var blob = new List<byte> { 0 };
        var publicKeyIndex = 0;
        if (spec.PublicKey.Length > 0)
        {
            publicKeyIndex = 1;
            blob.AddRange(EncodeCompressedLength(spec.PublicKey.Length));
            blob.AddRange(spec.PublicKey);
        }

        var indexSize = spec.FourByteHeapIndexes ? 4 : 2;
        var tables = BuildTables(spec, indexSize, publicKeyIndex);

        var streamCount = 3;
        var streamNames = new[] { "#~", "#Strings", "#Blob" };

        // Stream headers start at 32: sig(4) major(2) minor(2) reserved(4) versionLen(4)
        // version(12) flags(2) streamCount(2). Every header is an 8-byte (Offset, Size) pair
        // followed by the null-terminated name padded to a 4-byte boundary.
        const int headerPrefix = 32;
        var nameAreas = new int[streamCount];
        var rawNames = new byte[streamCount][];
        for (var i = 0; i < streamCount; i++)
        {
            rawNames[i] = Encoding.UTF8.GetBytes(streamNames[i]);
            nameAreas[i] = Align4(rawNames[i].Length + 1);
        }

        var slotHeaderStart = new int[streamCount];
        var cursor = headerPrefix;
        for (var slot = 0; slot < streamCount; slot++)
        {
            slotHeaderStart[slot] = cursor;
            cursor += 8 + nameAreas[spec.StreamOrder[slot]];
        }

        var dataStart = cursor;
        var tablesAt = dataStart;
        var stringsAt = tablesAt + tables.Count;
        var blobAt = stringsAt + strings.Count;

        var sink = new List<byte>(new byte[dataStart]);
        sink.AddRange(tables);
        sink.AddRange(strings);
        sink.AddRange(blob);

        // ---- write the metadata root over the reserved prefix ---------------
        WriteAscii(sink, 0, "BSJB");
        WriteU16List(sink, 4, 1);   // MajorVersion
        WriteU16List(sink, 6, 1);   // MinorVersion
        WriteU32List(sink, 8, 0);   // Reserved
        WriteU32List(sink, 12, MetadataVersionStringLength);
        WriteAscii(sink, 16, "v4.0.30319");
        // bytes 26..27 stay zero (terminator + padding)
        WriteU16List(sink, 28, 0);              // Flags
        WriteU16List(sink, 30, (ushort)streamCount);

        a.MetadataVersionLength = metadataAt + 12;
        a.MetadataStreamCount = metadataAt + 30;

        // ---- stream headers, in the requested order -------------------------
        var sizes = new[] { tables.Count, strings.Count, blob.Count };
        var offsets = new[] { tablesAt, stringsAt, blobAt };
        a.StreamOffsetField = new int[streamCount];
        a.StreamSizeField = new int[streamCount];
        a.StreamNameField = new int[streamCount];
        a.StreamNames = new string[streamCount];
        for (var slot = 0; slot < streamCount; slot++)
        {
            var stream = spec.StreamOrder[slot];
            var headerAt = slotHeaderStart[slot];
            WriteU32List(sink, headerAt, (uint)offsets[stream]);
            WriteU32List(sink, headerAt + 4, (uint)sizes[stream]);
            for (var i = 0; i < rawNames[stream].Length; i++)
            {
                sink[headerAt + 8 + i] = rawNames[stream][i];
            }
            a.StreamOffsetField[slot] = metadataAt + headerAt;
            a.StreamSizeField[slot] = metadataAt + headerAt + 4;
            a.StreamNameField[slot] = metadataAt + headerAt + 8;
            a.StreamNames[slot] = streamNames[stream];
        }

        // ---- anchors inside the heaps and the table stream ------------------
        a.TablesData = metadataAt + tablesAt;
        a.TablesValidMask = a.TablesData + 8;
        a.TablesAssemblyRowCount = a.TablesData + 24;
        a.TablesRowsStart = a.TablesData + 28;
        a.AssemblyRow = a.TablesRowsStart;
        a.AssemblyFlags = a.AssemblyRow + 12;
        a.AssemblyPublicKeyIndex = a.AssemblyRow + 16;
        a.AssemblyNameIndex = a.AssemblyRow + 16 + indexSize;
        a.AssemblyCultureIndex = a.AssemblyRow + 16 + indexSize * 2;
        a.StringsData = metadataAt + stringsAt;
        a.StringsSize = strings.Count;
        a.BlobData = metadataAt + blobAt;
        a.BlobSize = blob.Count;
        a.BlobLengthPrefix = spec.PublicKey.Length > 0 ? a.BlobData + 1 : a.BlobData;

        return sink.ToArray();
    }

    private static List<byte> BuildTables(Spec spec, int indexSize, int publicKeyIndex)
    {
        var tables = new List<byte>();
        tables.AddRange(new byte[4]);        // Reserved
        tables.Add(2);                       // MajorVersion
        tables.Add(0);                       // MinorVersion
        tables.Add((byte)(indexSize == 4 ? 0x07 : 0x00)); // HeapSizes
        tables.Add(1);                       // Reserved
        AddU64(tables, 1UL << 0x20);         // Valid: only the Assembly table
        AddU64(tables, 0);                   // Sorted
        AddU32(tables, spec.TableRowCount);

        // Assembly row: HashAlgId(4) Major(2) Minor(2) Build(2) Revision(2) Flags(4)
        //                PublicKey(blob) Name(string) Culture(string)
        AddU32(tables, 0x8004);
        AddU16(tables, 1);
        AddU16(tables, 0);
        AddU16(tables, 0);
        AddU16(tables, 0);
        AddU32(tables, spec.AssemblyFlags);
        AddIndex(tables, publicKeyIndex, indexSize);
        AddIndex(tables, 1, indexSize);      // Name at #Strings offset 1
        AddIndex(tables, 0, indexSize);      // Culture neutral

        return tables;
    }

    // ------------------------------------------------------------------ helpers

    internal static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    internal static byte[] EncodeCompressedLength(int length)
    {
        if (length < 0x80)
        {
            return new[] { (byte)length };
        }
        if (length < 0x4000)
        {
            return new[] { (byte)(0x80 | (length >> 8)), (byte)(length & 0xFF) };
        }
        throw new ArgumentOutOfRangeException(nameof(length), "the fixture only needs the 1- and 2-byte forms");
    }

    internal static void WriteU16(byte[] image, int offset, ushort value)
    {
        image[offset] = (byte)(value & 0xFF);
        image[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    internal static void WriteU32(byte[] image, int offset, uint value)
    {
        image[offset] = (byte)(value & 0xFF);
        image[offset + 1] = (byte)((value >> 8) & 0xFF);
        image[offset + 2] = (byte)((value >> 16) & 0xFF);
        image[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    internal static void WriteU64(byte[] image, int offset, ulong value)
    {
        WriteU32(image, offset, (uint)(value & 0xFFFFFFFF));
        WriteU32(image, offset + 4, (uint)(value >> 32));
    }

    internal static void WriteName(byte[] image, int offset, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, image, offset, bytes.Length);
    }

    private static void WriteAscii(List<byte> sink, int offset, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++)
        {
            sink[offset + i] = bytes[i];
        }
    }

    private static void WriteU16List(List<byte> sink, int offset, ushort value)
    {
        sink[offset] = (byte)(value & 0xFF);
        sink[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteU32List(List<byte> sink, int offset, uint value)
    {
        sink[offset] = (byte)(value & 0xFF);
        sink[offset + 1] = (byte)((value >> 8) & 0xFF);
        sink[offset + 2] = (byte)((value >> 16) & 0xFF);
        sink[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void AddU16(List<byte> sink, ushort value)
    {
        sink.Add((byte)(value & 0xFF));
        sink.Add((byte)((value >> 8) & 0xFF));
    }

    private static void AddU32(List<byte> sink, uint value)
    {
        sink.Add((byte)(value & 0xFF));
        sink.Add((byte)((value >> 8) & 0xFF));
        sink.Add((byte)((value >> 16) & 0xFF));
        sink.Add((byte)((value >> 24) & 0xFF));
    }

    private static void AddU64(List<byte> sink, ulong value)
    {
        AddU32(sink, (uint)(value & 0xFFFFFFFF));
        AddU32(sink, (uint)(value >> 32));
    }

    private static void AddIndex(List<byte> sink, int value, int size)
    {
        if (size == 4)
        {
            AddU32(sink, (uint)value);
            return;
        }
        AddU16(sink, (ushort)value);
    }
}
