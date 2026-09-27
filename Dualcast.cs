using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace BlueMage
{
    /// <summary>
    /// Dualcast: a unit with the Dualcast support ability (481) can ready two spells at once, as in the Lion War
    /// ReMixed PSX hack.
    ///
    /// The engine gives each unit a single charge slot (+0x18D). After a unit commits a qualifying spell, its acted
    /// flag is cleared so it gets a second pick. When a second spell is committed, whichever resolves sooner keeps the
    /// engine's slot and the other is held here, then handed over once the slot frees; from then on the engine charges
    /// and fires it itself. The held spell is added to the turn order so the timeline shows both, and selecting it
    /// there shows its own target. While the second spell is being chosen, the timeline previews the candidate by
    /// committing it into the slot, so the first spell is added back to that preview. A held spell is dropped if its
    /// caster is KO'd, crystallised, petrified or turned to treasure before it can start charging.
    ///
    /// During the second pick the Abilities menu lists only what can be readied alongside the first spell, plus a
    /// Cancel Casting row (JobCommand 4, named by this mod's jobcommand.en.nxd) that drops the readied spell without
    /// spending the unit's action.
    ///
    /// Everything that reacts to a commit is gated on the simulation flag: the game builds its turn order by
    /// committing actions for real and reverting them, so without that gate one cast would queue hundreds of spells.
    ///
    /// Game-update note: every function is found by signature, but a few data structures are addressed by RVA (the
    /// RVA_* constants below). Those are the first things to re-check after a patch.
    /// </summary>
    public class Dualcast
    {
        // Development only. On: every diagnostic goes to the Reloaded console and to dualcast.log in the mod folder,
        // appended across sessions. Off (release): no log file is created at all, and the console only ever hears
        // about problems - a signature not found after a game update, or an exception.
        private static readonly bool Verbose = false;

        #region Ability and unit layout

        // Which ability counts as Dualcast. Equipped reaction/support/movement abilities are a bitfield running from
        // ability 0x1A6 at unit+0x94, most significant bit first, so the byte and bit are derived from the id. This
        // matches the engine's own checks: Half of MP 0x1CE -> +0x99 bit 0x80, Swiftspell 0x1E2 -> +0x9B bit 0x08.
        private const int DUALCAST_ABILITY = 481;      // 0x1E1, an empty Support slot between Reequip and Swiftspell
        private const int RSM_FIRST_ABILITY = 0x1A6;
        private const int OFF_RSM_BITS = 0x94;

        private static int DualcastByte => OFF_RSM_BITS + (DUALCAST_ABILITY - RSM_FIRST_ABILITY) / 8;
        private static byte DualcastBit => (byte)(0x80 >> ((DUALCAST_ABILITY - RSM_FIRST_ABILITY) & 7));

        private const int UNIT_COUNT = 21;
        private const int UNIT_STRIDE = 0x200;

        private const int OFF_STATUS = 0x61;    // bit 0x08 = Charging, 0x20 = Dead, 0x40 = Crystal
        private const int OFF_STATUS2 = 0x62;   // bit 0x80 = Petrify, 0x01 = Treasure
        private const int OFF_SUPPORT_CHARGE = 0x9B;   // bit 0x04 = Non-charge
        private const int OFF_CHARGE = 0x18D;   // 0xFF when not charging
        private const int OFF_ACTION = 0x1A0;   // pending action block
        private const int ACTION_LENGTH = 0x14; // what the engine itself copies in and out of the block
        private const int ACT_COMMAND = 0x01;   // skillset id, within the action block
        private const int ACT_ABILITY = 0x02;   // u16, within the action block
        private const int OFF_TURN_ACTIVE = 0x1B8;  // 1 while it is the unit's turn
        private const int OFF_MOVED = 0x1B9;    // bit 0x01 moved, 0x02 acted (for ending the turn)
        private const int OFF_ACTED = 0x1BA;    // bit 0x01 set once the unit has acted
        private const int OFF_UNIT_ID = 0x1BC;  // the id the sprite system knows the unit by
        private const byte STATUS_CHARGING = 0x08;
        private const byte STATUS_GONE = 0x60;         // Dead | Crystal
        private const byte STATUS2_GONE = 0x81;        // Petrify | Treasure
        private const byte SUPPORT_NON_CHARGE = 0x04;
        private const byte NOT_CHARGING = 0xFF;
        private const byte ACTED = 0x01;

        // The sprite actor's copy of the action it is about to carry out, in the same layout as the unit's block.
        private const int ACTOR_ACTION = 0x178;

        // Ability action data, 20 bytes per ability, from GetAbilityActionData.
        private const int ABILITY_CT = 0x0C;
        private const int ABILITY_MP = 0x0D;
        private const int ABILITY_FLAGS3 = 0x05;
        private const byte ABILITY_FLAG_NO_CHARGE = 0x04;
        private const int ABILITY_MAX_WITH_DATA = 0x16F;

        // SetChargingStatus kinds: 5 starts a charge from the ability's full charge time, 0 clears it.
        private const byte CHARGE_KIND_SET = 5;
        private const byte CHARGE_KIND_CLEAR = 0;

        #endregion

        #region Signatures and the data they lead to

        // 0x140281434 CommitAction(context), context byte 0 = unit index. The unit array comes from its LEA.
        private const string SIG_COMMIT_ACTION =
            "48 89 5C 24 08 57 48 83 EC 20 0F B6 19 48 8D 05 ?? ?? ?? ??";
        private const int COMMIT_UNIT_ARRAY_LEA = 0x0D;

        // 0x140310548 MarkTurnFlags(unit, moved, acted): records a move or an action (+0x1B9 bit 0x01, +0x1BA), then
        // ends the turn outright if the unit has now both moved and acted. The action path calls it with acted = 1
        // just BEFORE CommitAction, so a unit that moved first would have its turn over before a second pick opens.
        private const string SIG_MARK_TURN_FLAGS =
            "40 53 48 83 EC 20 48 63 D9 48 8D 05 ?? ?? ?? ?? 48 8B CB 48 C1 E1 09 48 03 C8 80 79 01 FF 75 09 83 C8 FF " +
            "48 83 C4 20 5B C3 85 D2 74 07";

        // 0x14030F030: one step of the battle state machine. Its result's high byte says what happened to the unit in
        // the low byte: 0x100 its turn starts, 0x200 its charged action fires, 0x300 a turn start with status effects
        // to apply first (and a few other events). The CT loop runs outside any unit's menu.
        private const string SIG_EXECUTE_ACTION =
            "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 54 41 55 41 56 41 57 48 83 EC 20 33 F6 4C 8D 2D ?? ?? ?? ??";
        private const int STEP_KIND_MASK = 0xFF00;
        private const int STEP_TURN = 0x100;
        private const int STEP_TURN_WITH_STATUS = 0x300;

        // Battle saves (suspend and auto-save) copy the whole unit array, 0x2A00 bytes, into a save buffer:
        // 0x1402CDBC4 BattleSave(?) fills it, and 0x1402CE5C8 / 0x1402CE7A8 restore the units from it. Held spells
        // live only in this mod, so they are stored beside the save in a file of our own, keyed by a fingerprint of
        // that buffer, and looked up again when the same save is loaded.
        private const string SIG_BATTLE_SAVE =
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC 40 03 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 " +
            "48 89 84 24 30 03 00 00 48 8B 05 ?? ?? ?? ?? 89 0D ?? ?? ?? ?? B9 32 00 00 00";
        private const string SIG_BATTLE_LOAD =
            "48 83 EC 28 8B 15 ?? ?? ?? ?? 45 33 C0 41 8D 48 32 E8 ?? ?? ?? ?? 8B 15 ?? ?? ?? ?? 45 33 C0 41 8D 48 33 E8";
        private const string SIG_BATTLE_LOAD_2 =
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC 30 03 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 " +
            "48 89 84 24 20 03 00 00 BE 01 00 00 00 48 8D 0D ?? ?? ?? ?? 8B D6 E8";

        // Inside 0x1402CE5C8: "mov r8d, 0x2A00 / lea rdx, buffer / lea rcx, units / call copy".
        private const string SIG_SAVE_BUFFER_COPY = "41 B8 00 2A 00 00 48 8D 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8";
        private const int SAVE_BUFFER_LEA = 0x06;
        private const int SAVED_UNITS_LENGTH = UNIT_COUNT * UNIT_STRIDE;

        private const string SAVED_HOLDS_FILE = "dualcast-saves.txt";
        private const int SAVED_HOLDS_KEPT = 100;   // most recent saves remembered

        // 0x14030FE60, not hooked: its MOV leads to the mode flag (0 = real, 1 = turn-order simulation,
        // 2 = action prediction, which is also what menus run in).
        private const string SIG_PREDICT_ACTION =
            "48 89 5C 24 20 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 40 4C 8D B1 A0 01 00 00";
        private const int PREDICT_MODE_FLAG_MOV = 0x6F;

        // 0x140279370 SetChargingStatus(unit, kind). Only refreshes the unit's sprite (0x1401FB064) when the mode
        // flag is 0.
        private const string SIG_SET_CHARGING =
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 20 44 8A 71 61";

        // 0x1402BB060 GetAbilityActionData(id) -> 20-byte record.
        private const string SIG_GET_ABILITY_DATA =
            "40 53 48 83 EC 20 48 63 D9 81 FB 6F 01 00 00 77 ?? 80 3D ?? ?? ?? ?? 00";

        // 0x14030FFC4: builds the turn order the timeline is drawn from, into 0x28 four-byte entries cleared to
        // 0xFFFFFFFF. entry[0] = unit index (low 5 bits) | 0x40 for a charged action | 0x80 = ninth bit of the
        // ability id, entry[1] = the ability id's low byte, u16 entry[2] = the time it sorts on.
        private const string SIG_TURN_ORDER =
            "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 54 41 55 41 56 41 57 48 83 EC 70 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 68";
        private const int TURN_ENTRIES = 0x28;
        private const int TURN_ENTRY_SIZE = 4;
        private const byte TURN_UNIT_MASK = 0x1F;
        private const byte TURN_FLAG_CHARGED = 0x40;
        private const byte TURN_FLAG_ABILITY_HIGH = 0x80;
        private const int TURN_TICK = 0x100;           // one charge tick, in the time units entries sort on
        private const uint TURN_ENTRY_EMPTY = 0xFFFFFFFF;
        private const byte TURN_KIND_MASK = 0x60;      // 0x40 alone marks a charged action (as the timeline tests it)
        // "cmp dword [return state], 9" at +0x32: the state the battle clock resumes after the current turn. The
        // clock's own state is the dword after it.
        private const int TURN_ORDER_RETURN_STATE_CMP = 0x32;
        private const int CLOCK_STATE_AFTER_RETURN = 4;   // the clock's state dword follows the return state
        // Clock states: 0 CT fill, 1 turns, 2 charge countdown, 3 fire any charge at 0, 4-6 status timers and
        // upkeep, 9 after a fired action (back to 3), 10 a unit's turn.
        private const int CLOCK_FIRE = 3;
        private const int CLOCK_AFTER_ACTION = 9;

        // 0x14030FD90 TurnOrderSlot(slot): builds a fresh turn order and returns that slot's unit, | 0x100 for a
        // charged action. Two timeline views use it to pick the row whose target they then show.
        private const string SIG_TURN_ORDER_SLOT =
            "40 53 48 81 EC D0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 C0 00 00 00 48 8B 05 ?? ?? ?? ?? " +
            "45 33 C0 48 63 D9 33 D2 48 8D 4C 24 20 C7";

        // 0x14027FFE8 ShowActionTarget(block): highlights the target area of a 0x14-byte action block, which it copies
        // before use. Every timeline view passes the row's unit+0x1A0, the one action a unit can describe.
        private const string SIG_SHOW_ACTION_TARGET =
            "48 83 EC 48 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 38 BA 14 00 00 00 4C 8B C1 44 8B CA 48 8D 4C 24 20 " +
            "E8 ?? ?? ?? ?? 0F B6 44 24 20 3C 15";

        // 0x1401FA82C FocusActionTarget(actor): points the cursor and camera at the target of the actor's unit's
        // action block (+0x1A0) - a tile when the target type is 5, otherwise the target unit's actor. Every timeline
        // view calls it just before ShowActionTarget.
        private const string SIG_FOCUS_ACTION_TARGET =
            "48 83 EC 28 48 8B 81 48 01 00 00 4C 8B C1 80 B8 AA 01 00 00 05 75";

        // Inside 0x140209738, the Combat Timeline's own view: a charged row was selected, and "mov rcx, [rsi+0x148]"
        // (at +0x12) loads the unit whose block is passed to ShowActionTarget two instructions later.
        private const string SIG_TIMELINE_SHOW_TARGET =
            "40 80 E7 60 40 80 FF 40 75 ?? 48 8B CE E8 ?? ?? ?? ?? 48 8B 8E 48 01 00 00 48 81 C1 A0 01 00 00 E8";
        private const int TIMELINE_SHOW_TARGET_HOOK = 0x12;

        // Just before it, the same view stores the two halves of the selected row's index (scroll and cursor, the MOVs
        // at +0 and +6) and loads its copy of the turn order (the LEA at +0x39); the row is copy[scroll + cursor].
        private const string SIG_TIMELINE_SELECTION =
            "89 2D ?? ?? ?? ?? 44 89 35 ?? ?? ?? ?? 41 39 A8 68 34 00 00 75 ?? 45 39 B0 78 34 00 00 75 ?? " +
            "38 1D ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? 41 8D 04 2E 40 88 3D ?? ?? ?? ?? 48 63 C8 4C 8D 25 ?? ?? ?? ??";
        private const int TIMELINE_SCROLL_MOV = 0x00;
        private const int TIMELINE_CURSOR_MOV = 0x06;
        private const int TIMELINE_ROWS_LEA = 0x39;

        // 0x1403167E4, not hooked: its LEA leads to the unit ability-entry blocks. 0x22 four-byte entries per block
        // (ability id in the low 10 bits of [0..1], command in [2], 0xFF = end), block id per unit at base+0x1998.
        private const string SIG_ABILITY_ENTRIES =
            "48 89 5C 24 18 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 20 4C 63 F9 48 8D 05 ?? ?? ?? ??";
        private const int ABILITY_ENTRIES_LEA = 0x37;
        private const int ENTRY_BLOCK_IDS = 0x1998;
        private const int ENTRY_BLOCKS = 0xEF8;
        private const int ENTRIES_PER_BLOCK = 0x22;
        private const int ENTRY_SIZE = 4;
        private const int ENTRY_COMMAND = 2;
        private const ushort ENTRY_ABILITY_MASK = 0x3FF;

        // 0x14030E010(unit, list): decides the Abilities menu's rows - Attack, primary, secondary, then 2 for
        // Evasive Stance and 3 for Reequip, 0xFF-terminated - and fills each row's type and blocked flag.
        private const string SIG_FILL_COMMANDS =
            "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 56 48 83 EC 20 4C 8B C2";
        private const int ROW_LIST_MAX = 6;
        private const byte COMMAND_CANCEL_CASTING = 4;   // an empty JobCommand slot in the vanilla game

        // 0x14030E368(unit, command, ids u16*, mp u8*, ct u8*, -, flags u8*, turn u8*): lists one skillset's
        // abilities and returns the count, ids ending in 0xFFFF. It reads stack arguments 5, 7 and 8 only; the
        // delegate carries a ninth anyway, because forwarding a slot too many is harmless and one too few is not.
        private const string SIG_FILL_SKILLSET = "40 53 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 18 01 00 00";
        private const int ABILITY_LIST_MAX = 0x40;

        // 0x14023678C, not hooked: builds the ability list a command opens. Its MOV at +0x11 leads to the pointer to
        // that list's item ids (0x7000 | ability). A near-identical copy for another screen uses the same pointer.
        private const string SIG_ABILITY_LIST_BUILD =
            "40 53 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 48 4C 8B 05 ?? ?? ?? ?? 4C 8D 0D ?? ?? ?? ?? " +
            "4C 0F BF 15 ?? ?? ?? ?? 44 8B F1 4C 89 05 ?? ?? ?? ?? 49 8D 80 A4 00 00 00 48 89 05 ?? ?? ?? ?? " +
            "49 8D 90 48 01 00 00";
        private const int ABILITY_LIST_PTR_MOV = 0x11;
        private const int ITEM_WIDGET_MASK = 0xF800;
        private const int ITEM_WIDGET_ABILITY = 0x7000;
        private const int ITEM_ABILITY_MASK = 0x7FF;

        // 0x1400FBB0C(unit, command) -> 5 for the unit's primary, 6 for its secondary, else 0. Its only callers are
        // the tooltip routers, which give 5/6 the job-command tooltip and anything unknown no tooltip at all.
        private const string SIG_COMMAND_SLOT =
            "83 F9 14 77 ?? 48 63 C9 48 8D 05 ?? ?? ?? ?? 48 C1 E1 09 48 03 C8";
        private const byte SLOT_PRIMARY = 5;

        // 0x14022BA70(ui, index): a menu item was activated. It does not yield. (The menu loop around it,
        // 0x14022CBE4, switches coroutine stacks and must never have a managed frame across it.) The item's handler
        // word is [ui+0x28][index]; a negative value -N makes the menu go back N levels.
        private const string SIG_ROW_ACTIVATED =
            "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 55 41 56 41 57 48 83 EC 20 83 25 ?? ?? ?? ?? 00";
        private const int UI_HANDLERS = 0x28;
        private const short HANDLER_BACK_ONE = -1;

        // 0x140260ABC(unitId) -> the unit's sprite actor; 0x1401FAF54(actor) applies its pending status changes.
        private const string SIG_GET_ACTOR =
            "48 8B 15 ?? ?? ?? ?? 48 85 D2 74 ?? 48 8B 82 48 01 00 00 48 85 C0 74 ?? 0F B6 80 BC 01 00 00 66 3B C1";
        private const string SIG_APPLY_ACTOR_STATUS =
            "48 85 C9 0F 84 ?? ?? ?? ?? 48 89 5C 24 08 57 48 83 EC 20 8B 81 6C 01 00 00";

        // 0x14028F728: assigns a C string to a string object (rcx = object, rdx = text). Battle message templates
        // pass through it before their placeholders are filled. Three identical copies exist; all are hooked.
        private const string SIG_STRING_ASSIGN =
            "40 53 48 83 EC 20 48 8B D9 49 83 C8 FF 49 FF C0 42 80 3C 02 00 75 ?? 48 83 C1 18";

        #endregion

        #region Data addressed by RVA (re-check these after a game update)

        private const int RVA_ROW_BLOCKED = 0x184A9A0;       // per Abilities row: 1 = refused, 0 = normal
        private const int RVA_ROW_TYPES = 0x184A9A8;         // per Abilities row: low nibble of the skillset type
        private const int RVA_SKILLSET_TYPES = 0x67E010;     // per skillset id
        private const int RVA_ROW_WIDGET_IDS = 0x783210;     // dwords of 0xB000 + command id; shared by every menu
        private const int RVA_ABILITIES_MENU = 0x802A80;     // the Abilities menu's object
        private const int RVA_ABILITY_LIST_MENU = 0x802A28;  // the object of the list a command opens
        private const int RVA_MENU_STATE_PTR = 0xD40950;     // -> menu state; +0x158 = ability list cursor
        private const int RVA_ABILITIES_CURSOR = 0x7DD1E2;   // remembered Abilities cursor, one byte per unit ...
        private const int RVA_UI_BUSY = 0x2FF9F24;           // makes sprite updates wait while a menu is open
        private const int ABILITIES_CURSOR_STRIDE = 0x11;    // ... every 0x11 bytes
        private const int MENU_LIST_CURSOR = 0x158;
        private const int ROW_WIDGET_BASE = 0xB000;
        private const int ROW_WIDGET_COUNT = 8;

        #endregion

        #region Text

        // The vanilla "readying" prompt and what it says instead while a unit chooses its second spell. The
        // placeholder is the game's own and is filled in with the readied spell's name.
        private const string READYING_TEMPLATE = "The unit is readying <string=lstr0>. Using another ability";
        private const string READYING_REPLACEMENT =
            "The unit is readying <string=lstr0>. Only magicks that can be readied alongside it are shown; " +
            "choose one to cast both, or Cancel Casting to drop it.";

        #endregion

        #region Delegates and hooks

        private delegate int CommitActionDelegate(nint context);
        private delegate nint ExecuteActionDelegate(nint arg1, nint arg2, nint arg3, nint arg4);
        private delegate nint SetChargingDelegate(nint unit, nint kind, nint arg3, nint arg4);
        private delegate nint GetAbilityActionDataDelegate(int abilityId);
        private delegate nint TurnOrderDelegate(nint entries, nint arg2, nint arg3);
        private delegate nint StringAssignDelegate(nint target, nint text);
        private delegate int FillCommandsDelegate(int unitIndex, nint list);
        private delegate int FillSkillsetDelegate(int unitIndex, int command, nint ids, nint mp, nint ct, nint arg6,
            nint flags, nint turn, nint arg9);
        private delegate byte CommandSlotDelegate(int unitIndex, int command);
        private delegate nint RowActivatedDelegate(nint ui, int index);
        private delegate nint GetActorDelegate(int unitId);
        private delegate void ApplyActorStatusDelegate(nint actor);
        private delegate int MarkTurnFlagsDelegate(int unitIndex, int moved, int acted);
        private delegate int TurnOrderSlotDelegate(int slot);
        private delegate int ShowActionTargetDelegate(nint block);
        private delegate void FocusActionTargetDelegate(nint actor);
        private delegate nint BattleSaveDelegate(nint arg1);
        private delegate nint BattleLoadDelegate(nint arg1);

        private IHook<CommitActionDelegate>? _commitHook;
        private IHook<MarkTurnFlagsDelegate>? _markTurnFlagsHook;
        private IHook<TurnOrderSlotDelegate>? _turnOrderSlotHook;
        private IHook<ShowActionTargetDelegate>? _showActionTargetHook;
        private IAsmHook? _timelineShowTargetHook;
        private IHook<BattleSaveDelegate>? _battleSaveHook;
        private IHook<BattleLoadDelegate>? _battleLoadHook;
        private IHook<BattleLoadDelegate>? _battleLoad2Hook;
        private IHook<ExecuteActionDelegate>? _executeActionHook;
        private IHook<TurnOrderDelegate>? _turnOrderHook;
        private IHook<FillCommandsDelegate>? _fillCommandsHook;
        private IHook<FillSkillsetDelegate>? _fillSkillsetHook;
        private IHook<CommandSlotDelegate>? _commandSlotHook;
        private IHook<RowActivatedDelegate>? _rowActivatedHook;
        private readonly List<IHook<StringAssignDelegate>> _stringAssignHooks = new();
        private readonly List<StringAssignDelegate> _stringAssignImpls = new();   // kept alive for the hooks

        private SetChargingDelegate? _setCharging;
        private GetAbilityActionDataDelegate? _getAbilityActionData;
        private GetActorDelegate? _getActor;
        private ApplyActorStatusDelegate? _applyActorStatus;
        private FocusActionTargetDelegate? _focusActionTarget;

        #endregion

        #region State

        private readonly IModLoader _modLoader;
        private readonly IReloadedHooks _hooks;
        private readonly ILogger _logger;
        private readonly IModConfig _modConfig;
        private readonly nint _baseAddress;
        private readonly object _lock = new();

        private nint _unitArray;
        private nint _modeFlag;
        private nint _returnState;
        private nint _abilityEntries;
        private nint _abilityListPointer;
        private nint _readyingReplacement;
        private nint _saveBuffer;
        private string? _savedHoldsPath;
        private nint _timelineScroll;
        private nint _timelineCursor;
        private nint _timelineRows;

        // Set to 1 by the asm hook when the Combat Timeline is about to show a charged row's target.
        private nint _timelineShowingTarget;

        // What ShowActionTarget is handed instead of the unit's block when the held spell's row is shown.
        private nint _heldPreviewBlock;

        // The most recent turn order built, and the held-spell unit the next ShowActionTarget call is previewing.
        private readonly byte[] _lastTurnOrder = new byte[TURN_ENTRIES * TURN_ENTRY_SIZE];
        private readonly uint[] _lentSaved = new uint[UNIT_COUNT];
        private int _previewHeldUnit = -1;

        /// <summary>A second spell waiting for the unit's charge slot to free up.</summary>
        private struct PendingSpell
        {
            public bool Armed;
            public byte[] Action;   // copy of the unit's action block
            public int Ability;
            public int ChargeLeft;  // charge time still to run once it reaches the engine
        }

        /// <summary>A snapshot of a unit's charge slot.</summary>
        private struct Slot
        {
            public bool Charging;
            public byte[] Action;
            public int Ability;
            public int ChargeLeft;
        }

        private readonly PendingSpell[] _pending = new PendingSpell[UNIT_COUNT];

        // The readied first spell, captured when a second pick opens. Only meaningful while that pick is open.
        private readonly Slot[] _firstSpell = new Slot[UNIT_COUNT];

        // The ability of a held spell after it was handed to the engine, while it charges there; 0 when none.
        private readonly int[] _handedOver = new int[UNIT_COUNT];

        // Units that readied a qualifying spell and have been given their action back for a second pick.
        private readonly bool[] _awaitingSecond = new bool[UNIT_COUNT];

        // Units whose MarkTurnFlags(acted) call was held back until their commit shows whether it opens a second pick.
        private readonly bool[] _turnFlagsDeferred = new bool[UNIT_COUNT];

        // The unit's remembered Abilities cursor before a second pick shortened its rows, and whether it was moved.
        private readonly sbyte[] _originalCursor = new sbyte[UNIT_COUNT];
        private readonly bool[] _cursorShifted = new bool[UNIT_COUNT];

        private bool _injecting;

        private StreamWriter? _writer;
        private string _lastDiagnostic = string.Empty;
        private int _diagnosticRepeats;

        #endregion

        public Dualcast(IModLoader modLoader, IReloadedHooks hooks, ILogger logger, IModConfig modConfig,
            IStartupScanner startupScanner)
        {
            _modLoader = modLoader;
            _hooks = hooks;
            _logger = logger;
            _modConfig = modConfig;
            _baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;

            if (Verbose)
            {
                try
                {
                    var path = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "dualcast.log");
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    _writer.WriteLine($"=== Dualcast {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                catch (Exception ex)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Dualcast could not open its log file: {ex.Message}");
                }
            }

            Scan(startupScanner, "CommitAction", SIG_COMMIT_ACTION, address =>
            {
                _unitArray = ResolveRipRelative(address + COMMIT_UNIT_ARRAY_LEA, 3, 7);
                _commitHook = _hooks.CreateHook<CommitActionDelegate>(CommitActionImpl, address).Activate();
            });

            _savedHoldsPath = SavedHoldsPath();

            Scan(startupScanner, "SaveBuffer", SIG_SAVE_BUFFER_COPY, address =>
                _saveBuffer = ResolveRipRelative(address + SAVE_BUFFER_LEA, 3, 7));

            Scan(startupScanner, "BattleSave", SIG_BATTLE_SAVE, address =>
                _battleSaveHook = _hooks.CreateHook<BattleSaveDelegate>(BattleSaveImpl, address).Activate());

            Scan(startupScanner, "BattleLoad", SIG_BATTLE_LOAD, address =>
                _battleLoadHook = _hooks.CreateHook<BattleLoadDelegate>(BattleLoadImpl, address).Activate());

            Scan(startupScanner, "BattleLoad2", SIG_BATTLE_LOAD_2, address =>
                _battleLoad2Hook = _hooks.CreateHook<BattleLoadDelegate>(BattleLoad2Impl, address).Activate());

            Scan(startupScanner, "MarkTurnFlags", SIG_MARK_TURN_FLAGS, address =>
                _markTurnFlagsHook = _hooks.CreateHook<MarkTurnFlagsDelegate>(MarkTurnFlagsImpl, address).Activate());

            Scan(startupScanner, "ExecuteAction", SIG_EXECUTE_ACTION, address =>
                _executeActionHook = _hooks.CreateHook<ExecuteActionDelegate>(ExecuteActionImpl, address).Activate());

            Scan(startupScanner, "ModeFlag", SIG_PREDICT_ACTION, address =>
                _modeFlag = ResolveRipRelative(address + PREDICT_MODE_FLAG_MOV, 2, 6));

            Scan(startupScanner, "TurnOrder", SIG_TURN_ORDER, address =>
            {
                _returnState = ResolveRipRelative(address + TURN_ORDER_RETURN_STATE_CMP, 2, 7);
                _turnOrderHook = _hooks.CreateHook<TurnOrderDelegate>(TurnOrderImpl, address).Activate();
            });

            Scan(startupScanner, "TurnOrderSlot", SIG_TURN_ORDER_SLOT, address =>
                _turnOrderSlotHook = _hooks.CreateHook<TurnOrderSlotDelegate>(TurnOrderSlotImpl, address).Activate());

            Scan(startupScanner, "ShowActionTarget", SIG_SHOW_ACTION_TARGET, address =>
            {
                _heldPreviewBlock = Marshal.AllocHGlobal(ACTION_LENGTH);
                _showActionTargetHook =
                    _hooks.CreateHook<ShowActionTargetDelegate>(ShowActionTargetImpl, address).Activate();
            });

            Scan(startupScanner, "FocusActionTarget", SIG_FOCUS_ACTION_TARGET, address =>
                _focusActionTarget = _hooks.CreateWrapper<FocusActionTargetDelegate>(address, out _));

            Scan(startupScanner, "TimelineSelection", SIG_TIMELINE_SELECTION, address =>
            {
                _timelineScroll = ResolveRipRelative(address + TIMELINE_SCROLL_MOV, 2, 6);
                _timelineCursor = ResolveRipRelative(address + TIMELINE_CURSOR_MOV, 3, 7);
                _timelineRows = ResolveRipRelative(address + TIMELINE_ROWS_LEA, 3, 7);
            });

            Scan(startupScanner, "TimelineShowTarget", SIG_TIMELINE_SHOW_TARGET, address =>
            {
                _timelineShowingTarget = Marshal.AllocHGlobal(1);
                Marshal.WriteByte(_timelineShowingTarget, 0);

                // push rax / mov rax, flag / mov byte [rax], 1 / pop rax. rax is free here (it holds a discarded
                // return value) but is preserved anyway.
                var code = new List<byte> { 0x50, 0x48, 0xB8 };
                code.AddRange(BitConverter.GetBytes((long)_timelineShowingTarget));
                code.AddRange(new byte[] { 0xC6, 0x00, 0x01, 0x58 });
                _timelineShowTargetHook = _hooks.CreateAsmHook(code.ToArray(),
                    (long)(address + TIMELINE_SHOW_TARGET_HOOK), AsmHookBehaviour.ExecuteFirst).Activate();
            });

            Scan(startupScanner, "AbilityEntries", SIG_ABILITY_ENTRIES, address =>
                _abilityEntries = ResolveRipRelative(address + ABILITY_ENTRIES_LEA, 3, 7));

            Scan(startupScanner, "AbilityList", SIG_ABILITY_LIST_BUILD, address =>
                _abilityListPointer = ResolveRipRelative(address + ABILITY_LIST_PTR_MOV, 3, 7));

            Scan(startupScanner, "FillCommands", SIG_FILL_COMMANDS, address =>
                _fillCommandsHook = _hooks.CreateHook<FillCommandsDelegate>(FillCommandsImpl, address).Activate());

            Scan(startupScanner, "FillSkillset", SIG_FILL_SKILLSET, address =>
                _fillSkillsetHook = _hooks.CreateHook<FillSkillsetDelegate>(FillSkillsetImpl, address).Activate());

            Scan(startupScanner, "CommandSlot", SIG_COMMAND_SLOT, address =>
                _commandSlotHook = _hooks.CreateHook<CommandSlotDelegate>(CommandSlotImpl, address).Activate());

            Scan(startupScanner, "RowActivated", SIG_ROW_ACTIVATED, address =>
                _rowActivatedHook = _hooks.CreateHook<RowActivatedDelegate>(RowActivatedImpl, address).Activate());

            Scan(startupScanner, "GetActor", SIG_GET_ACTOR, address =>
                _getActor = _hooks.CreateWrapper<GetActorDelegate>(address, out _));

            Scan(startupScanner, "ApplyActorStatus", SIG_APPLY_ACTOR_STATUS, address =>
                _applyActorStatus = _hooks.CreateWrapper<ApplyActorStatusDelegate>(address, out _));

            Scan(startupScanner, "SetChargingStatus", SIG_SET_CHARGING, address =>
                _setCharging = _hooks.CreateWrapper<SetChargingDelegate>(address, out _));

            Scan(startupScanner, "GetAbilityActionData", SIG_GET_ABILITY_DATA, address =>
                _getAbilityActionData = _hooks.CreateWrapper<GetAbilityActionDataDelegate>(address, out _));

            Scan(startupScanner, "StringAssign", SIG_STRING_ASSIGN, address =>
            {
                _readyingReplacement = Marshal.StringToCoTaskMemUTF8(READYING_REPLACEMENT);
                foreach (var copy in FindAll(SIG_STRING_ASSIGN, address))
                {
                    // Each copy calls back with its own index so it chains to its own original.
                    int index = _stringAssignHooks.Count;
                    StringAssignDelegate impl = (target, text) => StringAssignImpl(index, target, text);
                    _stringAssignImpls.Add(impl);
                    _stringAssignHooks.Add(_hooks.CreateHook(impl, copy).Activate());
                }
            });
        }

        private void Scan(IStartupScanner scanner, string name, string signature, Action<nint> onFound)
        {
            scanner.AddMainModuleScan(signature, result =>
            {
                if (!result.Found)
                {
                    Warn($"{name}: signature not found, Dualcast will not work (did the game update?)");
                    return;
                }

                try
                {
                    onFound(_baseAddress + result.Offset);
                    Diagnostic($"{name} at +{result.Offset:X}");
                }
                catch (Exception ex)
                {
                    Warn($"{name}: {ex.Message}");
                }
            });
        }

        #region Queueing the second spell

        /// <summary>
        /// A unit committed an action. In real execution, a qualifying first spell opens a second pick and a
        /// second spell is paired with the first.
        /// </summary>
        private int CommitActionImpl(nint context)
        {
            // Sampled before the call: the game sets the mode around a simulated commit and restores it before
            // returning, so reading it afterwards reports the caller's mode, not this commit's.
            int mode = ReadMode();

            // What the unit was charging before this commit overwrites the single charge slot.
            int unitIndex = context == 0 ? -1 : UnitIndexFromContext(context);
            var previous = mode == 0 && _unitArray != 0 && unitIndex >= 0 && unitIndex < UNIT_COUNT
                ? CaptureSlot(unitIndex)
                : default;

            // Do not refuse a commit here by returning -1. The caller has already written the chosen action into the
            // unit's action block, and skipping the original leaves the unit half-updated: the first spell fires at
            // once and the chosen action never resolves.
            int result = _commitHook?.OriginalFunction(context) ?? 0;

            try
            {
                if (!_injecting && context != 0 && mode == 0 && _unitArray != 0)
                    OnCommitted(unitIndex, previous);
            }
            catch (Exception ex)
            {
                Warn($"CommitAction: {ex.Message}");
            }

            try
            {
                if (mode == 0 && !_injecting && unitIndex >= 0 && unitIndex < UNIT_COUNT &&
                    _turnFlagsDeferred[unitIndex])
                    ResolveDeferredTurnFlags(unitIndex);
            }
            catch (Exception ex)
            {
                Warn($"CommitAction: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// The action path records "acted" just before committing, and that call ends the turn of a unit that has
        /// already moved. For what is about to be a Dualcast unit's first spell, the call is held back until the
        /// commit, and dropped if the commit opens a second pick. The acted flag itself is not lost: the commit sets
        /// it, and the second pick clears it.
        /// </summary>
        private int MarkTurnFlagsImpl(int unitIndex, int moved, int acted)
        {
            try
            {
                if (acted != 0 && moved == 0 && !_injecting && ReadMode() == 0 && WillOpenSecondPick(unitIndex))
                {
                    _turnFlagsDeferred[unitIndex] = true;
                    Diagnostic($"Unit {unitIndex}: acted flag held back for the Dualcast commit");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Warn($"MarkTurnFlags: {ex.Message}");
            }

            return _markTurnFlagsHook?.OriginalFunction(unitIndex, moved, acted) ?? 0;
        }

        /// <summary>
        /// Whether the action a unit is about to commit is a spell that will open a Dualcast second pick. Read from
        /// the unit's sprite actor, which holds the action until CommitAction copies it into the unit. A wrong guess
        /// is harmless: the held-back call is simply made after the commit instead of before it.
        /// </summary>
        private unsafe bool WillOpenSecondPick(int unitIndex)
        {
            if (unitIndex < 0 || unitIndex >= UNIT_COUNT || _unitArray == 0 || _getActor == null ||
                _awaitingSecond[unitIndex])
                return false;

            byte* unit = Unit(unitIndex);
            if ((unit[DualcastByte] & DualcastBit) == 0 || (unit[OFF_SUPPORT_CHARGE] & SUPPORT_NON_CHARGE) != 0)
                return false;

            nint actor = _getActor(unit[OFF_UNIT_ID]);
            if (actor == 0)
                return false;

            byte* action = (byte*)(actor + ACTOR_ACTION);
            if (action[0] != unitIndex)
                return false;

            // Only skillset types 0 and 6 take the ability straight from the block, as CommitAction does.
            byte type = ((byte*)(_baseAddress + RVA_SKILLSET_TYPES))[action[ACT_COMMAND]];
            if (type != 0 && type != 6)
                return false;

            return Qualifies(*(ushort*)(action + ACT_ABILITY));
        }

        /// <summary>Makes, or drops, the MarkTurnFlags call held back for this unit's commit.</summary>
        private void ResolveDeferredTurnFlags(int unitIndex)
        {
            _turnFlagsDeferred[unitIndex] = false;
            if (_awaitingSecond[unitIndex])
                return;   // the second pick opened, so the turn goes on

            Diagnostic($"Unit {unitIndex}: no second pick after all, acted flag recorded late");
            _markTurnFlagsHook?.OriginalFunction(unitIndex, 0, 1);
        }

        /// <summary>
        /// Around each step of the battle state machine. Before it, any held spell whose slot is free is handed over;
        /// after it, a unit's turn starting ends every open second pick.
        /// </summary>
        /// <remarks>
        /// The hand-over has to come first. One call runs the machine until the next event: after a charged action
        /// has been carried out it goes back to firing charges for the same tick, then on through the CT increase
        /// and any turns that follow. Handed over only after the call, a held spell with nothing left to charge
        /// missed its tick, and whoever's turn came next went ahead of it.
        /// </remarks>
        private unsafe nint ExecuteActionImpl(nint arg1, nint arg2, nint arg3, nint arg4)
        {
            int mode = ReadMode();

            try
            {
                if (!_injecting && _unitArray != 0 && mode == 0)
                    InjectReadySpells();
            }
            catch (Exception ex)
            {
                Warn($"ExecuteAction: {ex.Message}");
            }

            nint result = _executeActionHook?.OriginalFunction(arg1, arg2, arg3, arg4) ?? 0;

            try
            {
                if (!_injecting && _unitArray != 0 && mode == 0 && TurnStarted((int)result))
                    EndSecondPicks();
            }
            catch (Exception ex)
            {
                Warn($"ExecuteAction: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Whether a state-machine result is a unit's turn starting. The step that starts a turn returns 0x100 | unit
        /// (0x300 | unit when turn-start status effects apply first) after giving the unit a fresh turn: active
        /// (+0x1B8), not moved (+0x1B9) and not acted (+0x1BA). 0x300 is also returned for other events, and 0x200
        /// for a charged action firing, so the flags are checked too.
        /// </summary>
        private unsafe bool TurnStarted(int result)
        {
            int kind = result & STEP_KIND_MASK;
            int unitIndex = result & 0xFF;
            if ((kind != STEP_TURN && kind != STEP_TURN_WITH_STATUS) || unitIndex >= UNIT_COUNT)
                return false;

            byte* unit = Unit(unitIndex);
            return unit[OFF_TURN_ACTIVE] != 0 && unit[OFF_MOVED] == 0 && unit[OFF_ACTED] == 0;
        }

        /// <summary>
        /// A new turn began, so any second pick still open was never taken - its unit chose Wait instead, or its turn
        /// ended some other way. The readied first spell keeps charging as normal.
        /// </summary>
        private void EndSecondPicks()
        {
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                _turnFlagsDeferred[i] = false;

                if (_awaitingSecond[i])
                {
                    _awaitingSecond[i] = false;
                    Diagnostic($"Unit {i}: turn ended without a second spell");
                }

                RestoreAbilitiesCursor(i);
            }
        }

        /// <summary>
        /// The first qualifying spell hands the unit its action back for a second pick; the second decides which of
        /// the two keeps the engine's charge slot.
        /// </summary>
        private unsafe void OnCommitted(int unitIndex, Slot previous)
        {
            if (unitIndex < 0 || unitIndex >= UNIT_COUNT)
                return;

            // A unit still charging gets turns, and acting in one cancels its charge: the game drops the spell in the
            // slot, and the held one has to go with it, or it is handed over as soon as the slot frees and fires
            // anyway. Only an action taken in the unit's own turn counts - a charged action is carried out outside
            // it - and not the second pick, which is the one action that pairs with the first spell.
            if (_pending[unitIndex].Armed && !_awaitingSecond[unitIndex] && IsTakingTurn(unitIndex))
            {
                Diagnostic($"Unit {unitIndex}: acted while charging, held spell {_pending[unitIndex].Ability:X3} dropped");
                _pending[unitIndex] = default;
            }

            byte* unit = Unit(unitIndex);
            if ((unit[DualcastByte] & DualcastBit) == 0)
                return;

            int ability = *(ushort*)(unit + OFF_ACTION + ACT_ABILITY);
            bool charging = unit[OFF_CHARGE] != NOT_CHARGING && (unit[OFF_STATUS] & STATUS_CHARGING) != 0;

            if (!_awaitingSecond[unitIndex])
            {
                if (!charging || !Qualifies(ability))
                    return;

                _awaitingSecond[unitIndex] = true;
                _firstSpell[unitIndex] = CaptureSlot(unitIndex);
                unit[OFF_ACTED] &= unchecked((byte)~ACTED);
                Diagnostic($"Unit {unitIndex} readied {ability:X3} (charge {unit[OFF_CHARGE]}), second pick opened");
                return;
            }

            // The second pick. The engine has already overwritten the slot with it, so the first spell is 'previous'.
            _awaitingSecond[unitIndex] = false;
            RestoreAbilitiesCursor(unitIndex);

            if (!previous.Charging)
                return;

            if (!charging || !Qualifies(ability))
            {
                // Only reachable if the menu filtering is bypassed somehow. The first spell stays cancelled, as the
                // game does when a charging unit picks anything else. Putting it back is not an option: the engine
                // carries out the action it just committed as soon as this returns.
                Diagnostic($"Unit {unitIndex}: second action {ability:X3} is not a spell, {previous.Ability:X3} is cancelled");
                return;
            }

            int secondCharge = unit[OFF_CHARGE];
            var second = CaptureSlot(unitIndex);

            // The engine can only charge one: the spell that resolves sooner keeps the slot, and the other is held
            // and handed over when the slot frees, with only the rest of its charge time to run, so each resolves at
            // its own speed. On a tie the first cast goes first, and the other follows in the same tick, before any
            // turn. This matches ReMixed.
            if (secondCharge < previous.ChargeLeft)
            {
                Hold(unitIndex, previous, previous.ChargeLeft - secondCharge);
                Diagnostic($"Unit {unitIndex} casts {ability:X3} in {secondCharge}, then {previous.Ability:X3} " +
                    $"{previous.ChargeLeft - secondCharge} later");
            }
            else
            {
                PutInSlot(unitIndex, previous, previous.ChargeLeft);
                Hold(unitIndex, second, secondCharge - previous.ChargeLeft);
                Diagnostic($"Unit {unitIndex} casts {previous.Ability:X3} in {previous.ChargeLeft}, then {ability:X3} " +
                    $"{secondCharge - previous.ChargeLeft} later");
            }
        }

        /// <summary>
        /// Holds a spell until its unit's charge slot frees. The charge left counts from then: 0 fires it in the
        /// same pass as the spell before it.
        /// </summary>
        private void Hold(int unitIndex, Slot slot, int chargeAfter)
        {
            _pending[unitIndex] = new PendingSpell
            {
                Armed = true,
                Action = slot.Action,
                Ability = slot.Ability,
                ChargeLeft = Math.Max(0, chargeAfter),
            };
        }

        /// <summary>
        /// Hands any held second spell to the engine once its unit is no longer charging. A unit that is gone - often
        /// killed by its own first spell - loses the held spell instead. Handing it over anyway would leave it frozen
        /// on the timeline, since the engine does not tick a dead unit's charge, and cast it on revival.
        /// </summary>
        private unsafe void InjectReadySpells()
        {
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                byte* unit = Unit(i);
                DropHandedOverIfGone(i, unit);

                if (!_pending[i].Armed)
                    continue;

                // Also catches a spell left over from a battle that ended mid-cast, whose slot now holds another unit.
                if (!CanStillCast(unit) || (unit[DualcastByte] & DualcastBit) == 0)
                {
                    Diagnostic($"Unit {i}: held spell {_pending[i].Ability:X3} dropped, the caster is gone");
                    _pending[i] = default;
                    continue;
                }

                if (unit[OFF_CHARGE] != NOT_CHARGING || (unit[OFF_STATUS] & STATUS_CHARGING) != 0)
                    continue;   // still charging the first spell

                var spell = _pending[i];
                _pending[i] = default;

                PutInSlot(i, new Slot { Action = spell.Action, Ability = spell.Ability }, spell.ChargeLeft);
                _handedOver[i] = spell.Ability;
                Diagnostic($"Unit {i}: second spell {spell.Ability:X3} handed to the engine, charge {spell.ChargeLeft}");
            }
        }

        /// <summary>
        /// The held spell may reach the engine a moment before the first spell's effect lands. If that effect (or
        /// anything else) then takes the caster out, the second spell goes with it, as it would have if still held.
        /// </summary>
        private unsafe void DropHandedOverIfGone(int unitIndex, byte* unit)
        {
            int ability = _handedOver[unitIndex];
            if (ability == 0)
                return;

            if (unit[OFF_CHARGE] == NOT_CHARGING || *(ushort*)(unit + OFF_ACTION + ACT_ABILITY) != ability)
            {
                _handedOver[unitIndex] = 0;   // resolved, or replaced by something else
                return;
            }

            if (CanStillCast(unit))
                return;

            _handedOver[unitIndex] = 0;

            // Cleared the engine's own way, but with the mode flag raised so it does not post a sprite change for a
            // unit lying on the ground.
            int mode = ReadMode();
            if (_modeFlag != 0)
                *(int*)_modeFlag = 1;
            try
            {
                _setCharging?.Invoke((nint)unit, CHARGE_KIND_CLEAR, 0, 0);
            }
            finally
            {
                if (_modeFlag != 0)
                    *(int*)_modeFlag = mode;
            }

            unit[OFF_CHARGE] = NOT_CHARGING;
            Diagnostic($"Unit {unitIndex}: second spell {ability:X3} dropped, the caster is gone");
        }

        /// <summary>Whether a unit is still on the field in a state where a spell can go on charging.</summary>
        private static unsafe bool CanStillCast(byte* unit)
            => unit[1] != 0xFF && (unit[OFF_STATUS] & STATUS_GONE) == 0 && (unit[OFF_STATUS2] & STATUS2_GONE) == 0;

        private unsafe Slot CaptureSlot(int unitIndex)
        {
            byte* unit = Unit(unitIndex);
            if (unit[OFF_CHARGE] == NOT_CHARGING || (unit[OFF_STATUS] & STATUS_CHARGING) == 0)
                return default;

            var copy = new byte[ACTION_LENGTH];
            new ReadOnlySpan<byte>(unit + OFF_ACTION, ACTION_LENGTH).CopyTo(copy);
            return new Slot
            {
                Charging = true,
                Action = copy,
                Ability = *(ushort*)(unit + OFF_ACTION + ACT_ABILITY),
                ChargeLeft = unit[OFF_CHARGE],
            };
        }

        /// <summary>Writes an action back into the unit's charge slot with a given amount of charge left to run.</summary>
        private unsafe void PutInSlot(int unitIndex, Slot slot, int chargeLeft)
        {
            byte* unit = Unit(unitIndex);
            _injecting = true;
            try
            {
                slot.Action.AsSpan().CopyTo(new Span<byte>(unit + OFF_ACTION, ACTION_LENGTH));

                // SetChargingStatus starts from the ability's full charge time, so the remaining time goes on after.
                // 0 is valid: the machine's fire pass takes any charging unit at 0, and is where a held spell is
                // handed over.
                _setCharging?.Invoke((nint)unit, CHARGE_KIND_SET, 0, 0);
                unit[OFF_CHARGE] = (byte)Math.Clamp(chargeLeft, 0, 0xFE);
            }
            finally
            {
                _injecting = false;
            }
        }

        /// <summary>
        /// ReMixed's rule for a spell that can be readied alongside another: it has a charge time and an MP cost, and
        /// is not flagged as one that skips charging.
        /// </summary>
        private unsafe bool Qualifies(int ability)
        {
            if (ability <= 0 || ability > ABILITY_MAX_WITH_DATA || _getAbilityActionData == null)
                return false;

            byte* data = (byte*)_getAbilityActionData(ability);
            if (data == null)
                return false;

            return (data[ABILITY_CT] & 0x7F) > 0 && data[ABILITY_MP] > 0 &&
                   (data[ABILITY_FLAGS3] & ABILITY_FLAG_NO_CHARGE) == 0;
        }

        #endregion

        #region Timeline

        /// <summary>
        /// Adds the held spell to the turn order, so the timeline shows both spells. It has to be added here: a unit's
        /// own state can only ever describe one pending action.
        /// </summary>
        private unsafe nint TurnOrderImpl(nint entries, nint arg2, nint arg3)
        {
            // Read before the build: a preview commits the candidate spell into the unit just before building.
            int previewer = -1, firstOffset = 0;
            try
            {
                previewer = PreviewingSecondSpell(out firstOffset);
            }
            catch (Exception ex)
            {
                Warn($"TurnOrder: {ex.Message}");
            }

            int lent = 0;
            nint result;
            try
            {
                lent = LendFreeSlots();
                result = _turnOrderHook?.OriginalFunction(entries, arg2, arg3) ?? 0;
            }
            finally
            {
                ReturnLentSlots(lent);
            }

            try
            {
                if (entries != 0)
                {
                    for (int i = 0; i < UNIT_COUNT; i++)
                    {
                        if (_pending[i].Armed && (lent & (1 << i)) == 0)
                            AddCharged((byte*)entries, i, _pending[i].Ability, _pending[i].ChargeLeft);
                    }

                    if (previewer >= 0)
                        AddCharged((byte*)entries, previewer, _firstSpell[previewer].Ability, firstOffset,
                            aheadOnTie: true);   // on a tie the first cast resolves first

                    new ReadOnlySpan<byte>((void*)entries, _lastTurnOrder.Length).CopyTo(_lastTurnOrder);
                }
            }
            catch (Exception ex)
            {
                Warn($"TurnOrder: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// The unit, if any, whose second pick is being previewed right now: the game has committed a candidate spell
        /// into its slot to build the timeline, which pushes out the readied first spell. Also gives how many ticks
        /// after the candidate the first spell resolves (negative when it resolves sooner).
        /// </summary>
        private unsafe int PreviewingSecondSpell(out int firstOffset)
        {
            firstOffset = 0;
            if (_unitArray == 0)
                return -1;

            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if (!_awaitingSecond[i] || !_firstSpell[i].Charging)
                    continue;

                byte* unit = Unit(i);
                if (unit[OFF_CHARGE] == NOT_CHARGING || (unit[OFF_STATUS] & STATUS_CHARGING) == 0 ||
                    !Qualifies(*(ushort*)(unit + OFF_ACTION + ACT_ABILITY)))
                    continue;

                // Still the first spell: nothing is being previewed, and the engine lists it itself.
                if (new ReadOnlySpan<byte>(unit + OFF_ACTION, ACTION_LENGTH).SequenceEqual(_firstSpell[i].Action))
                    continue;

                firstOffset = _firstSpell[i].ChargeLeft - unit[OFF_CHARGE];
                return i;
            }

            return -1;
        }

        /// <summary>
        /// Adds a charged action the unit's own state cannot describe, a given number of ticks after the unit's
        /// listed charged action (before it, when negative).
        /// </summary>
        private unsafe bool AddCharged(byte* list, int holder, int ability, int ticksAfter, bool aheadOnTie = false)
        {
            if (!CanStillCast(Unit(holder)))
                return false;

            // The unit's listed charged action is the anchor. If the engine did not list it, this turn order is not
            // one the player is looking at.
            int firstTime = -1;
            for (int i = 0; i < TURN_ENTRIES; i++)
            {
                byte* entry = list + i * TURN_ENTRY_SIZE;
                if (*(uint*)entry == TURN_ENTRY_EMPTY)
                    break;

                if ((entry[0] & TURN_UNIT_MASK) == holder && (entry[0] & TURN_FLAG_CHARGED) != 0)
                {
                    firstTime = *(ushort*)(entry + 2);
                    break;
                }
            }

            if (firstTime < 0)
                return false;

            // A held spell starts charging when the slot frees, so it resolves its remaining time after the anchor.
            int time = firstTime + ticksAfter * TURN_TICK - (aheadOnTie && ticksAfter == 0 ? 1 : 0);
            InsertCharged(list, holder, ability, time);
            return true;
        }

        /// <summary>
        /// While a first spell is being carried out its unit has nothing charging, so there is no row to place the
        /// held spell after. For the length of one build, such a unit's slot is shown to the engine as holding the
        /// held spell with its charge left, so the engine lists it exactly where it will once handed over. Only the
        /// charge and the command/ability the builder reads are touched, and all are put back straight after.
        /// </summary>
        /// <returns>A bit per unit whose slot was lent.</returns>
        private unsafe int LendFreeSlots()
        {
            if (_unitArray == 0)
                return 0;

            int lent = 0;
            int chargePassOffset = ChargePassOffset();
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if (!_pending[i].Armed || _pending[i].Action == null)
                    continue;

                byte* unit = Unit(i);
                if (unit[OFF_CHARGE] != NOT_CHARGING || !CanStillCast(unit))
                    continue;

                _lentSaved[i] = *(uint*)(unit + OFF_ACTION);   // bytes 0-3: command at +1, ability at +2
                fixed (byte* action = _pending[i].Action)
                {
                    unit[OFF_ACTION + ACT_COMMAND] = action[ACT_COMMAND];
                    *(ushort*)(unit + OFF_ACTION + ACT_ABILITY) = *(ushort*)(action + ACT_ABILITY);
                }

                unit[OFF_CHARGE] = (byte)Math.Clamp(_pending[i].ChargeLeft + chargePassOffset, 0, 0xFE);
                lent |= 1 << i;
            }

            return lent;
        }

        /// <summary>
        /// Charge ticks to add to a lent slot so the builder places it right. The builder counts a charge of n as
        /// resolving before the turns of the n-th CT fill, which holds when it is built during a turn: the countdown
        /// comes first there. While a charged action is being carried out, this tick's countdown has already run and
        /// the next CT fill comes first, so every charged row reads one tick early. The builder corrects for that
        /// itself only for a turn taken in the middle of the fire pass (return state 9), not for the pass itself.
        /// </summary>
        private unsafe int ChargePassOffset()
        {
            if (_returnState == 0)
                return 0;

            int state = *(int*)(_returnState + CLOCK_STATE_AFTER_RETURN);
            int returnState = *(int*)_returnState;
            return (state == CLOCK_FIRE || state == CLOCK_AFTER_ACTION) && returnState != CLOCK_AFTER_ACTION ? 1 : 0;
        }

        private unsafe void ReturnLentSlots(int lent)
        {
            for (int i = 0; lent != 0 && i < UNIT_COUNT; i++)
            {
                if ((lent & (1 << i)) == 0)
                    continue;

                byte* unit = Unit(i);
                *(uint*)(unit + OFF_ACTION) = _lentSaved[i];
                unit[OFF_CHARGE] = NOT_CHARGING;
            }
        }

        /// <summary>Inserts a charged action in time order, pushing the rest down and dropping whatever falls off the end.</summary>
        private unsafe void InsertCharged(byte* list, int holder, int ability, int time)
        {
            if (time < 0 || time > ushort.MaxValue)
                return;

            for (int i = 0; i < TURN_ENTRIES; i++)
            {
                byte* entry = list + i * TURN_ENTRY_SIZE;
                bool empty = *(uint*)entry == TURN_ENTRY_EMPTY;
                if (!empty && *(ushort*)(entry + 2) <= time)
                    continue;

                if (!empty)
                {
                    for (int j = TURN_ENTRIES - 1; j > i; j--)
                        *(uint*)(list + j * TURN_ENTRY_SIZE) = *(uint*)(list + (j - 1) * TURN_ENTRY_SIZE);
                }

                // The name shown comes from the ability id the entry carries, so it must be the spell's own.
                entry[0] = (byte)(holder | TURN_FLAG_CHARGED | (ability > 0xFF ? TURN_FLAG_ABILITY_HIGH : 0));
                entry[1] = (byte)(ability & 0xFF);
                *(ushort*)(entry + 2) = (ushort)time;
                return;
            }
        }

        /// <summary>
        /// The unit whose held spell a turn-order row stands for, or -1. The held spell is always inserted after its
        /// first spell, so it is a holder's second charged row, or its only one once the first has fired.
        /// </summary>
        private unsafe int HeldSpellAt(byte* list, int index)
        {
            if (index < 0 || index >= TURN_ENTRIES)
                return -1;

            byte* entry = list + index * TURN_ENTRY_SIZE;
            if (*(uint*)entry == TURN_ENTRY_EMPTY || (entry[0] & TURN_KIND_MASK) != TURN_FLAG_CHARGED)
                return -1;

            int unit = entry[0] & TURN_UNIT_MASK;
            if (unit >= UNIT_COUNT || !_pending[unit].Armed)
                return -1;

            // While the first spell is being carried out, the held one is the unit's only charged row.
            if (Unit(unit)[OFF_CHARGE] == NOT_CHARGING)
                return unit;

            for (int i = 0; i < index; i++)
            {
                byte* earlier = list + i * TURN_ENTRY_SIZE;
                if ((earlier[0] & TURN_UNIT_MASK) == unit && (earlier[0] & TURN_KIND_MASK) == TURN_FLAG_CHARGED)
                    return unit;
            }

            return -1;
        }

        /// <summary>
        /// Two timeline views pick a row with this, then show its target straight away. Notes whether the row is a
        /// held spell, for that ShowActionTarget call. The slot indexes the turn order just built inside this call.
        /// </summary>
        private unsafe int TurnOrderSlotImpl(int slot)
        {
            int result = _turnOrderSlotHook?.OriginalFunction(slot) ?? -1;

            try
            {
                fixed (byte* list = _lastTurnOrder)
                    _previewHeldUnit = HeldSpellAt(list, slot);
            }
            catch (Exception ex)
            {
                _previewHeldUnit = -1;
                Warn($"TurnOrderSlot: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// When a timeline view shows the target of a held spell's row, hands it the held spell's action block instead
        /// of the unit's own, which belongs to the first spell. Anything else - including the target shown when a
        /// spell resolves - passes through untouched, because only a view that just picked a held row arms this.
        /// </summary>
        private unsafe int ShowActionTargetImpl(nint block)
        {
            try
            {
                int holder = _previewHeldUnit;
                _previewHeldUnit = -1;

                byte* shownByTimeline = (byte*)_timelineShowingTarget;
                if (shownByTimeline != null && *shownByTimeline != 0)
                {
                    *shownByTimeline = 0;
                    if (_timelineScroll != 0 && _timelineCursor != 0 && _timelineRows != 0)
                        holder = HeldSpellAt((byte*)_timelineRows, *(int*)_timelineScroll + *(int*)_timelineCursor);
                }

                if (holder >= 0 && _unitArray != 0 && _heldPreviewBlock != 0 && _pending[holder].Armed &&
                    block == (nint)Unit(holder) + OFF_ACTION)
                {
                    Marshal.Copy(_pending[holder].Action, 0, _heldPreviewBlock, ACTION_LENGTH);
                    block = _heldPreviewBlock;
                    FocusOnHeldTarget(holder);
                }
            }
            catch (Exception ex)
            {
                Warn($"ShowActionTarget: {ex.Message}");
            }

            return _showActionTargetHook?.OriginalFunction(block) ?? -1;
        }

        /// <summary>
        /// The view has just pointed the cursor and camera at the unit's own action, i.e. the first spell. Points
        /// them at the held spell's target instead, by running the same routine with the held spell briefly in the
        /// unit's block. Nothing else runs in between, and the block is put straight back.
        /// </summary>
        private unsafe void FocusOnHeldTarget(int holder)
        {
            if (_focusActionTarget == null || _getActor == null)
                return;

            byte* unit = Unit(holder);
            nint actor = _getActor(unit[OFF_UNIT_ID]);
            if (actor == 0)
                return;

            var block = new Span<byte>(unit + OFF_ACTION, ACTION_LENGTH);
            var own = block.ToArray();
            _pending[holder].Action.AsSpan().CopyTo(block);
            try
            {
                _focusActionTarget(actor);
            }
            finally
            {
                own.AsSpan().CopyTo(block);
            }
        }

        #endregion

        #region The second-pick menu

        /// <summary>
        /// While a unit chooses a second spell, leaves out every Abilities row with nothing in it that could be one
        /// (Attack always) and appends the Cancel Casting row. The per-row blocked flags and types belong to the
        /// positions the engine filled, so each kept row takes its flag along and has its type recomputed.
        /// </summary>
        private unsafe int FillCommandsImpl(int unitIndex, nint list)
        {
            int result = _fillCommandsHook?.OriginalFunction(unitIndex, list) ?? 0;

            try
            {
                if (list == 0 || unitIndex < 0 || unitIndex >= UNIT_COUNT || !_awaitingSecond[unitIndex])
                    return result;

                byte* rows = (byte*)list;
                int end = 0;
                while (end < ROW_LIST_MAX && rows[end] != 0xFF)
                {
                    if (rows[end] == COMMAND_CANCEL_CASTING)
                        return result;   // already done
                    end++;
                }

                byte* blocked = (byte*)(_baseAddress + RVA_ROW_BLOCKED);
                byte* types = (byte*)(_baseAddress + RVA_ROW_TYPES);
                byte* skillsetTypes = (byte*)(_baseAddress + RVA_SKILLSET_TYPES);

                int kept = 0;
                var removedBelow = new int[ROW_LIST_MAX];
                var removed = new bool[ROW_LIST_MAX];
                for (int i = 0; i < end; i++)
                {
                    removedBelow[i] = i - kept;
                    if (!CommandHasSecondSpell(unitIndex, rows[i]))
                    {
                        removed[i] = true;
                        continue;
                    }

                    byte flag = blocked[i];
                    rows[kept] = rows[i];
                    blocked[kept] = flag;
                    types[kept] = (byte)(skillsetTypes[rows[kept]] & 0x0F);
                    kept++;
                }

                // Cancel Casting and the terminator need two more slots. Nothing was removed if this is full, so the
                // list is still exactly as the engine left it.
                if (kept >= ROW_LIST_MAX)
                    return result;

                ShiftAbilitiesCursor(unitIndex, end, kept, removedBelow, removed);

                rows[kept] = COMMAND_CANCEL_CASTING;
                rows[kept + 1] = 0xFF;
                types[kept] = (byte)(skillsetTypes[COMMAND_CANCEL_CASTING] & 0x0F);
                blocked[kept] = 0;
                if (kept + 1 < ROW_LIST_MAX)
                    blocked[kept + 1] = 0;

                Diagnostic($"unit {unitIndex}: second pick shows {kept} of {end} commands plus Cancel Casting");
            }
            catch (Exception ex)
            {
                Warn($"FillCommands: {ex.Message}");
            }

            return result;
        }

        /// <summary>Whether any of the unit's abilities under this command could be a second spell.</summary>
        private unsafe bool CommandHasSecondSpell(int unitIndex, byte command)
        {
            if (_abilityEntries == 0)
                return true;   // unknown, so leave the row alone

            byte* entries = (byte*)_abilityEntries;
            int block = entries[ENTRY_BLOCK_IDS + unitIndex];
            byte* entry = entries + ENTRY_BLOCKS + block * ENTRIES_PER_BLOCK * ENTRY_SIZE;
            for (int i = 0; i < ENTRIES_PER_BLOCK; i++, entry += ENTRY_SIZE)
            {
                if (entry[ENTRY_COMMAND] == 0xFF)
                    break;
                if (entry[ENTRY_COMMAND] == command && Qualifies(*(ushort*)entry & ENTRY_ABILITY_MASK))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Moves the unit's remembered Abilities cursor to follow the rows that were left out, so it opens on the
        /// same command. Done once per second pick: after the first open the screen saves the cursor back in the
        /// shortened list's numbering, and shifting that again would move it twice.
        /// </summary>
        private unsafe void ShiftAbilitiesCursor(int unitIndex, int before, int after, int[] removedBelow, bool[] removed)
        {
            sbyte* cursor = AbilitiesCursor(unitIndex);
            int rows = after + 1;   // the rows kept, plus Cancel Casting

            if (!_cursorShifted[unitIndex])
            {
                int original = *cursor;
                int shifted = original >= 0 && original < before && !removed[original]
                    ? original - removedBelow[original]
                    : 0;
                _originalCursor[unitIndex] = (sbyte)original;
                _cursorShifted[unitIndex] = true;
                *cursor = (sbyte)Math.Clamp(shifted, 0, rows - 1);
                return;
            }

            if (*cursor < 0 || *cursor >= rows)
                *cursor = 0;
        }

        /// <summary>Puts the unit's remembered Abilities cursor back once its second pick is over.</summary>
        private unsafe void RestoreAbilitiesCursor(int unitIndex)
        {
            if (unitIndex < 0 || unitIndex >= UNIT_COUNT || !_cursorShifted[unitIndex])
                return;

            _cursorShifted[unitIndex] = false;
            *AbilitiesCursor(unitIndex) = _originalCursor[unitIndex];
        }

        private unsafe sbyte* AbilitiesCursor(int unitIndex)
            => (sbyte*)(_baseAddress + RVA_ABILITIES_CURSOR + unitIndex * ABILITIES_CURSOR_STRIDE);

        /// <summary>
        /// While the caster chooses a second spell, keeps only the abilities that could be one, so that is all a
        /// command's list shows. Compacting here, before the list builder counts and sizes the list, keeps
        /// everything downstream consistent.
        /// </summary>
        private unsafe int FillSkillsetImpl(int unitIndex, int command, nint ids, nint mp, nint ct, nint arg6,
            nint flags, nint turn, nint arg9)
        {
            int count = _fillSkillsetHook?.OriginalFunction(unitIndex, command, ids, mp, ct, arg6, flags, turn, arg9) ?? 0;

            try
            {
                if (ids == 0 || count <= 0 || count > ABILITY_LIST_MAX ||
                    unitIndex < 0 || unitIndex >= UNIT_COUNT || AwaitingSecondUnit() != unitIndex)
                    return count;

                ushort* id = (ushort*)ids;
                byte* mpArr = (byte*)mp, ctArr = (byte*)ct, flArr = (byte*)flags, tuArr = (byte*)turn;
                int kept = 0;
                for (int i = 0; i < count && id[i] != 0xFFFF; i++)
                {
                    if (!Qualifies(id[i] & 0x1FF))
                        continue;

                    id[kept] = id[i];
                    if (mpArr != null) mpArr[kept] = mpArr[i];
                    if (ctArr != null) ctArr[kept] = ctArr[i];
                    if (flArr != null) flArr[kept] = flArr[i];
                    if (tuArr != null) tuArr[kept] = tuArr[i];
                    kept++;
                }

                id[kept] = 0xFFFF;
                Diagnostic($"unit {unitIndex}: command {command & 0xFF:X2} lists {kept} of {count} for the second pick");
                return kept;
            }
            catch (Exception ex)
            {
                Warn($"FillSkillset: {ex.Message}");
                return count;
            }
        }

        /// <summary>
        /// Gives the Cancel Casting row the job-command tooltip. Without this the tooltip routers have no case for
        /// it and leave whatever tooltip was last shown on screen.
        /// </summary>
        private byte CommandSlotImpl(int unitIndex, int command)
        {
            if (command == COMMAND_CANCEL_CASTING && unitIndex >= 0 && unitIndex < UNIT_COUNT &&
                _awaitingSecond[unitIndex])
                return SLOT_PRIMARY;

            return _commandSlotHook?.OriginalFunction(unitIndex, command) ?? 0;
        }

        /// <summary>
        /// A menu item was chosen. Cancel Casting cancels the readied spell and backs out to the turn menu; an
        /// ability that could not be a second spell is ignored, in case one ever reaches a list.
        /// </summary>
        private unsafe nint RowActivatedImpl(nint ui, int index)
        {
            int unitIndex = -1;
            bool cancel = false;

            try
            {
                unitIndex = AwaitingSecondUnit();
                if (unitIndex >= 0 && ui != 0 && IsTakingTurn(unitIndex))
                {
                    if (RefusedSecondPick(ui))
                        return 0;

                    // The row-id array is shared by every menu, so the item must also be in the Abilities menu -
                    // otherwise Status (also index 3 in the turn menu) would read a stale Cancel Casting from it.
                    cancel = ui == _baseAddress + RVA_ABILITIES_MENU && index >= 0 && index < ROW_WIDGET_COUNT &&
                             ((uint*)(_baseAddress + RVA_ROW_WIDGET_IDS))[index] ==
                             ROW_WIDGET_BASE + COMMAND_CANCEL_CASTING;
                }
            }
            catch (Exception ex)
            {
                Warn($"RowActivated: {ex.Message}");
            }

            if (!cancel)
                return _rowActivatedHook?.OriginalFunction(ui, index) ?? 0;

            CancelCasting(unitIndex);

            // Back out the way a back item does: give the item the "go back one level" handler for this activation.
            // Otherwise the game tries to open the (empty) command and shows "Unable to use".
            nint handlers = *(nint*)(ui + UI_HANDLERS);
            if (unchecked((uint)(int)handlers + 0x0Fu) <= 0x0Eu)
                return _rowActivatedHook?.OriginalFunction(ui, index) ?? 0;   // already a shared back handler

            short* handler = (short*)handlers + index;
            short saved = *handler;
            if (!WriteProtected((nint)handler, BitConverter.GetBytes(HANDLER_BACK_ONE)))
            {
                Warn("Cancel Casting: could not back out of the menu");
                return 0;
            }

            try
            {
                return _rowActivatedHook?.OriginalFunction(ui, index) ?? 0;
            }
            finally
            {
                WriteProtected((nint)handler, BitConverter.GetBytes(saved));
            }
        }

        /// <summary>
        /// Whether the ability under the list cursor must be refused. The activation index is always 0 for a
        /// command's list (the list is one item as far as the menu is concerned); the chosen ability is the one
        /// under the cursor, which is what the list screen reads afterwards.
        /// </summary>
        private unsafe bool RefusedSecondPick(nint ui)
        {
            if (ui != _baseAddress + RVA_ABILITY_LIST_MENU || _abilityListPointer == 0)
                return false;

            ushort* items = *(ushort**)_abilityListPointer;
            nint state = *(nint*)(_baseAddress + RVA_MENU_STATE_PTR);
            if (items == null || state == 0)
                return false;

            int cursor = *(short*)(state + MENU_LIST_CURSOR);
            if (cursor < 0 || cursor >= ABILITY_LIST_MAX)
                return false;

            int id = items[cursor];
            if (id == 0xFFFF || (id & ITEM_WIDGET_MASK) != ITEM_WIDGET_ABILITY)
                return false;

            return !Qualifies(id & ITEM_ABILITY_MASK);
        }

        /// <summary>
        /// Drops the spell the unit is readying and gives it back a normal, unspent action. The charge is cleared the
        /// way the engine clears a finished one, and the sprite leaves its chanting pose at once.
        /// </summary>
        private unsafe void CancelCasting(int unitIndex)
        {
            byte* unit = Unit(unitIndex);
            int ability = *(ushort*)(unit + OFF_ACTION + ACT_ABILITY);

            _awaitingSecond[unitIndex] = false;
            _pending[unitIndex] = default;
            RestoreAbilitiesCursor(unitIndex);

            // SetChargingStatus only posts the sprite change in real mode and without a busy UI; menus run with both
            // the other way, so both are lifted for the call. The posted change is then applied at once, rather than
            // at the unit's next action, which is the only other point the game applies it.
            int mode = ReadMode();
            int* uiBusy = (int*)(_baseAddress + RVA_UI_BUSY);
            int busy = *uiBusy;
            if (_modeFlag != 0)
                *(int*)_modeFlag = 0;
            *uiBusy = 0;

            try
            {
                _setCharging?.Invoke((nint)unit, CHARGE_KIND_CLEAR, 0, 0);

                nint actor = _getActor?.Invoke(unit[OFF_UNIT_ID]) ?? 0;
                if (actor != 0)
                    _applyActorStatus?.Invoke(actor);
            }
            finally
            {
                *uiBusy = busy;
                if (_modeFlag != 0)
                    *(int*)_modeFlag = mode;
            }

            unit[OFF_CHARGE] = NOT_CHARGING;
            unit[OFF_ACTED] &= unchecked((byte)~ACTED);
            Diagnostic($"Unit {unitIndex}: Cancel Casting, {ability:X3} dropped");
        }

        /// <summary>
        /// Rewords the "using another ability will cancel the current one" prompt while a unit chooses its second
        /// spell. Everything else, including that prompt for ordinary units, is left alone.
        /// </summary>
        private nint StringAssignImpl(int index, nint target, nint text)
        {
            try
            {
                if (text != 0 && _readyingReplacement != 0 && IsTakingTurn(AwaitingSecondUnit()) &&
                    StartsWith(text, READYING_TEMPLATE))
                    text = _readyingReplacement;
            }
            catch (Exception ex)
            {
                Warn($"StringAssign: {ex.Message}");
            }

            return _stringAssignHooks[index].OriginalFunction(target, text);
        }

        /// <summary>
        /// Whether it is still this unit's turn (+0x1B8, set at turn start and cleared when the turn ends), so the
        /// second-pick rules never reach another unit's menu. (The game's "menu unit" pointer 0x141872EA0 is NOT
        /// usable for this: the menu's previews point it at other units while the caster is still choosing.)
        /// </summary>
        private unsafe bool IsTakingTurn(int unitIndex)
            => unitIndex >= 0 && unitIndex < UNIT_COUNT && _unitArray != 0 && Unit(unitIndex)[OFF_TURN_ACTIVE] != 0;

        /// <summary>
        /// The unit currently choosing a second spell, or -1. A second pick only exists while its first spell is
        /// charging, so one whose unit is gone or no longer charging (a battle left mid-pick) is cleared - but only in
        /// real execution, because mid-simulation a charging unit's charge can briefly read as finished.
        /// </summary>
        private unsafe int AwaitingSecondUnit()
        {
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if (!_awaitingSecond[i])
                    continue;

                byte* unit = _unitArray != 0 ? Unit(i) : null;
                if (ReadMode() == 0 && (unit == null || unit[1] == 0xFF || unit[OFF_CHARGE] == NOT_CHARGING))
                {
                    _awaitingSecond[i] = false;
                    RestoreAbilitiesCursor(i);
                    Diagnostic($"Unit {i}: stale second pick cleared");
                    continue;
                }

                return i;
            }

            return -1;
        }

        #endregion

        #region Saving and loading

        // A battle save captures the unit array but not this mod's held spells, which would otherwise be lost on
        // load. Each save that has held spells (or an open second pick) gets a line in our file:
        //   <fingerprint> <unit>:<charge left>:<ability>:<action block hex>;... [A<unit>...]
        // keyed by a hash of the saved unit array, which is exactly what comes back when that save is loaded.

        private nint BattleSaveImpl(nint arg1)
        {
            nint result = _battleSaveHook?.OriginalFunction(arg1) ?? 0;

            try
            {
                RememberHeldSpells();
            }
            catch (Exception ex)
            {
                Warn($"BattleSave: {ex.Message}");
            }

            return result;
        }

        private nint BattleLoadImpl(nint arg1)
        {
            nint result = _battleLoadHook?.OriginalFunction(arg1) ?? 0;
            RestoreHeldSpellsSafely();
            return result;
        }

        private nint BattleLoad2Impl(nint arg1)
        {
            nint result = _battleLoad2Hook?.OriginalFunction(arg1) ?? 0;
            RestoreHeldSpellsSafely();
            return result;
        }

        private void RestoreHeldSpellsSafely()
        {
            try
            {
                RestoreHeldSpells();
            }
            catch (Exception ex)
            {
                Warn($"BattleLoad: {ex.Message}");
            }
        }

        /// <summary>Records the held spells (and any open second pick) against the save just made.</summary>
        private void RememberHeldSpells()
        {
            string? key = SaveFingerprint();
            if (key == null || _savedHoldsPath == null)
                return;

            var held = new List<string>();
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if (_pending[i].Armed)
                    held.Add($"{i}:{_pending[i].ChargeLeft}:{_pending[i].Ability}:{Convert.ToHexString(_pending[i].Action)}");
            }

            var picks = new List<string>();
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                if (_awaitingSecond[i])
                    picks.Add($"A{i}");
            }

            // The same moment saved again replaces its line; a save with nothing held needs no line at all.
            var lines = ReadSavedHolds().Where(line => !line.StartsWith(key + " ", StringComparison.Ordinal)).ToList();
            if (held.Count > 0 || picks.Count > 0)
                lines.Add($"{key} {string.Join(";", held)} {string.Join(",", picks)}".TrimEnd());

            if (lines.Count > SAVED_HOLDS_KEPT)
                lines.RemoveRange(0, lines.Count - SAVED_HOLDS_KEPT);

            File.WriteAllLines(_savedHoldsPath, lines);
            Diagnostic($"Battle saved ({key[..8]}): {held.Count} held spell(s), {picks.Count} open second pick(s)");
        }

        /// <summary>
        /// A battle was loaded: everything this mod held belongs to the battle being left, so it is all dropped, then
        /// whatever was recorded against the loaded save is put back.
        /// </summary>
        private unsafe void RestoreHeldSpells()
        {
            for (int i = 0; i < UNIT_COUNT; i++)
            {
                _pending[i] = default;
                _handedOver[i] = 0;
                _awaitingSecond[i] = false;
                _turnFlagsDeferred[i] = false;
                _cursorShifted[i] = false;   // the loaded save has its own cursor positions
            }

            string? key = SaveFingerprint();
            if (key == null || _unitArray == 0)
                return;

            string? line = ReadSavedHolds().LastOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal));
            if (line == null)
            {
                Diagnostic($"Battle loaded ({key[..8]}): nothing held");
                return;
            }

            int restored = 0;
            foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                foreach (var item in part.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (item.StartsWith('A'))
                    {
                        if (int.TryParse(item.AsSpan(1), out int picker) && picker >= 0 && picker < UNIT_COUNT)
                        {
                            _awaitingSecond[picker] = true;
                            _firstSpell[picker] = CaptureSlot(picker);
                        }
                        continue;
                    }

                    var fields = item.Split(':');
                    if (fields.Length != 4 || !int.TryParse(fields[0], out int unit) || unit < 0 || unit >= UNIT_COUNT ||
                        !int.TryParse(fields[1], out int chargeLeft) || !int.TryParse(fields[2], out int ability))
                        continue;

                    byte[] action = Convert.FromHexString(fields[3]);
                    if (action.Length != ACTION_LENGTH)
                        continue;

                    _pending[unit] = new PendingSpell
                    {
                        Armed = true,
                        Action = action,
                        Ability = ability,
                        ChargeLeft = chargeLeft,
                    };
                    restored++;
                }
            }

            Diagnostic($"Battle loaded ({key[..8]}): {restored} held spell(s) restored");
        }

        /// <summary>A hash of the unit array as it stands in the save buffer, or null if the buffer is unknown.</summary>
        private unsafe string? SaveFingerprint()
        {
            if (_saveBuffer == 0)
                return null;

            var units = new ReadOnlySpan<byte>((void*)_saveBuffer, SAVED_UNITS_LENGTH);
            return Convert.ToHexString(SHA256.HashData(units));
        }

        private List<string> ReadSavedHolds()
        {
            try
            {
                if (_savedHoldsPath != null && File.Exists(_savedHoldsPath))
                    return File.ReadAllLines(_savedHoldsPath).Where(l => l.Length > 0).ToList();
            }
            catch (Exception ex)
            {
                Warn($"Could not read {SAVED_HOLDS_FILE}: {ex.Message}");
            }

            return new List<string>();
        }

        /// <summary>
        /// The file lives in this mod's user config folder, which survives mod updates; the mod folder is the fallback.
        /// </summary>
        private string? SavedHoldsPath()
        {
            try
            {
                string dir = _modLoader.GetModConfigDirectory(_modConfig.ModId);
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, SAVED_HOLDS_FILE);
            }
            catch
            {
                try
                {
                    return Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), SAVED_HOLDS_FILE);
                }
                catch (Exception ex)
                {
                    Warn($"Held spells will not survive saving: {ex.Message}");
                    return null;
                }
            }
        }

        #endregion

        #region Helpers

        private unsafe byte* Unit(int index) => (byte*)(_unitArray + index * UNIT_STRIDE);

        /// <summary>The context passed to CommitAction starts with the unit index.</summary>
        private static unsafe int UnitIndexFromContext(nint context) => *(byte*)context;

        private unsafe int ReadMode() => _modeFlag == 0 ? -1 : *(int*)_modeFlag;

        private static unsafe nint ResolveRipRelative(nint instruction, int displacementOffset, int instructionLength)
            => instruction + instructionLength + *(int*)(instruction + displacementOffset);

        private static unsafe bool StartsWith(nint text, string expected)
        {
            byte* p = (byte*)text;
            for (int i = 0; i < expected.Length; i++)
            {
                if (p[i] != (byte)expected[i])
                    return false;
            }

            return true;
        }

        /// <summary>Every address in the main module matching a signature, including the one already found.</summary>
        private unsafe List<nint> FindAll(string signature, nint known)
        {
            var parts = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = new byte[parts.Length];
            var mask = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                mask[i] = parts[i] != "??";
                pattern[i] = mask[i] ? Convert.ToByte(parts[i], 16) : (byte)0;
            }

            var (start, size) = GetSectionBounds(known);
            var results = new List<nint>();
            if (size == 0)
                return new List<nint> { known };

            byte* section = (byte*)start;
            for (int offset = 0; offset <= size - pattern.Length; offset++)
            {
                bool hit = true;
                for (int i = 0; i < pattern.Length && hit; i++)
                {
                    if (mask[i] && section[offset + i] != pattern[i])
                        hit = false;
                }

                if (hit)
                    results.Add(start + offset);
            }

            return results.Count > 0 ? results : new List<nint> { known };
        }

        private unsafe (nint Start, int Size) GetSectionBounds(nint address)
        {
            byte* pe = (byte*)_baseAddress + *(int*)(_baseAddress + 0x3C);
            ushort sections = *(ushort*)(pe + 6);
            ushort optionalHeader = *(ushort*)(pe + 20);
            byte* section = pe + 24 + optionalHeader;
            for (int i = 0; i < sections; i++, section += 40)
            {
                uint virtualSize = *(uint*)(section + 8);
                uint virtualAddress = *(uint*)(section + 12);
                nint start = _baseAddress + (nint)virtualAddress;
                if (address >= start && address < start + (nint)virtualSize)
                    return (start, (int)virtualSize);
            }

            return (0, 0);
        }

        /// <summary>Writes over memory that may be read-only, restoring its protection afterwards.</summary>
        private static bool WriteProtected(nint address, byte[] bytes)
        {
            if (!VirtualProtect(address, (nuint)bytes.Length, PAGE_EXECUTE_READWRITE, out uint old))
                return false;

            Marshal.Copy(bytes, 0, address, bytes.Length);
            VirtualProtect(address, (nuint)bytes.Length, old, out _);
            return true;
        }

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

        /// <summary>A problem worth a player's attention. Always reaches the Reloaded console.</summary>
        private void Warn(string message)
        {
            lock (_lock)
            {
                FlushDiagnosticRepeats();
                Write(message);
            }
        }

        /// <summary>Only written with Verbose on. Consecutive repeats are collapsed into a count.</summary>
        private void Diagnostic(string message)
        {
            if (!Verbose)
                return;

            lock (_lock)
            {
                if (message == _lastDiagnostic)
                {
                    _diagnosticRepeats++;
                    return;
                }

                FlushDiagnosticRepeats();
                _lastDiagnostic = message;
                Write(message);
            }
        }

        /// <summary>Names the repeated message: diagnostics and ordinary lines share one file.</summary>
        private void FlushDiagnosticRepeats()
        {
            if (_diagnosticRepeats == 0)
                return;

            int repeats = _diagnosticRepeats;
            _diagnosticRepeats = 0;
            Write($"  (\"{_lastDiagnostic}\" repeated {repeats} more times)");
        }

        private void Write(string message)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] {message}");
            try
            {
                _writer?.WriteLine(message);
            }
            catch
            {
                // The console still has it.
            }
        }

        #endregion
    }
}
