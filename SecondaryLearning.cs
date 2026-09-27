using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace BlueMage
{
    /// <summary>
    /// Learn on hit through the secondary skillset. The game only lets a unit learn a "learn on hit" ability (Blue
    /// Magicks, Summons, Ultima) from its PRIMARY skillset, so a Monk with Mettle set as secondary cannot pick up
    /// Ultima. This adds the secondary skillset as a second chance, like FFT ReMixed does on the PSX.
    ///
    /// The engine does it in two steps, each hooked here:
    /// - Check (0x14030CD08): after an action with the learn-on-hit flag, walks every unit that was hit and, for the
    ///   first one whose primary skillset holds the ability unlearned and passes the ability's learn roll, returns its
    ///   index. The game then offers the unit the ability and, if accepted, calls
    /// - Learn (0x14020D190): sets the learned bit in the primary skillset and shows the "learned" message.
    /// When the check finds nobody, the hook runs the same walk over the secondary skillsets. When the learner's
    /// primary skillset does not hold the ability, the learn hook sets the bit in the secondary skillset's slot first,
    /// and the original then shows the message as usual. Both steps are stateless, so a declined offer or a suspend
    /// save in between cannot leave anything stale.
    ///
    /// Where a secondary skillset's learned bits live follows the game's own rule (IsLearned 0x1403170CC and the
    /// ability list filler 0x14030E368): generic skillsets 5..0x17 use job slot S - 5, the Dark Knight skillset 0xE0
    /// uses Dark Knight's slot, and the unit's own special skillset (unit+0x192, e.g. Ramza's Mettle or Blue
    /// Magicks) uses slot 0. Any other skillset counts as fully learned, so there is nothing to learn from it.
    /// </summary>
    public class SecondaryLearning
    {
        #region Unit layout

        private const int UNIT_COUNT = 21;
        private const int UNIT_SIZE = 0x200;

        private const int OFF_JOB = 0x03;
        private const int OFF_FLAGS = 0x06;             // bit 0x20: the unit never learns (the game skips it)
        private const int OFF_PRIMARY_SKILLSET = 0x12;
        private const int OFF_SECONDARY_SKILLSET = 0x13;
        private const int OFF_LEARNED = 0xA2;           // 3 bytes per job slot, bit 0x80 >> (n & 7) for ability n
        private const int OFF_SPECIAL_SKILLSET = 0x192; // the skillset of the unit's own special job, 0 for generics
        private const int OFF_HIT_FLAGS = 0x1BB;        // bit 0x02: hit by the action; the check rewrites it to 0x04

        private const byte NO_LEARNING = 0x20;
        private const byte WAS_HIT = 0x02;

        private const int ABILITIES_PER_SKILLSET = 24;

        #endregion

        #region Game ids

        private const int FIRST_GENERIC_SKILLSET = 5;       // Fundaments
        private const int LAST_GENERIC_SKILLSET = 0x17;     // Dance; Mimic has nothing to learn
        private const int DARK_KNIGHT_SKILLSET = 0xE0;
        private const int DARK_KNIGHT_JOB = 0xA0;

        // The check refuses Ultima through the Chapter 2-3 Mettle; kept for the secondary too.
        private const int MIDGAME_METTLE_SKILLSET = 0x1A;
        private const int ULTIMA = 0x9A;

        // Ability common data, 8 bytes per ability: +2 learn chance (%), +3 flags.
        private const int ABILITY_DATA_SIZE = 8;
        private const int ABILITY_LEARN_CHANCE = 2;
        private const int ABILITY_FLAGS = 3;
        private const byte LEARN_ON_HIT = 0x20;

        // The status mask the check uses for "can this unit learn right now" (dead, petrified and so on).
        private const int CANNOT_LEARN_STATUS_MASK = 4;

        #endregion

        #region Signatures

        // 0x14030CD08(casterIndex, ushort* outAbility) -> learner index or -1. The prologue runs up to the
        // learn-on-hit flag test, so the operands below are at fixed offsets from the start.
        private const string SIG_CHECK =
            "48 89 5C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 20 45 33 DB 4C 8B E2 44 39 1D ?? ?? ?? ?? " +
            "0F 85 ?? ?? ?? ?? 48 0F BF 35 ?? ?? ?? ?? 66 85 F6 0F 84 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 4C 8B EE " +
            "F6 44 F0 03 20";
        private const int CHECK_BLOCK_CMP = 0x1A;       // cmp [global], r11d: the check bails unless it is 0
        private const int CHECK_ABILITY_MOVSX = 0x27;   // movsx rsi, word [current ability id]
        private const int CHECK_ABILITY_DATA_LEA = 0x38;
        private const int CHECK_UNITS_LEA = 0x50;       // lea rbx, [units + 0x1BB]
        private const int CHECK_STATUS_CALL = 0x93;     // 0x140278FA8(unit, mask) -> nonzero if a status matches
        private const int CHECK_JOB_SLOT_CALL = 0xC3;   // 0x1402B8F18(job) -> job slot
        private const int CHECK_SKILLSET_CALL = 0x10F;  // 0x140275860(skillset, n) -> ability id
        private const int CHECK_ROLL_CALL = 0x132;      // 0x140278E68(100, chance) -> 0 when the roll passes

        // 0x14020D190(): learns the ability for the unit whose index the check result was stored in.
        private const string SIG_LEARN =
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 40 48 8B 05 ?? ?? ?? ?? " +
            "48 33 C4 48 89 44 24 38 0F B7 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 98 48 01 00 00 0F B6 4B 03 " +
            "44 0F B6 7B 12";
        private const int LEARN_INDEX_MOVZX = 0x27;     // movzx ecx, word [learner index]

        #endregion

        private delegate int CheckDelegate(int casterIndex, nint outAbility);
        private delegate void LearnDelegate();
        private delegate int StatusMatchDelegate(nint unit, int mask);
        private delegate int JobSlotDelegate(int job);
        private delegate ushort SkillsetAbilityDelegate(int skillset, int index);
        private delegate int RollDelegate(int range, int chance);

        private IHook<CheckDelegate>? _checkHook;
        private IHook<LearnDelegate>? _learnHook;
        private StatusMatchDelegate? _statusMatch;
        private JobSlotDelegate? _jobSlot;
        private SkillsetAbilityDelegate? _skillsetAbility;
        private RollDelegate? _roll;

        private readonly ILogger _logger;
        private readonly IModConfig _modConfig;
        private readonly IReloadedHooks _hooks;
        private readonly nint _baseAddress;

        private nint _checkBlocked;     // int, nonzero while the game allows no learning
        private nint _abilityId;        // short, the ability being resolved
        private nint _abilityData;      // ability common data table
        private nint _units;            // battle unit array
        private nint _learnerIndex;     // u16, unit index the check returned

        public SecondaryLearning(IReloadedHooks hooks, ILogger logger, IModConfig modConfig, IStartupScanner startupScanner)
        {
            _hooks = hooks;
            _logger = logger;
            _modConfig = modConfig;
            _baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;

            Scan(startupScanner, "Learn-on-hit check", SIG_CHECK, address =>
            {
                _checkBlocked = ResolveRipRelative(address + CHECK_BLOCK_CMP, 3, 7);
                _abilityId = ResolveRipRelative(address + CHECK_ABILITY_MOVSX, 4, 8);
                _abilityData = ResolveRipRelative(address + CHECK_ABILITY_DATA_LEA, 3, 7);
                _units = ResolveRipRelative(address + CHECK_UNITS_LEA, 3, 7) - OFF_HIT_FLAGS;

                _statusMatch = _hooks.CreateWrapper<StatusMatchDelegate>(ResolveCall(address + CHECK_STATUS_CALL), out _);
                _jobSlot = _hooks.CreateWrapper<JobSlotDelegate>(ResolveCall(address + CHECK_JOB_SLOT_CALL), out _);
                _skillsetAbility = _hooks.CreateWrapper<SkillsetAbilityDelegate>(ResolveCall(address + CHECK_SKILLSET_CALL), out _);
                _roll = _hooks.CreateWrapper<RollDelegate>(ResolveCall(address + CHECK_ROLL_CALL), out _);

                _checkHook = _hooks.CreateHook<CheckDelegate>(CheckImpl, address).Activate();
            });

            Scan(startupScanner, "Learn ability", SIG_LEARN, address =>
            {
                _learnerIndex = ResolveRipRelative(address + LEARN_INDEX_MOVZX, 3, 7);
                _learnHook = _hooks.CreateHook<LearnDelegate>(LearnImpl, address).Activate();
            });
        }

        /// <summary>
        /// Runs the game's primary-skillset check, and if nobody learns, the same check over secondary skillsets.
        /// The original rewrites every hit unit's flag as it goes, so who was hit is noted before calling it.
        /// </summary>
        private unsafe int CheckImpl(int casterIndex, nint outAbility)
        {
            int ability = *(short*)_abilityId;
            bool learnable = _learnHook != null && *(int*)_checkBlocked == 0 && ability > 0 &&
                             (AbilityData(ability)[ABILITY_FLAGS] & LEARN_ON_HIT) != 0;

            uint hit = 0;
            if (learnable)
            {
                for (int i = 0; i < UNIT_COUNT; i++)
                {
                    if (i != casterIndex && (Unit(i)[OFF_HIT_FLAGS] & WAS_HIT) != 0)
                        hit |= 1u << i;
                }
            }

            int learner = _checkHook!.OriginalFunction(casterIndex, outAbility);
            if (learner != -1 || hit == 0)
                return learner;

            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if ((hit & (1u << i)) == 0)
                    continue;

                byte* unit = Unit(i);
                if ((unit[OFF_FLAGS] & NO_LEARNING) != 0 || _statusMatch!((nint)unit, CANNOT_LEARN_STATUS_MASK) != 0)
                    continue;

                int index = FindUnlearned(unit, unit[OFF_SECONDARY_SKILLSET], SecondarySlot(unit), ability);
                if (index < 0 || _roll!(100, AbilityData(ability)[ABILITY_LEARN_CHANCE]) != 0)
                    continue;

                *(ushort*)outAbility = (ushort)ability;
                return i;
            }

            return -1;
        }

        /// <summary>
        /// Sets the learned bit in the secondary skillset's slot when the learner got the offer through it (its
        /// primary skillset does not hold the ability unlearned). The original then finds nothing to set in the
        /// primary skillset and just shows the "learned" message.
        /// </summary>
        private unsafe void LearnImpl()
        {
            int unitIndex = *(ushort*)_learnerIndex;
            int ability = *(short*)_abilityId;

            if (unitIndex < UNIT_COUNT && ability > 0)
            {
                byte* unit = Unit(unitIndex);
                if (FindUnlearned(unit, unit[OFF_PRIMARY_SKILLSET], _jobSlot!(unit[OFF_JOB]), ability) < 0)
                {
                    byte skillset = unit[OFF_SECONDARY_SKILLSET];
                    int slot = SecondarySlot(unit);
                    int index = FindUnlearned(unit, skillset, slot, ability);
                    if (index >= 0)
                    {
                        unit[OFF_LEARNED + slot * 3 + index / 8] |= (byte)(0x80 >> (index & 7));
                        Log($"unit {unitIndex} learned ability {ability} through secondary skillset {skillset} (slot {slot})");
                    }
                }
            }

            _learnHook!.OriginalFunction();
        }

        /// <summary>The job slot holding the secondary skillset's learned bits, or -1 if there is nothing to learn.</summary>
        private unsafe int SecondarySlot(byte* unit)
        {
            byte skillset = unit[OFF_SECONDARY_SKILLSET];
            if (skillset == 0 || skillset == 0xFF || skillset == unit[OFF_PRIMARY_SKILLSET])
                return -1;
            if (skillset >= FIRST_GENERIC_SKILLSET && skillset <= LAST_GENERIC_SKILLSET)
                return skillset - FIRST_GENERIC_SKILLSET;
            if (skillset == DARK_KNIGHT_SKILLSET)
                return _jobSlot!(DARK_KNIGHT_JOB);
            if (skillset == unit[OFF_SPECIAL_SKILLSET])
                return 0;
            return -1;
        }

        /// <summary>Index of the ability in the skillset if the unit has not learned it yet, else -1.</summary>
        private unsafe int FindUnlearned(byte* unit, int skillset, int slot, int ability)
        {
            if (slot < 0 || (skillset == MIDGAME_METTLE_SKILLSET && ability == ULTIMA))
                return -1;

            byte* learned = unit + OFF_LEARNED + slot * 3;
            for (int i = 0; i < ABILITIES_PER_SKILLSET; i++)
            {
                if ((learned[i / 8] & (0x80 >> (i & 7))) == 0 && _skillsetAbility!(skillset, i) == ability)
                    return i;
            }
            return -1;
        }

        private unsafe byte* Unit(int index) => (byte*)(_units + index * UNIT_SIZE);

        private unsafe byte* AbilityData(int ability) => (byte*)(_abilityData + ability * ABILITY_DATA_SIZE);

        private void Scan(IStartupScanner scanner, string name, string signature, Action<nint> onFound)
        {
            scanner.AddMainModuleScan(signature, result =>
            {
                if (!result.Found)
                {
                    Log($"{name}: signature not found, secondary skillset learning will not work (did the game update?)");
                    return;
                }

                try
                {
                    onFound(_baseAddress + result.Offset);
                }
                catch (Exception ex)
                {
                    Log($"{name}: {ex.Message}");
                }
            });
        }

        private void Log(string message) => _logger.WriteLine($"[{_modConfig.ModId}] Secondary learning: {message}");

        // Reads a rip-relative operand: target = instruction + instructionLength + disp32
        private static unsafe nint ResolveRipRelative(nint instruction, int displacementOffset, int instructionLength)
        {
            return instruction + instructionLength + *(int*)(instruction + displacementOffset);
        }

        private static unsafe nint ResolveCall(nint instruction)
        {
            if (*(byte*)instruction != 0xE8)
                throw new InvalidOperationException($"expected a call at {instruction:X}, the game code has changed");
            return ResolveRipRelative(instruction, 1, 5);
        }
    }
}
