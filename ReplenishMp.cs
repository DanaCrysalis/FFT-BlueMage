using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace BlueMage
{
    /// <summary>
    /// Replenish MP: a reaction ability (443, the empty slot between Counter and Cup of Life) that restores MP equal
    /// to half of the HP damage the unit takes. It triggers like Gil Snapper (on HP damage, Brave% chance) and
    /// resolves like Absorb MP (an MP restore on the unit itself).
    ///
    /// The engine's reactions run in two steps. After damage is applied, a trigger check picks the reaction and
    /// stores its id and a value in the target's action context (unit+0x1CE and unit+0x1E6). When the reaction
    /// resolves, an effect routine switches on the id and turns the value into HP, MP, gil and so on.
    /// - Trigger: the Auto-Potion bit test next to Gil Snapper's is widened to include this ability's bit, so a
    ///   Replenish MP unit reaches the shared trigger routine, which this hook then retags and halves.
    /// - Effect: 443 has no case in the effect routine, so while it runs the id is shown as Absorb MP, whose case
    ///   restores MP equal to the stored value. The id is put back straight after, so the name shown is still ours.
    /// </summary>
    public class ReplenishMp
    {
        #region Abilities and unit layout

        private const int REPLENISH_MP = 443;   // 0x1BB
        private const int ABSORB_MP = 438;      // 0x1B6, the reaction whose MP-restore effect is reused
        private const int AUTO_POTION = 441;    // 0x1B9, the reaction whose trigger is borrowed

        // Equipped reaction/support/movement abilities are a bitfield from ability 0x1A6 at unit+0x94, most
        // significant bit first (see Dualcast).
        private const int RSM_FIRST_ABILITY = 0x1A6;
        private const int OFF_RSM_BITS = 0x94;

        private static int BitByte(int ability) => OFF_RSM_BITS + (ability - RSM_FIRST_ABILITY) / 8;
        private static byte Bit(int ability) => (byte)(0x80 >> ((ability - RSM_FIRST_ABILITY) & 7));

        // The target's action context (unit+0x1BE).
        private const int CTX_REACTION = 0x10;  // u16 reaction ability id
        private const int CTX_VALUE = 0x28;     // u16 the reaction's value; the trigger stores the HP damage here

        #endregion

        #region Signatures

        // 0x14030AB17, inside the apply-result routine 0x14030A488: after the Gil Snapper test, "test cl, 0x10"
        // picks Auto-Potion (cl = unit+0x96) and calls the trigger with 0x1B9. The immediate becomes 0x10 | our bit.
        private const string SIG_AUTO_POTION_TEST =
            "84 CA 74 ?? B9 B7 01 00 00 EB ?? F6 C1 10 74 ?? B9 B9 01 00 00 E8";
        private const int AUTO_POTION_TEST_IMM = 13;

        // 0x14030BE18(ability): the trigger shared by Gil Snapper and Auto-Potion. If the hit did HP damage and the
        // Brave roll passes (always, outside real execution), it stores the ability and the HP damage in the context.
        // Its MOVs lead to the context pointer (+6) and the target unit pointer (+0x20).
        private const string SIG_DAMAGE_TRIGGER =
            "40 53 48 83 EC 20 4C 8B 1D ?? ?? ?? ?? 0F B7 D9 41 80 7B 27 00 7D ?? 83 3D ?? ?? ?? ?? 00";
        private const int TRIGGER_CONTEXT_MOV = 0x06;
        private const int TRIGGER_UNIT_MOV = 0x20;

        // 0x1403095E4(): resolves a reaction's effect, switching on the reaction id global its MOVSX reads (+0xF).
        private const string SIG_REACTION_EFFECT =
            "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 0F BF 0D ?? ?? ?? ?? B8 B8 01 00 00 40 B7 01 66 3B C8";
        private const int EFFECT_REACTION_ID_MOVSX = 0x0F;

        #endregion

        private delegate void DamageTriggerDelegate(int ability);
        private delegate int ReactionEffectDelegate();

        private IHook<DamageTriggerDelegate>? _damageTriggerHook;
        private IHook<ReactionEffectDelegate>? _reactionEffectHook;

        private readonly ILogger _logger;
        private readonly IModConfig _modConfig;
        private readonly IReloadedHooks _hooks;
        private readonly nint _baseAddress;

        private nint _contextPointer;   // -> the target's action context
        private nint _unitPointer;      // -> the target unit
        private nint _reactionId;       // u16, the reaction being resolved

        public ReplenishMp(IReloadedHooks hooks, ILogger logger, IModConfig modConfig, IStartupScanner startupScanner)
        {
            _hooks = hooks;
            _logger = logger;
            _modConfig = modConfig;
            _baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;

            Scan(startupScanner, "Auto-Potion test", SIG_AUTO_POTION_TEST, address =>
                WidenBitTest(address + AUTO_POTION_TEST_IMM, Bit(AUTO_POTION), Bit(REPLENISH_MP)));

            Scan(startupScanner, "Damage reaction trigger", SIG_DAMAGE_TRIGGER, address =>
            {
                _contextPointer = ResolveRipRelative(address + TRIGGER_CONTEXT_MOV, 3, 7);
                _unitPointer = ResolveRipRelative(address + TRIGGER_UNIT_MOV, 3, 7);
                _damageTriggerHook = _hooks.CreateHook<DamageTriggerDelegate>(DamageTriggerImpl, address).Activate();
            });

            Scan(startupScanner, "Reaction effect", SIG_REACTION_EFFECT, address =>
            {
                _reactionId = ResolveRipRelative(address + EFFECT_REACTION_ID_MOVSX, 4, 8);
                _reactionEffectHook = _hooks.CreateHook<ReactionEffectDelegate>(ReactionEffectImpl, address).Activate();
            });
        }

        /// <summary>
        /// Arrives here with Auto-Potion's id for a Replenish MP unit too, since the widened test sends both. Retags
        /// the reaction and halves the stored damage, rounding up like Damage Split.
        /// </summary>
        private unsafe void DamageTriggerImpl(int ability)
        {
            byte* unit = *(byte**)_unitPointer;
            if ((ushort)ability != AUTO_POTION || unit == null || !Has(unit, REPLENISH_MP) || Has(unit, AUTO_POTION))
            {
                _damageTriggerHook!.OriginalFunction(ability);
                return;
            }

            byte* context = *(byte**)_contextPointer;
            ushort previousId = *(ushort*)(context + CTX_REACTION);
            ushort previousValue = *(ushort*)(context + CTX_VALUE);

            // Cleared so a passed roll can be told apart from an earlier Replenish MP still waiting to resolve.
            *(ushort*)(context + CTX_REACTION) = 0;
            _damageTriggerHook!.OriginalFunction(REPLENISH_MP);

            if (*(ushort*)(context + CTX_REACTION) == REPLENISH_MP)
            {
                *(ushort*)(context + CTX_VALUE) = (ushort)((*(ushort*)(context + CTX_VALUE) + 1) / 2);
            }
            else
            {
                *(ushort*)(context + CTX_REACTION) = previousId;
                *(ushort*)(context + CTX_VALUE) = previousValue;
            }
        }

        /// <summary>Resolves Replenish MP through Absorb MP's case: MP restored = the stored value.</summary>
        private unsafe int ReactionEffectImpl()
        {
            ushort* id = (ushort*)_reactionId;
            if (*id != REPLENISH_MP)
                return _reactionEffectHook!.OriginalFunction();

            *id = ABSORB_MP;
            try
            {
                return _reactionEffectHook!.OriginalFunction();
            }
            finally
            {
                *id = REPLENISH_MP;
            }
        }

        private static unsafe bool Has(byte* unit, int ability) => (unit[BitByte(ability)] & Bit(ability)) != 0;

        private unsafe void WidenBitTest(nint immediate, byte expected, byte extra)
        {
            byte* b = (byte*)immediate;
            if (*b != expected)
            {
                Warn($"Auto-Potion test holds 0x{*b:X2}, expected 0x{expected:X2}; left unpatched");
                return;
            }

            VirtualProtect(immediate, 1, PAGE_EXECUTE_READWRITE, out uint oldProtect);
            *b = (byte)(expected | extra);
            VirtualProtect(immediate, 1, oldProtect, out _);
        }

        private void Scan(IStartupScanner scanner, string name, string signature, Action<nint> onFound)
        {
            scanner.AddMainModuleScan(signature, result =>
            {
                if (!result.Found)
                {
                    Warn($"{name}: signature not found, Replenish MP will not work (did the game update?)");
                    return;
                }

                try
                {
                    onFound(_baseAddress + result.Offset);
                }
                catch (Exception ex)
                {
                    Warn($"{name}: {ex.Message}");
                }
            });
        }

        private void Warn(string message) => _logger.WriteLine($"[{_modConfig.ModId}] Replenish MP: {message}");

        // Reads a rip-relative operand: target = instruction + instructionLength + disp32
        private static unsafe nint ResolveRipRelative(nint instruction, int displacementOffset, int instructionLength)
        {
            return instruction + instructionLength + *(int*)(instruction + displacementOffset);
        }

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
    }
}
