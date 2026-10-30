using System.Reflection;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Deception;

/// <summary>
/// Walk-versus-menu input for Kagero, plus stick and mouse magnitude on the
/// standing step. The walk latch is set when koku3 func_80113E80 ran. That
/// function is the standing-walk case of the player state switch. The latch is
/// published on the pre of the frame-end VSync, func_800B0770 when RA is
/// 0x80014444 (func_8001441C). Other VSync callers, including VSync(-1) polls,
/// do not publish it: they would clear a latch the next present still needs.
/// Present polls the pad during that VSync, after the pre, and the post writes
/// the buffer. func_80014510 then samples it, still inside func_8001441C, so
/// the next pass's holds are that write. The mover of a pass therefore reads
/// the previous pass's binding, and the first walk pass still has the menu set.
///
/// func_80114634 writes forward at the vector's +4 and strafe at +0.
/// func_801148F8 writes yaw at +2. Full deflection matches the unreduced step
/// from speed byte 0x800E7C53, not the halfword, which a diagonal shrinks.
/// s = (byte + 0x32) &amp; 0xFF. Forward is s, back is the game's /10 of 3*s,
/// strafe is the same of 4*s. Turn is the raw byte while L1 or R1 is held, and
/// the /10 of byte&lt;&lt;3 otherwise. An idle stick leaves the halfword, so the
/// D-pad and the keyboard stay on the game's step. The fraction under 1 carries.
///
/// Holding L2 skips both writers. func_801130F0 then runs func_80117444, the
/// retail free look. That path is not scaled. Right-stick Y and mouse Y are
/// unbound. The stock walk binding puts right-stick up/down on L2/R2, and a
/// tilt would hold free look, so startup removes that exact pair.
///
/// Mouse X is about one digital turn per eight pixels, times MouseSensitivityX.
/// A pass whose turn writer did not run discards the motion, so releasing L2
/// does not dump it as a turn. Escape captures while focused and mouse look is
/// on. A host popup, focus loss, or mouse look off releases. The pointer stays
/// when the tick stops. Picture not judged.
/// </summary>
public static class InputSupport
{
    static readonly ModInfo Self = new()
    {
        Id = "deception.input",
        Name = "Deception input",
        Version = "1",
    };

    // func_8001441C's call. The pre sees this RA; LibEtc.VSync does not rewrite it.
    const uint FrameVSyncReturn = 0x80014444u;

    const uint SpeedByte = 0x800E7C53u;
    // Live hold pointers. 0x800D0000 - 0x4838 is L1, - 0x4830 is R1.
    const uint L1Slot = 0x800CB7C8u;
    const uint R1Slot = 0x800CB7D0u;

    const uint WalkTick = 0x80113E80u;
    const uint StepFn = 0x80114634u;
    const uint TurnFn = 0x801148F8u;

    static volatile bool _latched;
    static volatile bool _walking;

    static float _fwdCarry, _strafeCarry, _turnCarry;
    static uint _stepVec, _turnVec;

    // One take per pass. The turn writer spends X. A pass that skips it discards.
    static bool _motionTaken;
    static bool _turnRan;
    static bool _turnUsed;
    static float _tickDx;

    /// <summary>True when the standing walk tick ran on the last completed pass.</summary>
    public static bool Walking => _walking;

    static int _installOnce;
    static bool _armed;
    static bool _padLive;
    static bool _padAnnounced;
    static bool _moveAnnounced;
    static bool _moveMissLogged;
    static bool _padQueued;

    // func_80014290 passes these to func_800C0A28. 0x22 bytes apart.
    const uint Pad0 = 0x800D4958u;
    const uint Pad1 = 0x800D497Au;
    const uint PadInit = 0x800C0A28u;
    const uint PadVSync = 0x800B0770u;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installOnce, 1) != 0) return;
        // koku3 loads after main. Keep trying until the pad hooks and the walk
        // hooks have both committed.
        HookAttach.OnOverlayLoad("input", Attach,
            "The host pad and the walk sticks will not reach the game.", 48);
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        if (!_armed)
        {
            Arm();
            _armed = true;
        }

        return AttachPad() && AttachMove();
    }

    static void Arm()
    {
        MigrateStockKeys();
        EnsureMenuKeys();
        UnbindStockLook();

        // Port 0 ignores PadKind. Pad is the menu set and PadWalk is the walk
        // set. Both keep the default axis indices, so the four axis floats stay
        // on LeftX/LeftY/RightX/RightY. Magnitude reads those floats, not the
        // button bits. The keyboard splits on the same latch: Keys while
        // walking, KeysMenu otherwise.
        ConfigManager.Port0PadBindings = () =>
        {
            var game = ConfigManager.Game;
            if (!Walking) return game.Pad ?? new GamepadBindings();
            return game.PadWalk ?? GamepadBindings.Walk();
        };
        ConfigManager.Port0KeyBindings = () =>
        {
            var game = ConfigManager.Game;
            if (!Walking) return game.KeysMenu ?? KeyBindings.Menu();
            return game.Keys ?? new KeyBindings();
        };

        // Escape toggles the pointer while focused and mouse look is on. A
        // host popup or focus loss releases it. The listener runs on the
        // window thread, which is the only thread GLFW will change the cursor on.
        Event.AddListener<KeyboardEvent>(OnKeyboard);
        HostWindow.FocusChanged += focused =>
        {
            if (!focused) HostWindow.MouseCaptured = false;
        };
    }

    /// <summary>
    /// The shared walk binding maps right-stick up/down onto L2/R2. L2 held
    /// is free look and skips the step writers, so that pair is removed when
    /// it is exactly the stock one. A player who bound a different combination
    /// keeps it. Reset writes the stock pair back; the next launch removes it.
    /// </summary>
    static void UnbindStockLook()
    {
        var pad = ConfigManager.Game.PadWalk;
        if (pad.L2 is not [100, 108] || pad.R2 is not [101, 109]) return;

        pad.L2 = [100];
        pad.R2 = [101];
        ConfigManager.SaveGame();
        Console.WriteLine(
            "[Deception] input: walk pad dropped right-stick up/down from L2/R2, " +
            "so a tilt does not hold free look.");
    }

    /// <summary>
    /// func_800C0A28 installs the two libpad buffers. Publishing before it
    /// returns would race the 0xFF fill. The VSync thunk is func_800B0770.
    /// </summary>
    static bool AttachPad()
    {
        var init = SymbolRegistry.Resolve("main", null, PadInit);
        var vsync = SymbolRegistry.Resolve("main", null, PadVSync);
        if (init?.Name != "func_800C0A28" || vsync?.Name != "func_800B0770")
            return false;

        if (!_padQueued)
        {
            var flags = BindingFlags.Public | BindingFlags.Static;
            var self = typeof(InputSupport);
            // Pre and post share one registration. Queue both before any retry,
            // or the pre's registration would hide the post.
            HookManager.AddPre(Self, vsync, self.GetMethod(nameof(OnPadVSyncBefore), flags)!);
            HookManager.AddPost(Self, init, self.GetMethod(nameof(OnPadReady), flags)!);
            HookManager.AddPost(Self, vsync, self.GetMethod(nameof(OnPadVSync), flags)!);
            _padQueued = true;
        }

        HookManager.Commit();
        return HookManager.IsCommitted(init) && HookManager.IsCommitted(vsync);
    }

    static bool AttachMove()
    {
        var tick = SymbolRegistry.Resolve("koku3", null, WalkTick);
        var step = SymbolRegistry.Resolve("koku3", null, StepFn);
        var turn = SymbolRegistry.Resolve("koku3", null, TurnFn);
        if (tick?.Name != "func_80113E80" || step?.Name != "func_80114634" || turn?.Name != "func_801148F8")
        {
            if (!_moveMissLogged && Dispatcher.Overlays.ContainsKey("koku3"))
            {
                _moveMissLogged = true;
                Console.Error.WriteLine(
                    "[Deception] input: walk hooks did not resolve " +
                    $"(0x80113E80={(tick == null ? "missing" : tick.Name)}, " +
                    $"0x80114634={(step == null ? "missing" : step.Name)}, " +
                    $"0x801148F8={(turn == null ? "missing" : turn.Name)}).");
            }

            return false;
        }

        var flags = BindingFlags.Public | BindingFlags.Static;
        var self = typeof(InputSupport);
        if (!HookManager.IsRegistered(tick))
            HookManager.AddPost(Self, tick, self.GetMethod(nameof(OnWalkTick), flags)!);
        if (!HookManager.IsRegistered(step))
        {
            HookManager.AddPre(Self, step, self.GetMethod(nameof(OnStepBefore), flags)!);
            HookManager.AddPost(Self, step, self.GetMethod(nameof(OnStep), flags)!);
        }

        if (!HookManager.IsRegistered(turn))
        {
            HookManager.AddPre(Self, turn, self.GetMethod(nameof(OnTurnBefore), flags)!);
            HookManager.AddPost(Self, turn, self.GetMethod(nameof(OnTurn), flags)!);
        }

        HookManager.Commit();
        var committed = HookManager.IsCommitted(tick) && HookManager.IsCommitted(step) && HookManager.IsCommitted(turn);
        if (committed && !_moveAnnounced)
        {
            _moveAnnounced = true;
            Console.WriteLine(
                "[Deception] input: koku3 walk scales func_80114634 (forward at +4, strafe at +0) " +
                "and func_801148F8 (turn at +2). Mouse X turns. Right-stick Y and mouse Y stay unbound.");
        }

        return committed;
    }

    public static void OnPadReady(CpuContext c, IMemory m)
    {
        _padLive = true;
        PublishPad(m);
    }

    /// <summary>
    /// Pre of func_800B0770. The frame-end call's RA is 0x80014444. Publishing
    /// here is before Present polls, so the post writes the walk or menu set
    /// the mover just earned. Any other caller leaves the latch for that call.
    /// </summary>
    public static void OnPadVSyncBefore(CpuContext c, IMemory m)
    {
        if (c.RA != FrameVSyncReturn) return;
        PublishWalk();
    }

    public static void OnPadVSync(CpuContext c, IMemory m)
    {
        if (!_padLive) return;
        PublishPad(m);
    }

    static void PublishWalk()
    {
        var now = _latched;
        _latched = false;
        _walking = now;
        _turnRan = false;

        // The walk hooks already consumed this pass's motion. Drain here only
        // when they did not, so menu motion does not become the next turn.
        if (!now && !_motionTaken)
            HostWindow.TakeMouseMotion();

        _motionTaken = false;
        _tickDx = 0f;
        _turnUsed = false;

        var mouse = MouseOn();
        if (!mouse || !HostWindow.Focused || PopupManager.AnyOpen)
        {
            if (HostWindow.MouseCaptured) HostWindow.MouseCaptured = false;
        }

        // Show the label while walking, and while the pointer is still held
        // after the tick stops. A host popup hides it.
        HostWindow.MouseCaptureHud = mouse && !PopupManager.AnyOpen &&
                                      (now || HostWindow.MouseCaptured);
    }

    static void PublishPad(IMemory m)
    {
        if (!_padAnnounced)
        {
            _padAnnounced = true;
            Console.WriteLine("[Deception] input: publishing host pad into 0x800D4958.");
        }

        // Port 0 is always present: the keyboard is the pad when no controller is.
        WritePad(m, Pad0, true, Controller.State,
            Controller.RightX, Controller.RightY, Controller.LeftX, Controller.LeftY, Controller.Analog);
        if (Controller.Connected2)
            WritePad(m, Pad1, true, Controller.State2,
                Controller.RightX2, Controller.RightY2, Controller.LeftX2, Controller.LeftY2, Controller.Analog2);
        else
            WritePad(m, Pad1, false, 0xFFFF, 0x80, 0x80, 0x80, 0x80, false);
    }

    /// <summary>
    /// func_80014510 builds <c>(buf[2] &lt;&lt; 8) | buf[3]</c> and inverts it.
    /// On the wire buf[2] is the first button byte (Select, L3, R3, Start, Up,
    /// Right, Down, Left) and buf[3] the second (L2, R2, L1, R1, Triangle,
    /// Circle, Cross, Square), which is the low byte of <c>Controller.State</c>
    /// then the high byte. The halfword at 0x800E5ED8 is that order, active
    /// high, so 0xF000 is the D-pad and the face buttons are 0x00F0. Sticks are
    /// the four bytes after the buttons: RightX, RightY, LeftX, LeftY.
    /// See "The pad's two button bytes were reversed" in NOTES.md.
    /// </summary>
    static void WritePad(IMemory m, uint buf, bool connected, ushort state,
        byte rx, byte ry, byte lx, byte ly, bool analog)
    {
        if (!connected)
        {
            m.WriteU8(buf, 0xFF);
            return;
        }

        m.WriteU8(buf, 0);
        m.WriteU8(buf + 1, analog ? (byte)0x73 : (byte)0x41);
        m.WriteU8(buf + 2, (byte)state);
        m.WriteU8(buf + 3, (byte)(state >> 8));
        m.WriteU8(buf + 4, analog ? rx : (byte)0x80);
        m.WriteU8(buf + 5, analog ? ry : (byte)0x80);
        m.WriteU8(buf + 6, analog ? lx : (byte)0x80);
        m.WriteU8(buf + 7, analog ? ly : (byte)0x80);
    }

    /// <summary>Post of func_80113E80. This pass is a walk pass. L2, and the gate
    /// func_801185C8, skip the turn writer; drop the mouse so it cannot burst.</summary>
    public static void OnWalkTick(CpuContext c, IMemory m)
    {
        _latched = true;
        if (!_turnRan) DiscardMotion();
    }

    /// <summary>Pre of func_80114634. The body reassigns A0.</summary>
    public static void OnStepBefore(CpuContext c, IMemory m) => _stepVec = c.A0;

    /// <summary>
    /// Post of func_80114634. A live left stick replaces the halfword with the
    /// unreduced full step times the shaped axis. Forward and back have
    /// different full steps. An idle axis leaves the game's halfword.
    /// </summary>
    public static void OnStep(CpuContext c, IMemory m)
    {
        if (!ConfigManager.Game.Analog)
        {
            _fwdCarry = _strafeCarry = 0f;
            return;
        }

        var vec = _stepVec != 0 ? _stepVec : c.A0;
        if (vec == 0)
        {
            _fwdCarry = _strafeCarry = 0f;
            return;
        }

        var s = (m.ReadU8(SpeedByte) + 0x32) & 0xFF;
        // Stick up is a negative LeftY. Forward is the positive halfword.
        var y = Shape(-Controller.LeftAxisY);
        if (y > 0f)
            Apply(m, vec + 4u, y, s, ref _fwdCarry);
        else if (y < 0f)
            Apply(m, vec + 4u, y, Tenth(3 * s), ref _fwdCarry);
        else
            _fwdCarry = 0f;

        var x = Shape(Controller.LeftAxisX);
        if (x != 0f)
            Apply(m, vec, x, Tenth(4 * s), ref _strafeCarry);
        else
            _strafeCarry = 0f;
    }

    /// <summary>Pre of func_801148F8. A0 is the output vector.</summary>
    public static void OnTurnBefore(CpuContext c, IMemory m) => _turnVec = c.A0;

    /// <summary>
    /// Post of func_801148F8. A live right stick replaces the yaw step. Mouse X
    /// is added either way. Shoulders up use the faster turn, matching the
    /// writer: a stick strafe past the button threshold holds L1 or R1.
    /// </summary>
    public static void OnTurn(CpuContext c, IMemory m)
    {
        _turnRan = true;
        var vec = _turnVec != 0 ? _turnVec : c.A0;
        if (vec == 0)
        {
            _turnCarry = 0f;
            DiscardMotion();
            return;
        }

        var raw = (int)m.ReadU8(SpeedByte);
        var full = Held(m, L1Slot) || Held(m, R1Slot) ? raw : Tenth(raw << 3);
        var perPixel = full > 0 ? full / 8f : 8f;
        var mouse = TakeTurnMouse(perPixel);
        var shaped = ConfigManager.Game.Analog ? Shape(Controller.RightAxisX) : 0f;
        Apply(m, vec + 2u, shaped, full, ref _turnCarry, mouse);
    }

    static void OnKeyboard(KeyboardEvent e)
    {
        if (!e.Pressed || e.Repeat || e.Key != (int)Silk.NET.Input.Key.Escape) return;
        if (PopupManager.AnyOpen || !HostWindow.Focused || !MouseOn()) return;
        HostWindow.MouseCaptured = !HostWindow.MouseCaptured;
    }

    /// <summary>
    /// An untouched RecompOne keyboard file still names one key per button.
    /// That loads, and when it is exactly <see cref="KeyBindings.Stock"/> it is
    /// rewritten to the walk layout so the new defaults actually take effect.
    /// A file that differs in any button is left as the player saved it.
    /// </summary>
    static void MigrateStockKeys()
    {
        if (!KeyBindings.Matches(ConfigManager.Game.Keys, KeyBindings.Stock())) return;

        ConfigManager.Game.Keys = new KeyBindings();
        ConfigManager.SaveGame();
        Console.WriteLine(
            "[Deception] keys: stock keyboard layout migrated to the walk defaults " +
            "(W/S move, A/D strafe, Q/E turn, arrows stay the D-pad).");
    }

    /// <summary>
    /// An older file has Keys and no KeysMenu. Keep that walk table. Fill the
    /// menu table in memory from it, and leave the file alone until a normal
    /// settings save.
    /// </summary>
    static void EnsureMenuKeys()
    {
        if (ConfigManager.GameFileHas("KeysMenu")) return;
        ConfigManager.Game.KeysMenu = KeyBindings.MenuFrom(ConfigManager.Game.Keys);
    }

    static bool MouseOn() => ConfigManager.Game.MouseLook;

    static bool Held(IMemory m, uint slot)
    {
        var ptr = m.ReadU32(slot);
        return ptr != 0 && m.ReadU16(ptr) != 0;
    }

    /// <summary>
    /// HI of the signed multiply by 0x66666667, then arithmetic &gt;&gt; 2.
    /// That is the game's own scale. s = 50 gives Tenth(150) = 15 and Tenth(200) = 20.
    /// </summary>
    static int Tenth(int n)
    {
        var hi = (int)(((long)n * unchecked((int)0x66666667)) >> 32);
        return hi >> 2;
    }

    static void EnsureTick()
    {
        if (_motionTaken) return;
        (_tickDx, _) = HostWindow.TakeMouseMotion();
        _motionTaken = true;
    }

    /// <summary>Spend this pass's mouse without adding it, so it cannot burst later.</summary>
    static void DiscardMotion()
    {
        EnsureTick();
        _turnUsed = true;
    }

    /// <summary>dx times one digital turn per eight pixels, times MouseSensitivityX.</summary>
    static float TakeTurnMouse(float perPixel)
    {
        EnsureTick();
        if (_turnUsed) return 0f;
        _turnUsed = true;
        if (!MouseOn() || !HostWindow.MouseCaptured) return 0f;
        return _tickDx * perPixel * SensitivityX();
    }

    static float SensitivityX()
    {
        var s = ConfigManager.Game.MouseSensitivityX;
        if (float.IsNaN(s) || s < 0f) return 0f;
        return s;
    }

    /// <summary>Past the deadzone, remapped so the edge is 0 and ±1 is full.
    /// StickCurve is a power on that remap. 1 skips the power.</summary>
    static float Shape(float axis)
    {
        var deadzone = Deadzone();
        var mag = MathF.Abs(axis);
        if (!(mag > deadzone)) return 0f;
        var t = (mag - deadzone) / (1f - deadzone);
        if (t > 1f) t = 1f;
        var curve = Curve();
        if (curve != 1f)
            t = MathF.Pow(t, curve);
        return MathF.CopySign(t, axis);
    }

    static float Deadzone()
    {
        var d = ConfigManager.Game.StickDeadzone;
        if (float.IsNaN(d)) return 0.15f;
        if (d < 0f) return 0f;
        if (d > 0.99f) return 0.99f;
        return d;
    }

    static float Curve()
    {
        var c = ConfigManager.Game.StickCurve;
        if (float.IsNaN(c) || c < 0f) return 1f;
        return c;
    }

    /// <summary>
    /// A live stick overwrites the halfword (the game already added its step).
    /// An idle stick keeps it, and <paramref name="extra"/>, the mouse, is added.
    /// The fractional part is carried so a slow deflection does not truncate to
    /// nothing; the carry is dropped when nothing is driving. The cast truncates
    /// toward zero. A sum past a signed halfword is clamped at the store, and
    /// that excess is not put back into the carry.
    /// </summary>
    static void Apply(IMemory m, uint addr, float shaped, float full, ref float carry, float extra = 0f)
    {
        if (shaped == 0f && extra == 0f)
        {
            carry = 0f;
            return;
        }

        var basis = shaped != 0f ? 0 : (int)ReadI16(m, addr);
        var sum = shaped * full + extra + carry;
        var whole = (int)sum;
        carry = sum - whole;
        WriteI16(m, addr, basis + whole);
    }

    static short ReadI16(IMemory m, uint addr) => (short)m.ReadU16(addr);

    static void WriteI16(IMemory m, uint addr, int value)
    {
        if (value > short.MaxValue) value = short.MaxValue;
        else if (value < short.MinValue) value = short.MinValue;
        m.WriteU16(addr, (ushort)(short)value);
    }
}
