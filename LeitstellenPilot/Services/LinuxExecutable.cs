using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace LeitstellenPilot.Services;

// Reads the official .NET single-file bundle without executing downloaded code.
public static class LinuxExecutable
{
    private static readonly byte[] Signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
    private static long BundleHeader(Stream input)
    {
        // Native libraries can precede the marker in a .NET apphost. Scan in bounded
        // chunks, retaining the marker and its preceding offset across boundaries.
        var buffer = new byte[64 * 1024];
        var overlap = Signature.Length + sizeof(long) - 1;
        var retained = 0;
        long position = 0;
        input.Position = 0;
        while (true)
        {
            var read = input.Read(buffer, retained, buffer.Length - retained);
            if (read == 0) throw new InvalidDataException("Kein .NET-Standalone-Bundle.");
            var available = retained + read;
            var match = available >= sizeof(long) + Signature.Length
                ? buffer.AsSpan(sizeof(long), available - sizeof(long)).IndexOf(Signature) : -1;
            if (match >= 0)
            {
                var header = BitConverter.ToInt64(buffer, match);
                if (header < position + match + sizeof(long) + Signature.Length || header > input.Length - 32)
                    throw new InvalidDataException("Ungültiger Bundle-Header.");
                return header;
            }
            retained = Math.Min(overlap, available);
            buffer.AsSpan(available - retained, retained).CopyTo(buffer);
            position += available - retained;
        }
    }
    private static string Text(BinaryReader reader, int maximum)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Ungültige Bundle-Zeichenfolge.");
        return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
    }
    public static string ReadVersion(string path, ReleasePlatform? platform = null)
    {
        platform ??= ReleasePlatform.Current;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length < 5 * 1024 * 1024 || input.Length > 512L * 1024 * 1024) throw new InvalidDataException("Ungültige Dateigröße.");
            var prefix = new byte[64]; input.ReadExactly(prefix);
            if (!prefix.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) || prefix[4] != 2 || prefix[5] != 1 ||
                BitConverter.ToUInt16(prefix, 18) != platform.ElfMachine || BitConverter.ToUInt16(prefix, 16) is not (2 or 3))
                throw new InvalidDataException("Keine passende Linux-ELF-Anwendung.");
            var header = BundleHeader(input);
            input.Position = header;
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var major = reader.ReadUInt32(); var minor = reader.ReadUInt32(); var count = reader.ReadInt32();
            if (major is not (2 or 6) || minor != 0 || count is < 1 or > 4096) throw new InvalidDataException("Nicht unterstütztes Bundle-Format.");
            Text(reader, 256);
            for (var i = 0; i < 5; i++) reader.ReadInt64();
            for (var i = 0; i < count; i++)
            {
                var offset = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = major >= 6 ? reader.ReadInt64() : 0;
                var type = reader.ReadByte(); var name = Text(reader, 1024);
                if (offset < 64 || size < 0 || offset > header || size > header - offset || compressed < 0 || compressed > size)
                    throw new InvalidDataException("Ungültiger Bundle-Eintrag.");
                if (name != "LeitstellenPilot.dll") continue;
                if (type != 1 || compressed != 0 || size is < 1024 or > 32 * 1024 * 1024) throw new InvalidDataException("Ungültige Hauptassembly.");
                input.Position = offset; var assembly = new byte[(int)size]; input.ReadExactly(assembly);
                using var pe = new PEReader(new MemoryStream(assembly));
                var metadata = pe.GetMetadataReader(); var definition = metadata.GetAssemblyDefinition();
                if (metadata.GetString(definition.Name) != "LeitstellenPilot") throw new InvalidDataException("Falsches Produkt.");
                return definition.Version.ToString();
            }
            throw new InvalidDataException("LeitstellenPilot-Hauptassembly fehlt.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or ArgumentException or OverflowException)
        { throw new InvalidOperationException("Die Datei ist kein gültiges LeitstellenPilot-Standalonepaket für " + platform.RuntimeIdentifier + ".", ex); }
    }
}
