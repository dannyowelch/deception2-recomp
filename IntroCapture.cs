using System.IO.Compression;
using RecompOne.Runtime;

namespace Deception;

/// <summary>
/// While KF2_INTRO_PNG is set, read the presented picture back and keep the
/// first one that is not a flat colour. Writes that PNG and leaves the process.
/// </summary>
public static class IntroCapture
{
    public static void Arm()
    {
        var path = Environment.GetEnvironmentVariable("KF2_INTRO_PNG");
        if (string.IsNullOrWhiteSpace(path)) return;

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(180_000);
            Console.Error.WriteLine("[Boot] intro capture timed out before a non-blank presented frame");
            Environment.Exit(2);
        })
        { IsBackground = true, Name = "intro-watchdog" };
        watchdog.Start();

        void Take(byte[] rgba, int w, int h, int dispX, int dispY)
        {
            if (w < 8 || h < 8 || !HasPicture(rgba))
            {
                PresentSnap.Request(Take);
                return;
            }

            // The first bright frame is the iris. Keep the ones after it too,
            // so a torn logo is not the only picture we have.
            _saved++;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var dir = Path.GetDirectoryName(path);
            var numbered = Path.Combine(dir ?? "", $"{name}_{_saved}{ext}");
            WritePng(numbered, rgba, w, h);
            Console.WriteLine($"[present] captured {w}x{h} from {dispX},{dispY} -> {numbered}");
            if (_saved >= 10)
                Environment.Exit(0);
            PresentSnap.Request(Take);
        }

        PresentSnap.Request(Take);
    }

    static int _peak;
    static int _reported;
    static int _saved;

    static bool HasPicture(byte[] rgba)
    {
        var min = int.MaxValue;
        var max = 0;
        var step = Math.Max(4, (rgba.Length / 4) / 4000 * 4);
        for (var i = 0; i + 2 < rgba.Length; i += step)
        {
            var lum = rgba[i] + rgba[i + 1] + rgba[i + 2];
            if (lum < min) min = lum;
            if (lum > max) max = lum;
        }

        if (max > _peak) _peak = max;
        var now = Environment.TickCount;
        if (now - _reported > 1000)
        {
            _reported = now;
            Console.WriteLine($"[present] snap peak={_peak}");
            _peak = 0;
        }

        // The opening iris sits just above black, then a flat grey disc.
        // Wait until a pixel is actually bright (a sum of 220 is still that disc).
        return max - min > 24 && max > 500;
    }

    static void WritePng(string path, byte[] rgba, int w, int h)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * w * 4, w * 4);
        }

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
            raw.WriteTo(z);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(fs, "IHDR", IHDR(w, h));
        WriteChunk(fs, "IDAT", deflated.ToArray());
        WriteChunk(fs, "IEND", []);
    }

    static byte[] IHDR(int w, int h)
    {
        var b = new byte[13];
        PutBe(b, 0, w);
        PutBe(b, 4, h);
        b[8] = 8;
        b[9] = 6;
        return b;
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        PutBe(len, 0, data.Length);
        s.Write(len);
        var tag = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(tag);
        s.Write(data);
        var crc = Crc(tag, data);
        Span<byte> c = stackalloc byte[4];
        PutBe(c, 0, (int)crc);
        s.Write(c);
    }

    static void PutBe(Span<byte> b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    static uint Crc(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        void Feed(byte x)
        {
            c ^= x;
            for (var i = 0; i < 8; i++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        foreach (var x in type) Feed(x);
        foreach (var x in data) Feed(x);
        return ~c;
    }
}
