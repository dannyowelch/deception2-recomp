using RecompOne.Runtime.Cdrom;

namespace RecompOne.Runtime.Memory;

public sealed class Dma
{
    private const uint Start = 0x01000000u;

    private readonly IMemory _mem;
    private readonly Gpu _gpu;
    private readonly Spu _spu;
    private readonly Mdec _mdec;
    private readonly Action _raiseIrq;
    private CdController? _cd;

    private uint _dicr;

    public Dma(IMemory mem, Gpu gpu, Spu spu, Mdec mdec, Action raiseIrq)
    {
        _mem = mem;
        _gpu = gpu;
        _spu = spu;
        _mdec = mdec;
        _raiseIrq = raiseIrq;
    }

    public void SetCd(CdController cd)
    {
        _cd = cd;
    }

    public uint ReadDicr()
    {
        return _dicr;
    }

    public void WriteDicr(uint val)
    {
        var flags = (_dicr >> 24) & 0x7Fu;
        flags &= ~((val >> 24) & 0x7Fu);
        _dicr = (val & 0x00FFFFFFu) | (flags << 24);
        UpdateMaster();
    }

    // The stream's per-sector enable is a byte read-modify-write of DICR+2
    // (bits 16-23). A 32-bit store is the only access that used to reach
    // _dicr, so that byte wrote a dead backing byte and the last sector never
    // raised. The read has to come back from _dicr: the modify writes the
    // byte it just read, and a zero from the backing store would clear the
    // master bit the word write had set. Bits 24-30 stay write-1-to-clear.
    public byte ReadReg8(uint phys)
    {
        var shift = (int)((phys - 0x1F8010F4u) & 3u) * 8;
        return (byte)(_dicr >> shift);
    }

    public void WriteReg8(uint phys, byte value)
    {
        var shift = (int)((phys - 0x1F8010F4u) & 3u) * 8;
        if (shift >= 24)
        {
            WriteDicr((_dicr & 0x00FFFFFFu) | ((uint)value << 24));
            return;
        }

        var mask = 0xFFu << shift;
        _dicr = (_dicr & ~(uint)mask) | ((uint)value << shift);
        UpdateMaster();
    }

    public ushort ReadReg16(uint phys)
    {
        var shift = phys == 0x1F8010F6u ? 16 : 0;
        return (ushort)(_dicr >> shift);
    }

    public void WriteReg16(uint phys, ushort value)
    {
        if (phys == 0x1F8010F6u)
        {
            WriteDicr((_dicr & 0x0000FFFFu) | ((uint)value << 16));
            return;
        }

        _dicr = (_dicr & 0xFFFF0000u) | value;
        UpdateMaster();
    }

    private void UpdateMaster()
    {
        var flags = (_dicr >> 24) & 0x7Fu;
        var enables = (_dicr >> 16) & 0x7Fu;
        var set = (_dicr & 0x8000u) != 0
                  || (((_dicr >> 23) & 1u) != 0 && (flags & enables) != 0);
        _dicr = set ? _dicr | 0x80000000u : _dicr & 0x7FFFFFFFu;
    }

    public void Run(int channel, uint madr, uint bcr, uint chcr)
    {
        Log.Dma($"ch{channel} madr=0x{madr:X8} bcr=0x{bcr:X8} chcr=0x{chcr:X8}");
        switch (channel)
        {
            case 0: TransferMdecIn(madr, bcr); break;
            case 1: TransferMdecOut(madr, bcr); break;
            case 2: TransferGpu(madr, bcr, chcr); break;
            case 3: TransferCd(madr, bcr); break;
            case 4: TransferSpu(madr, bcr, chcr); break;
            case 6: ClearOrderingTable(madr, bcr); break;
            default: return;
        }

        Complete(channel, channel == 3 ? WordCount(bcr) : 0);
    }

    private void TransferMdecIn(uint madr, uint bcr)
    {
        var words = WordCount(bcr);
        for (uint i = 0; i < words; i++)
            _mdec.Write0(_mem.ReadU32(madr + i * 4u));
    }

    private void TransferMdecOut(uint madr, uint bcr)
    {
        var words = WordCount(bcr);
        for (uint i = 0; i < words; i++)
            _mem.WriteU32(madr + i * 4u, _mdec.ReadData());
    }

    private void TransferGpu(uint madr, uint bcr, uint chcr)
    {
        var sync = (chcr >> 9) & 3u;
        if (sync == 2)
        {
            var addr = madr & Runtime.RamWordMask;
            for (var guard = 0; guard < 0x100000; guard++)
            {
                var header = _mem.ReadU32(addr);
                var count = header >> 24;
                for (uint i = 0; i < count; i++)
                {
                    var src = addr + 4u + i * 4u;
                    _gpu.WriteGp0(_mem.ReadU32(src), src);
                }

                var next = header & 0xFFFFFFu;
                if (next == 0xFFFFFFu || (next & 0x800000u) != 0) break;
                addr = next & Runtime.RamWordMask;
            }
        }
        else if ((chcr & 1u) != 0)
        {
            var words = WordCount(bcr);
            for (uint i = 0; i < words; i++)
            {
                uint src = madr + i * 4u;
                _gpu.WriteGp0(_mem.ReadU32(src), src);
            }
        }
        else
        {
            var words = WordCount(bcr);
            for (uint i = 0; i < words; i++)
                _mem.WriteU32(madr + i * 4u, _gpu.ReadData());
        }
    }

    private void TransferSpu(uint madr, uint bcr, uint chcr)
    {
        if ((chcr & 1u) == 0) return;
        var bytes = WordCount(bcr) * 4u;
        var buf = new byte[bytes];
        for (uint i = 0; i < bytes; i++)
            buf[i] = _mem.ReadU8(madr + i);
        _spu.DmaWrite(_spu.TransferAddrBytes(), buf);
    }

    private void TransferCd(uint madr, uint bcr)
    {
        if (_cd == null) return;
        _cd.DmaReadData(_mem, madr, WordCount(bcr) * 4u);
    }

    private void ClearOrderingTable(uint madr, uint bcr)
    {
        var count = bcr & 0xFFFFu;
        if (count == 0) return;
        // 0095. The same words, written straight into RAM when nothing watches the stores.
        if (_mem is PSMemory ps && ps.ClearOrderingTable(madr, count)) return;
        var addr = madr;
        for (uint i = 0; i < count - 1; i++)
        {
            _mem.WriteU32(addr, (addr - 4u) & 0x00FFFFFFu);
            addr -= 4u;
        }

        _mem.WriteU32(addr, 0x00FFFFFFu);
    }

    private static int _streamProbe;

    private void Complete(int channel, uint words)
    {
        // 0004. Not gated on DICR: the library routine that would have set those
        // bits is the same one LibApi reimplements, so it never runs to set them.
        // A callback exists only because the game asked for it, which is the same
        // condition the enable bits encode.
        Sdk.LibApi.Complete(channel);

        Log.Irq($"dma ch {channel} dicr = 0x{_dicr:X8}");
        var master = (_dicr & (1u << 23)) != 0;
        var enabled = (_dicr & (1u << (16 + channel))) != 0;
        // The STR player clears the channel-3 enable around the header and around
        // every body except the frame's last sector. The header has to stay
        // quiet so its id survives the check. A body still has to raise: the
        // only writer of ring status 2 is the DMA callback, and it has to run
        // after this caller's status store, including on the sectors that are
        // not the last one. The master bit is the callback registration.
        var streamBody = channel == 3 && master && words > 16;
        if ((!master || !enabled) && !streamBody)
        {
            if ((channel == 1 || channel == 3) && _streamProbe < 40)
            {
                _streamProbe++;
                Console.WriteLine($"[DMA] ch{channel} IRQ skipped dicr=0x{_dicr:X8} words={words}");
            }
            return;
        }
        _dicr |= 1u << (24 + channel);
        UpdateMaster();
        // Channel 3 is the CD stream. Its handler has to run after the
        // caller's status store, and that caller polls once while it arms
        // the rest of the sector. Other channels keep the immediate raise.
        if (channel == 3) Interrupts.RaiseAfterPolls(3, 1);
        else _raiseIrq();
        if ((channel == 1 || channel == 3) && _streamProbe < 40)
        {
            _streamProbe++;
            var prod = _mem.ReadU32(0x800EED64u);
            var cons = _mem.ReadU32(0x800EED68u);
            var wait = _mem.ReadU32(0x800EED6Cu);
            var gate = _mem.ReadU32(0x800EED5Cu);
            Console.WriteLine($"[DMA] ch{channel} raised words={words} prod={prod} cons={cons} wait={wait} gate={gate} {SlotStatus()}");
        }
    }

    private string SlotStatus()
    {
        var sb = new System.Text.StringBuilder(24);
        for (var i = 0; i < 8; i++)
        {
            if (i != 0) sb.Append(',');
            sb.Append(_mem.ReadU16(0x8018D450u + (uint)(i * 0x20)).ToString());
        }
        return sb.ToString();
    }

    private static uint WordCount(uint bcr)
    {
        var size = bcr & 0xFFFFu;
        var blocks = (bcr >> 16) & 0xFFFFu;
        var total = blocks == 0 ? size : size * blocks;
        return total == 0 ? 0x10000u : total;
    }
}