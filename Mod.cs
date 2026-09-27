using BlueMage.Configuration;
using BlueMage.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace BlueMage
{
    /// <summary>
    /// Job data, monster rebalance and monster skillsets are applied by fftivc.utility.modloader from
    /// FFTIVC/tables/enhanced/*.xml. This code only patches the hardcoded tables the mod loader does not expose:
    /// status inflict data, unit sprites, sprite sheet types and unit portraits.
    /// </summary>
    public class Mod : ModBase
    {
        private readonly IModLoader _modLoader;
        private readonly IReloadedHooks? _hooks;
        private readonly ILogger _logger;
        private readonly IMod _owner;
        private Config _configuration;
        private readonly IModConfig _modConfig;

        private readonly nint _baseAddress;
        private readonly object _lock = new();

        private Dualcast? _dualcast;
        private ReplenishMp? _replenishMp;
        private SecondaryLearning? _secondaryLearning;

        // Needed together for the monster palette pointer fix
        private nint _ramzaJobAddress;
        private nint _monsterPaletteGlobalAddress;

        // Blue Mage and Red Mage occupy two unused special character / job slots.
        // The unit's spriteset and job are both set to these ids (see OverrideEntryData nxd).
        private const int BLUE_MAGE_ID = 0x38;
        private const int RED_MAGE_ID = 0x39;

        // Sprite files loaded for the spritesets (FFTPack ids). The mod replaces both files.
        private const uint BLUE_MAGE_SPRITE_FILE = 0x5B; // unit/battle_dami_spr.bin
        private const uint RED_MAGE_SPRITE_FILE = 0x82;  // unit/battle_kasanek_spr.bin

        // Portraits: spriteset -> face id (6 identical u16 tables) -> wldface texture number (u32 table).
        // Face ids 0x61/0x62 are unused (-1) in the vanilla face map. Face ids >= 0xA0 are only accepted from a
        // hardcoded whitelist and wldface numbers above 205 are rejected, so both must stay in range.
        private const int BLUE_MAGE_FACE_ID = 0x61;
        private const int RED_MAGE_FACE_ID = 0x62;
        private const uint BLUE_MAGE_WLDFACE = 56; // ui/ffto/common/face/texture/wldface_056_08_uitx.tex
        private const uint RED_MAGE_WLDFACE = 57;  // ui/ffto/common/face/texture/wldface_057_08_uitx.tex

        // The enhanced UI loads portraits through a wldface -> texture slot table (0..205). Numbers without a vanilla
        // portrait share the slot of an existing one (56 -> Oracle's, 57 -> Squire's), so the shared portrait is loaded
        // instead. 584 and 585 are the unused slots of the missing wldface 17 and 18.
        private const int BLUE_MAGE_FACE_SLOT = 584;
        private const int RED_MAGE_FACE_SLOT = 585;
        private const int FACE_SLOT_TABLE_COUNT = 206;

        // Before building the wldface_%03d_%02d path, the UI remaps wldface numbers that have no vanilla portrait
        // to an existing one (56 -> 118 Oracle, 57 -> 96 Squire). Our numbers must pass through unchanged.
        private const string PORTRAIT_REMAP_SIGNATURE = "8B D1 83 F9 38 0F 8F ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? 83 F9 2A 7F ?? 74 ??";
        private delegate int PortraitRemapDelegate(int wldface);
        private IHook<PortraitRemapDelegate>? _portraitRemapHook;

        private const int PORTRAIT_TABLE_COUNT = 6;

        // Sprite sheet data table (spriteset -> SHP/SEQ type). Signature is the dummy entry 0 followed by the
        // nine TYPE1 entries and the first TYPE2 one; SHP/SEQ 0 is TYPE1 (generic male), 1 is TYPE2 (generic female).
        private const string SPRITE_TYPE_TABLE_SIGNATURE =
            "00 00 00 00 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 00 00 00 24 01 01 00 24";
        private const byte SHP_SEQ_TYPE1 = 0;

        // Entry size constants
        private const int JOB_ENTRY_SIZE = 49;
        private const int STATUS_ENTRY_SIZE = 6;
        private const int SPRITE_ENTRY_SIZE = 8;
        private const int SPRITE_TYPE_ENTRY_SIZE = 4;
        private const int PORTRAIT_ENTRY_SIZE = 2;
        private const int FACEMAP_ENTRY_SIZE = 4;

        // Memory patch data structure
        private struct MemoryPatch
        {
            public string Description;
            public nint Address;
            public byte[] Data;

            public MemoryPatch(string description, nint address, byte[] data)
            {
                Description = description;
                Address = address;
                Data = data;
            }
        }

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;

#if DEBUG
            // Attaches debugger in debug mode
            Debugger.Launch();
#endif
            _baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;

            var startupScannerController = _modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out var startupScanner))
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Error: Could not get startup scanner");
                return;
            }

            // Status inflict table (6 bytes per entry)
            startupScanner.AddMainModuleScan("00 00 00 00 00 00 10 00 00 00 80 00 10 00 20 00 00 00 10 00 08 00 00 00 10 00 00 02 00 00 10 00", result =>
            {
                if (!Found(result.Found, "Status table"))
                    return;

                var statusTable = _baseAddress + result.Offset;
                ApplyPatches(
                    new MemoryPatch("Mighty Guard Inflict Status (7A)", statusTable + STATUS_ENTRY_SIZE * 0x7A, new byte[] { 0x80, 0x00, 0x00, 0x00, 0x38, 0x00 }),
                    new MemoryPatch("Blaster Inflict Status (7B)", statusTable + STATUS_ENTRY_SIZE * 0x7B, new byte[] { 0x40, 0x00, 0x80, 0x00, 0x06, 0x00 }));
            });

            // Spriteset -> sprite file tables, from the two LEAs of the palette loader.
            // Entries are { u32 fftpack file id, u32 read size }; only the file id is changed.
            startupScanner.AddMainModuleScan("48 8D 0D ?? ?? ?? ?? 4C 8D 35 ?? ?? ?? ?? 4C 0F 44 F1 43 8B 6C FE 04", result =>
            {
                if (!Found(result.Found, "Sprite table code"))
                    return;

                var code = _baseAddress + result.Offset;
                var patches = new List<MemoryPatch>();
                foreach (var table in new[] { ResolveRipRelative(code, 3, 7), ResolveRipRelative(code + 7, 3, 7) })
                {
                    patches.Add(new MemoryPatch("Blue Mage Sprite", table + SPRITE_ENTRY_SIZE * BLUE_MAGE_ID, BitConverter.GetBytes(BLUE_MAGE_SPRITE_FILE)));
                    patches.Add(new MemoryPatch("Red Mage Sprite", table + SPRITE_ENTRY_SIZE * RED_MAGE_ID, BitConverter.GetBytes(RED_MAGE_SPRITE_FILE)));
                }
                ApplyPatches(patches.ToArray());
            });

            // Spriteset -> sprite sheet data (4 bytes per entry: SHP id, SEQ id, flying flag, graphic height).
            // The SHP/SEQ ids pick the frame assembly and animation sequence files the sheet is drawn for; both
            // mage sheets are generic male (TYPE1) sheets, but spriteset 0x38 is a TYPE2 (generic female) slot,
            // which animates the wrong frames. Only the two type bytes are changed, height and flying are kept.
            startupScanner.AddMainModuleScan(SPRITE_TYPE_TABLE_SIGNATURE, result =>
            {
                if (!Found(result.Found, "Sprite sheet data table"))
                    return;

                var spriteTypeTable = _baseAddress + result.Offset;
                ApplyPatches(
                    new MemoryPatch("Blue Mage Sprite Type", spriteTypeTable + SPRITE_TYPE_ENTRY_SIZE * BLUE_MAGE_ID, new byte[] { SHP_SEQ_TYPE1, SHP_SEQ_TYPE1 }),
                    new MemoryPatch("Red Mage Sprite Type", spriteTypeTable + SPRITE_TYPE_ENTRY_SIZE * RED_MAGE_ID, new byte[] { SHP_SEQ_TYPE1, SHP_SEQ_TYPE1 }));
            });

            // Spriteset -> face id tables. There are six identical copies, each used by a different screen.
            startupScanner.AddMainModuleScan("00 00 00 00 01 00 02 00 03 00 78 00 79 00 04 00 7A 00 7B 00 7C 00 7D 00 05 00 06 00 7E", result =>
            {
                if (!Found(result.Found, "Portrait tables"))
                    return;

                var tables = FindAllInSection(_baseAddress + result.Offset, new byte[]
                {
                    0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x78, 0x00, 0x79, 0x00, 0x04, 0x00,
                    0x7A, 0x00, 0x7B, 0x00, 0x7C, 0x00, 0x7D, 0x00, 0x05, 0x00, 0x06, 0x00, 0x7E
                });
                if (tables.Count != PORTRAIT_TABLE_COUNT)
                    _logger.WriteLine($"[{_modConfig.ModId}] Warning: expected {PORTRAIT_TABLE_COUNT} portrait tables, found {tables.Count}");

                var patches = new List<MemoryPatch>();
                foreach (var table in tables)
                {
                    patches.Add(new MemoryPatch("Blue Mage Portrait", table + PORTRAIT_ENTRY_SIZE * BLUE_MAGE_ID, BitConverter.GetBytes((ushort)BLUE_MAGE_FACE_ID)));
                    patches.Add(new MemoryPatch("Red Mage Portrait", table + PORTRAIT_ENTRY_SIZE * RED_MAGE_ID, BitConverter.GetBytes((ushort)RED_MAGE_FACE_ID)));
                }
                ApplyPatches(patches.ToArray());
            });

            // Face id -> wldface table, from the LEA in the face lookup helper
            startupScanner.AddMainModuleScan("48 8D 0D ?? ?? ?? ?? 8B 04 81 C3 B8 C2 00 00 00", result =>
            {
                if (!Found(result.Found, "Face map code"))
                    return;

                var faceMap = ResolveRipRelative(_baseAddress + result.Offset, 3, 7);
                WarnIfFaceIdUsed(faceMap, BLUE_MAGE_FACE_ID);
                WarnIfFaceIdUsed(faceMap, RED_MAGE_FACE_ID);
                ApplyPatches(
                    new MemoryPatch("Blue Mage Face", faceMap + FACEMAP_ENTRY_SIZE * BLUE_MAGE_FACE_ID, BitConverter.GetBytes(BLUE_MAGE_WLDFACE)),
                    new MemoryPatch("Red Mage Face", faceMap + FACEMAP_ENTRY_SIZE * RED_MAGE_FACE_ID, BitConverter.GetBytes(RED_MAGE_WLDFACE)));
            });

            // wldface -> UI texture slot table
            startupScanner.AddMainModuleScan("FF FF FF FF 38 02 00 00 39 02 00 00 3A 02 00 00 3B 02 00 00 3C 02 00 00", result =>
            {
                if (!Found(result.Found, "Face slot table"))
                    return;

                var slotTable = _baseAddress + result.Offset;
                WarnIfFaceSlotUsed(slotTable, BLUE_MAGE_FACE_SLOT);
                WarnIfFaceSlotUsed(slotTable, RED_MAGE_FACE_SLOT);
                ApplyPatches(
                    new MemoryPatch("Blue Mage Face Slot", slotTable + FACEMAP_ENTRY_SIZE * (int)BLUE_MAGE_WLDFACE, BitConverter.GetBytes(BLUE_MAGE_FACE_SLOT)),
                    new MemoryPatch("Red Mage Face Slot", slotTable + FACEMAP_ENTRY_SIZE * (int)RED_MAGE_WLDFACE, BitConverter.GetBytes(RED_MAGE_FACE_SLOT)));
            });

            // wldface remap applied before loading a portrait, see PORTRAIT_REMAP_SIGNATURE
            startupScanner.AddMainModuleScan(PORTRAIT_REMAP_SIGNATURE, result =>
            {
                if (!Found(result.Found, "Portrait remap function"))
                    return;

                if (_hooks == null)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Error: Could not get hooks, portraits will not be replaced");
                    return;
                }

                _portraitRemapHook = _hooks.CreateHook<PortraitRemapDelegate>(PortraitRemapImpl, _baseAddress + result.Offset).Activate();
                _logger.WriteLine($"[{_modConfig.ModId}] Hooked portrait remap at {_baseAddress + result.Offset:X}");
            });

            // Ramza Chapter 1 job entry, used as a safe default for the monster palette pointer
            startupScanner.AddMainModuleScan("19 00 00 00 00 00 00 00 00 D0 40 06 FF 00 0B 78 0B 69 5F 64 32 6E 30 64 04 03 0A 00 00 00 00 00 00 40", result =>
            {
                if (!Found(result.Found, "Ramza Chapter 1 job data"))
                    return;

                lock (_lock)
                {
                    _ramzaJobAddress = _baseAddress + result.Offset;
                    TrySeedMonsterPaletteGlobal();
                }
            });

            // Global pointer to the last monster's job data, stored here and read for the portrait palette.
            // Portrait code reads it without a null check for face ids >= 0x3F, which crashes if no monster
            // portrait was drawn yet. Our face ids are in that range.
            startupScanner.AddMainModuleScan("48 89 0D ?? ?? ?? ?? 0F B6 49 2E 41 80 F8 5B", result =>
            {
                if (!Found(result.Found, "Monster palette pointer"))
                    return;

                lock (_lock)
                {
                    _monsterPaletteGlobalAddress = ResolveRipRelative(_baseAddress + result.Offset, 3, 7);
                    TrySeedMonsterPaletteGlobal();
                }
            });

            // Dualcast (support ability 481), Replenish MP (reaction ability 443) and learning on hit through the
            // secondary skillset install their own hooks; they share the scanner and hooks APIs.
            if (_hooks != null)
            {
                _dualcast = new Dualcast(_modLoader, _hooks, _logger, _modConfig, startupScanner);
                _replenishMp = new ReplenishMp(_hooks, _logger, _modConfig, startupScanner);
                _secondaryLearning = new SecondaryLearning(_hooks, _logger, _modConfig, startupScanner);
            }
            else
                _logger.WriteLine($"[{_modConfig.ModId}] Error: no hooks API, Dualcast, Replenish MP and secondary skillset learning will not be active");
        }

        private int PortraitRemapImpl(int wldface)
        {
            if (wldface == BLUE_MAGE_WLDFACE || wldface == RED_MAGE_WLDFACE)
                return wldface;

            return _portraitRemapHook!.OriginalFunction(wldface);
        }

        private bool Found(bool found, string name)
        {
            if (!found)
                _logger.WriteLine($"[{_modConfig.ModId}] {name} could not be found");
            return found;
        }

        private unsafe void TrySeedMonsterPaletteGlobal()
        {
            if (_ramzaJobAddress == 0 || _monsterPaletteGlobalAddress == 0)
                return;

            // Only fill it if the game has not set it; Ramza's entry has a monster palette of 0
            if (*(nint*)_monsterPaletteGlobalAddress == 0)
                ApplyPatches(new MemoryPatch("Monster Palette Pointer Default", _monsterPaletteGlobalAddress, BitConverter.GetBytes((long)_ramzaJobAddress)));
        }

        private unsafe void WarnIfFaceIdUsed(nint faceMap, int faceId)
        {
            uint current = *(uint*)(faceMap + FACEMAP_ENTRY_SIZE * faceId);
            if (current != uint.MaxValue)
                _logger.WriteLine($"[{_modConfig.ModId}] Warning: face id {faceId:X} was already mapped to wldface {current}, another mod may be using it");
        }

        private unsafe void WarnIfFaceSlotUsed(nint slotTable, int slot)
        {
            for (int i = 0; i < FACE_SLOT_TABLE_COUNT; i++)
            {
                if (*(int*)(slotTable + FACEMAP_ENTRY_SIZE * i) == slot)
                    _logger.WriteLine($"[{_modConfig.ModId}] Warning: face slot {slot} is already used by wldface {i}, another mod may be using it");
            }
        }

        // Reads a rip-relative operand: target = instruction + instructionLength + disp32
        private static unsafe nint ResolveRipRelative(nint instruction, int displacementOffset, int instructionLength)
        {
            return instruction + instructionLength + *(int*)(instruction + displacementOffset);
        }

        // Finds every occurrence of a pattern within the PE section containing 'address'
        private unsafe List<nint> FindAllInSection(nint address, byte[] pattern)
        {
            var results = new List<nint>();
            var (start, size) = GetSectionBounds(address);
            if (size == 0)
                return results;

            var section = new ReadOnlySpan<byte>((void*)start, size);
            int offset = 0;
            while (true)
            {
                int index = section.Slice(offset).IndexOf(pattern);
                if (index < 0)
                    break;

                results.Add(start + offset + index);
                offset += index + 1;
            }
            return results;
        }

        private unsafe (nint Start, int Size) GetSectionBounds(nint address)
        {
            byte* pe = (byte*)_baseAddress + *(int*)(_baseAddress + 0x3C);
            ushort numSections = *(ushort*)(pe + 6);
            ushort optionalHeaderSize = *(ushort*)(pe + 20);
            byte* section = pe + 24 + optionalHeaderSize;
            for (int i = 0; i < numSections; i++, section += 40)
            {
                uint virtualSize = *(uint*)(section + 8);
                uint virtualAddress = *(uint*)(section + 12);
                nint start = _baseAddress + (nint)virtualAddress;
                if (address >= start && address < start + (nint)virtualSize)
                    return (start, (int)virtualSize);
            }
            return (0, 0);
        }

        private void ApplyPatches(params MemoryPatch[] patches)
        {
            // Scan callbacks may run concurrently and patches can share pages, so protection changes are serialized
            lock (_lock)
            {
                int successfulPatches = 0;
                foreach (var patch in patches)
                {
                    if (ApplySinglePatch(patch))
                        successfulPatches++;
                }

                if (successfulPatches != patches.Length)
                    _logger.WriteLine($"[{_modConfig.ModId}] Failed: {patches.Length - successfulPatches}/{patches.Length}");
            }
        }

        private unsafe bool ApplySinglePatch(MemoryPatch patch)
        {
            try
            {
                byte* ptr = (byte*)patch.Address;

                // Make memory writable
                if (!VirtualProtect(patch.Address, (UIntPtr)patch.Data.Length, MemoryProtection.ExecuteReadWrite, out uint oldProtect))
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Failed to change memory protection for '{patch.Description}'");
                    return false;
                }

                // Write the bytes
                for (int i = 0; i < patch.Data.Length; i++)
                {
                    ptr[i] = patch.Data[i];
                }

                // Restore original protection
                VirtualProtect(patch.Address, (UIntPtr)patch.Data.Length, (MemoryProtection)oldProtect, out _);

                // Verify the patch
                for (int i = 0; i < patch.Data.Length; i++)
                {
                    if (ptr[i] != patch.Data[i])
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] Verification failed for '{patch.Description}' at byte {i}");
                        return false;
                    }
                }

                _logger.WriteLine($"[{_modConfig.ModId}] Applied '{patch.Description}' at {patch.Address:X} ({patch.Data.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Exception while applying '{patch.Description}': {ex.Message}");
                return false;
            }
        }

        #region Windows API
        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize,
            MemoryProtection flNewProtect, out uint lpflOldProtect);

        [Flags]
        private enum MemoryProtection : uint
        {
            Execute = 0x10,
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            NoAccess = 0x01,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,
            GuardModifierflag = 0x100,
            NoCacheModifierflag = 0x200,
            WriteCombineModifierflag = 0x400
        }
        #endregion

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}
