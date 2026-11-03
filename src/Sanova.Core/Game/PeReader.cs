using System.Buffers.Binary;
using System.Text;

namespace Sanova.Core.Game;

public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

/// <summary>The handful of PE header fields used to classify unrecognized game executables.</summary>
public sealed record PeInfo(ushort Machine, uint TimeDateStamp, uint EntryPointRva, uint ImageBase, IReadOnlyList<PeSection> Sections)
{
    public bool Is32BitX86 => Machine == 0x014C;

    public DateTimeOffset LinkTime => DateTimeOffset.FromUnixTimeSeconds(TimeDateStamp);

    public bool HasSection(string name) => Sections.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    public PeSection? EntryPointSection => Sections.FirstOrDefault(s =>
        EntryPointRva >= s.VirtualAddress && EntryPointRva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
}

/// <summary>Minimal, allocation-light PE header parser. Returns null for anything that is not a valid PE image.</summary>
public static class PeReader
{
    public static PeInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryRead(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PeInfo? TryRead(Stream stream)
    {
        Span<byte> dos = stackalloc byte[64];
        if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z')
        {
            return null;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset <= 0 || peOffset > 16 * 1024 * 1024 || peOffset + 24 > stream.Length)
        {
            return null;
        }

        stream.Position = peOffset;
        Span<byte> header = stackalloc byte[24];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length ||
            header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0)
        {
            return null;
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        if (sectionCount > 96 || optionalHeaderSize < 32)
        {
            return null;
        }

        var optional = new byte[optionalHeaderSize];
        if (stream.ReadAtLeast(optional, optional.Length, throwOnEndOfStream: false) < optional.Length)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        var entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(16));
        var imageBase = magic == 0x10B ? BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(28)) : 0u;

        var sections = new List<PeSection>(sectionCount);
        Span<byte> section = stackalloc byte[40];
        for (var i = 0; i < sectionCount; i++)
        {
            if (stream.ReadAtLeast(section, section.Length, throwOnEndOfStream: false) < section.Length)
            {
                return null;
            }
            var name = Encoding.ASCII.GetString(section[..8]).TrimEnd('\0');
            sections.Add(new PeSection(
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(section[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(section[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(section[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(section[16..])));
        }

        return new PeInfo(machine, timeDateStamp, entryPoint, imageBase, sections);
    }
}
