using System.Text;

namespace BrainstormBuddy.Stt;

/// <summary>Минимальный читатель .npy (float32, C-order) — для валидации против эталона.</summary>
public static class NpyIO
{
    public static (float[] data, int[] shape) LoadFloat32(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var magic = br.ReadBytes(6); // \x93NUMPY
        if (magic.Length < 6 || magic[0] != 0x93) throw new InvalidDataException("Не .npy файл");
        byte major = br.ReadByte(); br.ReadByte(); // minor
        int headerLen = major >= 2 ? br.ReadInt32() : br.ReadUInt16();
        string header = Encoding.ASCII.GetString(br.ReadBytes(headerLen));

        if (!header.Contains("'<f4'") && !header.Contains("\"<f4\""))
            throw new NotSupportedException("Ожидается dtype float32 (<f4): " + header);
        if (header.Contains("'fortran_order': True"))
            throw new NotSupportedException("fortran_order не поддерживается");

        // shape из "'shape': (a, b, c)"
        int si = header.IndexOf("(", header.IndexOf("shape"));
        int se = header.IndexOf(")", si);
        var dims = header.Substring(si + 1, se - si - 1)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0).Select(int.Parse).ToArray();

        int count = 1; foreach (var d in dims) count *= d;
        var data = new float[count];
        var bytes = br.ReadBytes(count * 4);
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        return (data, dims);
    }
}
