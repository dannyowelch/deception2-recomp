using Verdite.Launcher;

// Kagero: Deception II -- the shipped entry point.
//
// The launcher is Verdite Core's (tools/verdite-core/launcher): it settles where
// files live, asks for a disc, builds the game from it on first run and hands
// over. This is what it needs to know about this port. See docs/PACKAGING.md.
//
// The recompiler config, the game assembly and the funcmaps are still the
// Tecmo's Deception tree this port was copied from. They do not describe
// SLUS-00677. The disc check refuses that disc, and every other one, until
// Kagero's own executables are mapped.

return Launcher.Run(new LauncherGame
{
    Name = "Deception2",
    AppId = "deception2",
    GameTitle = "Kagero: Deception II",
    Serial = "SLUS-00677",

    // Discs a player is likely to reach for. The addresses in Deception.json
    // are the first game's. Any other serial is refused by the boot-file check;
    // these say which game it actually is.
    WrongDiscs = new Dictionary<string, string>
    {
        ["SLUS-00340"] =
            "This is Tecmo's Deception (SLUS-00340), which is a different game. " +
            "This port is Kagero: Deception II (SLUS-00677).",
        ["SLUS-90041"] =
            "This is the Kagero: Deception II demo (SLUS-90041). " +
            "This port is the retail disc (SLUS-00677).",
        ["SLUS-00158"] =
            "This is King's Field (SLUS-00158), which is a different game. " +
            "This port is Kagero: Deception II (SLUS-00677).",
        ["SLUS-00255"] =
            "This is King's Field II (SLUS-00255), which is a different game. " +
            "This port is Kagero: Deception II (SLUS-00677).",
    },

    RecompilerConfig = "Deception.json",
    // The game assembly, matching Deception.csproj. The launcher assembly is
    // Deception2Launcher so this process can load Deception.dll into the default
    // context without colliding with itself.
    GameAssembly = "Deception",
    PreviousAssemblies = ["KingsField2"],
    UpdateRepository = "dannyowelch/deception2-recomp",

    // Kagero's overlays are not identified. Treating the first game's overlay
    // names as play or as a return to the title would invent a session this
    // disc does not have.
    PlayAfter = _ => null,
}, args);
