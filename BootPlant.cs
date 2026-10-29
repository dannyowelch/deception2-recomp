using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace Deception;

/// <summary>
/// Copies the two KOKU_DAT.BIN members the intro calls by link address.
/// Member 0x132 is the MDEC/stream player (TECMO.STR / OPENING.STR) linked at
/// 0x800F2800. Member 0x81 is the routine the title state calls at 0x801BE4DC,
/// linked at 0x801B2000. The boot executable jal's both and does not itself
/// name either destination, so the bytes have to be in guest RAM before main.
/// </summary>
public static class BootPlant
{
    public static void Apply(DiscFs fs, IMemory m)
    {
        var dat = fs.ReadFile("KOKU_DAT.BIN");
        Copy(m, dat, 0x99000, 0xAAC5C, 0x800F2800);
        Copy(m, dat, 0x40800, 0x13F7A, 0x801B2000);
        // Members 0xAE (file index 3, koku3) and 0xA9 (file index 2, koku2) are
        // linked at the same base as the player. Entry loads every overlay, and
        // either one covers the player, so the player's map entries would be
        // gone for the intro. Put the player back. The post-movie CdRead of
        // index 3 loads koku3, and new game loads index 2, by LBA.
        // See "New game loads directory index 2 over the player" in NOTES.md.
        Dispatcher.Unload("koku3");
        Dispatcher.Unload("koku2");
        Dispatcher.Load("mdec");
        Console.WriteLine("[Boot] KOKU_DAT.BIN member 0x132 -> 0x800F2800, member 0x81 -> 0x801B2000");
    }

    static void Copy(IMemory m, byte[] src, int offset, int size, uint dest)
    {
        var end = offset + size;
        var i = offset;
        var addr = dest;
        for (; i + 4 <= end; i += 4, addr += 4)
            m.WriteU32(addr, (uint)(src[i] | (src[i + 1] << 8) | (src[i + 2] << 16) | (src[i + 3] << 24)));
        for (; i < end; i++, addr++)
            m.WriteU8(addr, src[i]);
    }
}
