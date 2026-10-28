namespace RevitMCP.Addin.Coordination.Bcf;

/// <summary>
/// The 22-character IFC GlobalId and the GUID it compresses. The GUID's 32 hex digits, read as one big-endian 128-bit
/// number, are written in base 64 with the IFC alphabet: the first character carries the top 2 bits, the next 21 six
/// bits each. Revit's IFC exporter writes an element's GlobalId this way from <c>ExportUtils.GetExportId</c>, so the
/// same conversion matches BCF components back to Revit elements. Pure.
/// </summary>
public static class IfcGuid
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    public static string FromGuid(Guid guid)
    {
        var bytes = BigEndianBytes(guid);
        var chars = new char[22];

        // First byte: 8 bits in two characters.
        chars[0] = Alphabet[bytes[0] >> 6];
        chars[1] = Alphabet[bytes[0] & 0x3F];

        // Then five groups of 3 bytes (24 bits) in four characters each.
        for (var group = 0; group < 5; group++)
        {
            var offset = 1 + group * 3;
            var value = (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
            for (var i = 3; i >= 0; i--)
            {
                chars[2 + group * 4 + i] = Alphabet[value & 0x3F];
                value >>= 6;
            }
        }

        return new string(chars);
    }

    public static bool TryToGuid(string? ifcGuid, out Guid guid)
    {
        guid = Guid.Empty;
        if (ifcGuid == null || ifcGuid.Length != 22) return false;

        var values = new int[22];
        for (var i = 0; i < 22; i++)
        {
            var index = Alphabet.IndexOf(ifcGuid[i]);
            if (index < 0) return false;
            values[i] = index;
        }

        if (values[0] > 3) return false;

        var bytes = new byte[16];
        bytes[0] = (byte)((values[0] << 6) | values[1]);
        for (var group = 0; group < 5; group++)
        {
            var value = 0;
            for (var i = 0; i < 4; i++) value = (value << 6) | values[2 + group * 4 + i];
            var offset = 1 + group * 3;
            bytes[offset] = (byte)(value >> 16);
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)value;
        }

        guid = new Guid(ToHex(bytes));
        return true;
    }

    /// <summary>True for a well-formed 22-character IFC GlobalId.</summary>
    public static bool IsValid(string? ifcGuid) => TryToGuid(ifcGuid, out _);

    /// <summary>The GUID's 16 bytes in the order its "N" string writes them.</summary>
    private static byte[] BigEndianBytes(Guid guid)
    {
        var hex = guid.ToString("N");
        var bytes = new byte[16];
        for (var i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string ToHex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
}
