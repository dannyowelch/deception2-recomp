using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Deception;

/// <summary>
/// The frame function <c>func_8001441C</c> waits in VSync, then reads the pad
/// and steps the world. One call is one picture and one step of traps, the
/// player and enemies. A zero byte at <c>0x800DB4C4</c> passes VSync(3): three
/// NTSC vblanks, 20 pictures a second. Any other value passes VSync(2): two
/// vblanks, 30 a second. On a PC the work fits in the first vblank, so the
/// wait is the whole frame and the whole speed.
///
/// The pre-hook rewrites that one call to VSync(2). Menus and in-engine
/// cutscenes already ask for two vblanks, so they stay at 30. Gameplay's
/// VSync(3) becomes the same two. The guest return is <c>0x80014444</c>.
/// Each picture is held for two vblanks, so a 60 Hz display scans it twice.
/// The boot load's VSync(3), the movie player's own hold and the VSync(-1)
/// polls stay on the disc's arguments. <c>KF2_FRAMECAP=native</c> leaves the
/// frame call alone too.
/// </summary>
public static class FrameRate
{
    public const uint FrameReturn = 0x80014444;
    public const uint MovieReturn = 0x800F2B24;
    const uint VSyncThunk = 0x800B0770;
    const uint ModeByte = 0x800DB4C4;

    static readonly ModInfo Self = new()
    {
        Id = "deception.framerate",
        Name = "Deception frame rate",
        Version = "1",
    };

    static bool _native;
    static bool _probe;
    static bool _queued;
    static bool _armed;
    static int _asked2;
    static int _asked3;
    static int _movie2;
    static int _movie4;
    static int _movieOther;
    static long _windowStart;

    /// <summary>True when the disc's 2- and 3-vblank waits are left in place.</summary>
    public static bool Native => _native;

    public static void Install()
    {
        var cap = Environment.GetEnvironmentVariable("KF2_FRAMECAP");
        _native = cap != null && cap.Equals("native", StringComparison.OrdinalIgnoreCase);
        var probe = Environment.GetEnvironmentVariable("KF2_FRAMECAP_PROBE");
        _probe = probe is "1";
        if (_native)
        {
            Console.WriteLine("[deception] frame cap: disc VSync(2) and VSync(3)");
            return;
        }

        Console.WriteLine("[deception] frame cap: two vblanks (30/s). KF2_FRAMECAP=native keeps the disc's wait.");
        HookAttach.OnOverlayLoad("frame rate", Attach,
            "The disc's 20/s and 30/s waits stay.");
    }

    static bool Attach()
    {
        if (_armed) return true;
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("main", null, VSyncThunk);
        if (target?.Name != "func_800B0770") return false;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        if (!_queued)
        {
            HookManager.AddPre(Self, target, typeof(FrameRate).GetMethod(nameof(OnVSync), flags)!);
            _queued = true;
        }

        if (!HookManager.IsCommitted(target))
            HookManager.Commit();
        if (!HookManager.IsCommitted(target)) return false;

        _armed = true;
        _windowStart = Environment.TickCount64;
        Console.WriteLine("[deception] frame cap: func_8001441C waits two vblanks.");
        return true;
    }

    /// <summary>
    /// Runs before <c>LibEtc.VSync</c> reads <c>A0</c>. Mode 3 from the frame
    /// function becomes 2. Mode 2 is already two vblanks. The movie player,
    /// the boot load and the VSync(-1) polls keep the argument they passed.
    /// </summary>
    public static void OnVSync(CpuContext c, IMemory m)
    {
        if (c.RA == MovieReturn)
        {
            if (!_probe) return;
            uint movie = c.A0;
            if (movie == 2) _movie2++;
            else if (movie == 4) _movie4++;
            else _movieOther++;
            Report(m);
            return;
        }

        if (c.RA != FrameReturn) return;
        uint asked = c.A0;
        if (asked != 2 && asked != 3) return;

        if (asked == 2) _asked2++;
        else _asked3++;
        if (asked == 3) c.A0 = 2;

        if (!_probe) return;
        Report(m);
    }

    static void Report(IMemory m)
    {
        long now = Environment.TickCount64;
        long elapsed = now - _windowStart;
        if (elapsed < 1000) return;
        int n2 = _asked2;
        int n3 = _asked3;
        int m2 = _movie2;
        int m4 = _movie4;
        int mo = _movieOther;
        _asked2 = 0;
        _asked3 = 0;
        _movie2 = 0;
        _movie4 = 0;
        _movieOther = 0;
        _windowStart = now;
        int total = n2 + n3;
        int movie = m2 + m4 + mo;
        double seconds = elapsed / 1000.0;
        byte mode = m.ReadU8(ModeByte);
        Console.WriteLine(
            $"[deception] frame {total / seconds:0}/s over {seconds:0.0}s, disc asked 2 x{n2} and 3 x{n3}, mode byte {mode}; movie {movie / seconds:0}/s asked 2 x{m2} and 4 x{m4}");
    }
}
