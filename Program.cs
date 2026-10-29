using RecompOne.Runtime;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using Recompiled;

// Entry point for the Kagero: Deception II (SLUS-00677) port.
// Before the runtime reads settings.json or the memory cards.
// The window icon is the same picture embedded in the executable. Set before
// the window exists so the first frame already wears it; when the launcher has
// already opened the window, this still replaces whatever it set.
Runtime.AppId = "deception2";
var iconPath = Path.Combine(AppContext.BaseDirectory, "deception2.png");
if (File.Exists(iconPath))
    Runtime.SetIcon(File.ReadAllBytes(iconPath));

Deception.LegacyData.Migrate();
Deception.GraphicsOptions.Install();
Deception.PrimHeap.Install();
// The live Entry predates the call EntryWriter emits. Install is idempotent
// and attaches on the overlay load, which is after the symbols exist.
Deception.InputSupport.Install();

var log = Environment.GetEnvironmentVariable("KF2_LOG");
if (!string.IsNullOrWhiteSpace(log))
{
    var parts = log.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var all = parts.Any(p => p.Equals("all", StringComparison.OrdinalIgnoreCase));
    bool Has(string name) => all || parts.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
    Log.BiosOn = Has("bios");
    Log.CdOn = Has("cd");
    Log.GpuOn = Has("gpu");
    Log.DmaOn = Has("dma");
    Log.SdkOn = Has("sdk");
    Log.SpuOn = Has("spu");
    Log.MdecOn = Has("mdec");
    Log.VSyncOn = Has("vsync");
    Log.IrqOn = Has("irq");
}

var presentProbe = Environment.GetEnvironmentVariable("KF2_PRESENT_PROBE")?.Trim();
if (presentProbe is "1" or "2")
    GpuHle.PresentProbe = true;
if (presentProbe == "2")
    GpuHle.PresentVerdictProbe = true;

Deception.IntroCapture.Arm();

// 8 MB. The two primitive buffers are parked above the console's 2 MB, and a
// 2 MB guest would alias those stores back onto the start of RAM.
var memory = new PSMemory(MemoryMap.DevkitRamSize);
try
{
    Entry.Run(memory, args.Length > 0 ? args[0] : null);
}
catch (Exception e)
{
    Console.Error.WriteLine($"[Deception2] crashed: {e}");
    return 1;
}
return 0;