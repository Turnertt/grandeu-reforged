# Find Game Values by Identity, Not Address

An external editor keeps finding the same value only if it stops storing addresses. Instead it should store a recipe that rebuilds the value's location from something the game cannot run without: a static global reached through a code signature, an object's type identity, or, the strongest option for Unreal Engine 3, the engine's own object and name tables plus the reflection data that names every field. Each of these anchors survives a different kind of movement:

- **Pointer paths** from a static base survive heap reallocation and restarts, but break when a patch moves globals or struct fields.
- **AOB signatures** survive patches only for as long as the chosen code bytes do.
- **Reflection by name** survives patches for as long as two engine globals can still be found.

Every rung needs validation on each use. The usual failure in every source is not "not found" but "found something that looks right": more than 99.99% of single-session pointer-scan hits are coincidences, and freed objects still match structural signatures.

GrandeuReforged already sits at the top of this hierarchy. It resolves game-class offsets by name, finds its key objects by name in GObjects, and checks self-index identity. Its remaining fragility is concentrated in two places:

- **The bootstrap.** GObjects and GNames are each found by one signature, and the first plausible hit is accepted.
- **The reflection trust gate.** It treats "the reflection reader works" and "these three fields have not moved" as one question.

The most valuable upgrades, in order:

1. A signature-free, content-validated fallback that finds GObjects and GNames in the exe's data section.
2. A self-referential calibration of the core reflection offsets.
3. ObjectFlags-based liveness checks.
4. Turning Item/Hero Search hits into name paths so results persist across sessions.

Code caves and debug-register capture should stay out of the shipped tool.

## Four kinds of movement call for four different anchors

"The address changed" covers four separate events, and treating them as one is the root of most brittle tools:

- **Heap reallocation and respawn** move an object within one session.
- **A restart** replays every allocation.
- **A patch** moves code, sometimes globals, and occasionally struct fields.

Fixed heap addresses almost never carry over. In DeepMem's study, **only 1.32% of `_EPROCESS` objects (4.7% of `_ETHREAD`) sat at the same virtual address across memory dumps** ([DeepMem, CCS 2018](https://www.cs.ucr.edu/~heng/pubs/deepmem_ccs18.pdf)).

Static globals are the opposite case. The linker strips base relocations from EXEs by default, and an image without relocations "must therefore be loaded at its preferred base address", which for an EXE is **0x00400000** ([Microsoft PE Format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)). So in a non-ASLR 32-bit game every global has the same address on every run. All movement between sessions is heap movement, and signatures are needed only to survive patches.

Patches break things at two layers. Code bytes shift, and struct displacements embedded in instructions change: one Cyberpunk 2077 patch turned `movzx ecx,word ptr [rsi+340]` into `[rsi+350]` ([badecho](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)). Data layouts mostly hold, but not always. **DD1's 2026-06/07 patch moved `ItemBoxEquipments` from 0x39C to 0x3A8 and shifted the hero arrays beside it.** The pawn → HeroManager chain and the native item and hero blocks held ([DECISIONS.md](../DECISIONS.md); [DD1_INTERNALS.md §3](../DD1_INTERNALS.md)).

| Technique | Heap realloc / respawn | Restart | Code-moving patch | Access needed | Crash risk |
|---|---|---|---|---|---|
| Cached raw address | No | No | No | RPM | None if validated |
| Pointer path from a static base | Only if the path avoids transient objects | Yes (non-ASLR) | No, unless the base and offsets are re-derived | RPM | None |
| AOB signature → global → chain | Yes | Yes | Only if the signature avoids volatile bytes | RPM | None |
| vtable scan (stored vtable address) | Yes | Yes | No (vtable moves) | RPM | None; false positives |
| RTTI name → vtable | Yes | Yes | Yes | RPM | None, but needs RTTI in the binary |
| UE3 reflection by name | Yes | Yes | Yes, while GObjects/GNames can be found | RPM | None |
| Structural heap sweep + validation | Yes | Yes | Yes (no dependence on code) | RPM | None; false positives |
| Code-cave pointer grabber | Yes | Must reinstall each session | Depends on its signature | WPM + VirtualAllocEx | Moderate |
| Hardware-breakpoint capture | Per access event | Per session | Yes, as a discovery tool | Debugger attach | High (hang/kill) |

The pattern in the table is the central point. Only techniques anchored to **data the engine itself must keep consistent** (reflection, structural invariants) survive all four kinds of movement without a person in the loop. Everything else either re-derives its anchor from code, which a patch can break, or caches something that moves.

## Pointer maps buy restart stability, not patch stability

### How Cheat Engine finds pointer paths

Cheat Engine's pointer scanner works in two steps:

1. **Build a pointer map.** It snapshots every pointer-sized value in readable memory that points into valid memory, and stores the snapshot as a reverse index from value to the slots holding it. The index is a 16-way nibble trie, 8 levels deep for 32-bit targets ([CE pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)).
2. **Search backward.** A depth-first reverse search starts from the target. At each level it asks which slots hold a value in `[target − maxOffset, target]`. It stops at a module-relative ("static") slot or at the maximum depth, and prunes with options:
   - `noLoop`
   - `maxOffsetsPerNode`
   - `onlyOneStaticInPath`
   - `mustEndWithSpecificOffset`

   ([CE pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas))

The official defaults are a max level of 5, and changing the max offset only "if you suspect an offset larger than 2048 bytes" ([CE help](https://www.cheatengine.org/help/pointer-scan.htm)). Open-source scanners share the same core with different index structures. ptrscan uses a Rust `BTreeMap` range query ([ptrscan](https://docs.rs/ptrscan/latest/src/ptrscan/lib.rs.html)). PyMemoryEditor builds its map lazily and warns that going from depth 4 to depth 6 is "usually a 10× slowdown" ([PyMemoryEditor](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)).

### Cross-session comparison does most of the filtering

Raw results are almost all noise. A CE forum regular estimates **over 99.99% of single-map results are coincidental**. A typical tell is an offset larger than the struct it points into ([CE forum 621469](https://forum.cheatengine.org/viewtopic.php?t=621469)).

The fix is cross-session comparison. CE loads a second pointer map taken from a different game instance and re-resolves each candidate against it before writing the result. Its author says this "instant rescan" **cuts disk needs from about 200 GB to 1 GB or less**. He recommends:

- capturing the maps after a game or PC restart;
- setting "max different offsets per node" to 2–3, which yields fewer results with small offsets.

([CE forum, Pointerscan how-to](https://forum.cheatengine.org/viewtopic.php?p=5745475))

Each extra map is a filter with essentially no false-negative cost: a true stable path survives every map, and coincidental ones almost never resolve twice.

### Ranking the surviving paths

Ranking signals, in order of weight:

1. **Validation breadth.** Prefer paths that survived several restarts and varied game states.
2. **Shallow depth.**
3. **Small, aligned offsets.**
4. **A base in the main exe's static data.** PSR argues that sequences beginning at an executable-relative static address "are generally stable across application restarts" ([NCC Group PSR](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)).
5. **Real objects at each hop.** Intermediate nodes should be objects with a vtable at offset 0, which PSR also uses.
6. **Several independent aliases agreeing on the same object.**

A Kingdom Hearts 3 trainer write-up shows these signals in practice ([dev.to KH3 trainer](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)):

- The author capped depth at 2 and kept **three surviving chains rooted in the main exe**, two of them from roots 0x30 apart. Keeping several aliases lets the tool "detect disagreement if a future build shifts one global" instead of failing silently.
- The same project hit the **max-offset trap**: the real final hop, `+0x7E04`, exceeded CE's default window, so the true chain was never searched. The fix was to scan for the struct base and treat `+0x7E04` as a known terminal offset. CE's `mustEndWithSpecificOffset` option exists for exactly this case.

### Common failure modes

- **Transient objects.** In KH3, HP relocated mid-session, and an address-suffix filter worked across restarts but broke on area transitions ([dev.to KH3 trainer](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)).
- **The stack.** PSR ignores reads through ESP/EBP because "sequences that traverse the stack are usually unstable" ([NCC Group PSR](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)).
- **Freed-and-reallocated memory (ABA).** A traced sequence can traverse memory that was freed and reused for a different object.

### Tracing instead of brute force

PSR itself takes a different route from brute force. It attaches with the debug API, sets a PAGE_GUARD trap on the target, single-steps threads, and walks the trace backward from the accessing register to a static read. The paper calls this faster and usually more accurate than CE, which "can take hours" for several levels and "produce[s] large numbers of unstable sequences".

### What pointer paths can and cannot survive

Restart stability is not update stability. Any recompile can move globals and change struct layouts, so a pointer path survives a patch only if its base can be re-derived (by signature or engine metadata) and its offsets can be re-derived (by reflection). An external C# x86 tool can implement the whole CE pipeline with sorted `uint[]` arrays and `ReadProcessMemory`. The catch is the tool's own memory: a 32-bit host has 2 GB of user address space, or 4 GB when large-address-aware on 64-bit Windows ([Microsoft memory limits](https://learn.microsoft.com/en-us/windows/win32/memory/memory-limits-for-windows-releases)), so a map with tens of millions of pointers belongs in a helper process or on disk.

The verdict for engine-reflective games: when the object can be found directly each session, a pointer path becomes a cached fast route rather than a correctness requirement.

## Signatures re-find code anchors; hooks and breakpoints capture registers at a price

### A good signature wildcards everything the linker or a patch can move

Signature practice has settled on a few rules. AlliedModders' guide says:

- wildcard absolute addresses, relative `call` displacements and stack offsets;
- keep prologue opcodes and register-only instructions fixed;
- check uniqueness separately, because its sample scanner "returns only the first match".

([AlliedModders: Signature Scanning](https://wiki.alliedmods.net/Signature_Scanning))

Generators such as SigMaker append instructions until the pattern matches exactly once, and fall back to "signatures by data or code xrefs" (call sites that reference the target) when the target's own bytes are not unique ([IDA-Pro-SigMaker](https://github.com/A200K/IDA-Pro-SigMaker)). Patch-survival advice from trainer authors adds three more rules ([badecho](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)):

- avoid instructions that embed a static address;
- prefer small displacements;
- keep the original-bytes assert. "Removing it may let injection succeed but usually causes a crash."

Resolving a match is simpler on x86-32 than in the x64 tutorials most code is copied from:

- **Globals.** The absolute address of a global is embedded literally in the instruction. DD1's GNames and GObjects patterns read the static TArray's address at `match+2` ([DD dumper NamesStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/NamesStore.cpp); [ObjectsStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/ObjectsStore.cpp)).
- **Relative branches.** A branch resolves as `site + 5 + rel32`, which GH-Offset-Dumper models as `opLoc: 1, opLength: 5` for an `E8` call ([GH-Offset-Dumper](https://github.com/guidedhacking/GH-Offset-Dumper)).

### Redundancy, string anchors and scan speed

**Redundancy is the community's main hedge against a broken signature:**

- asr tries an ordered list of candidate signatures per engine global, and the first hit wins ([asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)).
- LiveSplit's `SigScanTarget` holds several signatures ([LiveSplit SignatureScanner.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)).
- hazedumper ships a second, independent derivation of LocalPlayer because its signature "was broken a few times and/or pointed to something wrong" ([hazedumper](https://github.com/frk1/hazedumper)).

String anchors change less often than instruction bytes. UE3's object validity check logs `"Invalid object index %i"` ([UnObj.cpp L322](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L322)). A Rocket League write-up finds GObjects by following that string's cross-reference into `IsValid` and reading the globals used there ([dev.to Rocket League UE3](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)). On x86-32 a string's address appears in code as a literal 4-byte immediate, so this cross-reference can be found by scanning `.text` for those bytes, with no disassembler.

Scan speed is not the constraint. Reloaded.Memory.SigScan measured **30,025 MB/s on a 200 MiB buffer with AVX** ([Reloaded.Memory.SigScan](https://github.com/Reloaded-Project/Reloaded.Memory.SigScan)), and unrealsdk anchors on the rarest byte common to all patterns before the masked compare ([unrealsdk multi_sigscan.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/multi_sigscan.h)). The cross-process copy dominates the cost. Correctness is the real issue: `ReadProcessMemory` "fails if the requested read operation crosses into an area of the process that is inaccessible" ([Microsoft ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory)). Wide scans must therefore follow region boundaries, and a scanner should read once and match every pattern against the same buffer.

### Code caves and debug registers read what the game holds in registers

Some values live only in a register at the moment the game touches them. Two families of techniques capture them.

**A code-cave "pointer grabber"** overwrites at least 5 bytes at an instruction that holds the object pointer with a `jmp` into allocated memory. That memory stores the register into a slot, replays the displaced instructions, and jumps back, while the external tool polls the slot. The KH3 author's rule is to use execution-hook capture for values that relocate mid-session and static chains for session-stable ones. He also found the obvious hook impractical, because it fired only when the game touched the value, for example on a shop purchase ([dev.to KH3 trainer](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)).

The patch carries real crash risks:

- **Mid-instruction threads.** Raymond Chen explains that patching more than one instruction risks a thread being "in the middle of executing an instruction that overlaps the first five bytes", which is why Windows reserved a 2-byte `MOV EDI,EDI` hot-patch point ([The Old New Thing, 2011](https://devblogs.microsoft.com/oldnewthing/20110921-00/?p=9583)).
- **Stale instruction cache.** Modified code requires `FlushInstructionCache`, because "the CPU cannot detect the change" ([Microsoft FlushInstructionCache](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-flushinstructioncache)).

**Hardware data breakpoints**, the mechanism behind Cheat Engine's "find out what accesses this address", are worse for an external tool. The breakpoint exception has to be caught by an attached debugger, or it goes to the game's own handlers, where an unhandled exception will most likely crash it. Attaching a debugger has its own costs:

- `DebugActiveProcess` suspends every thread on attach.
- "Exiting the debugger also exits the process unless you use the DebugSetProcessKillOnExit function" ([Microsoft DebugActiveProcess](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-debugactiveprocess)).
- Debug registers must be set on suspended threads, because setting the context of a running thread gives "unpredictable" results ([Microsoft SetThreadContext](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadcontext)).
- Thread suspension is itself deadlock-prone ([The Old New Thing, 2025](https://devblogs.microsoft.com/oldnewthing/20250411-00/?p=111066)).
- Only four addresses can be watched at once.
- Under Wine, memory access already relies on ptrace, and a reported bug shows `ReadProcessMemory` failing when the target is already under gdb ([wine-bugs 2019-05](https://www.winehq.org/pipermail/wine-bugs/2019-May/515418.html)).

Both families belong to development-time discovery (finding which instruction touches a value, then signing it), not to a shipped runtime path, whenever a read-only route exists.

## UE3 reflection shrinks patch exposure to two globals and ten offsets

Every UE3 SDK generator follows the same plan:

1. Hand-define a small "bootstrap" set of core reflection offsets.
2. Iterate GObjects and pick out the class objects.
3. Walk each class's `Children → Next` chain to list its properties.
4. Sort the properties by `Offset` and fill gaps with padding.

([TheFeckless SDK generator](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/TFL_SdkGen.cpp))

bl-sdk/unrealsdk, a 32-bit UE3 runtime for Borderlands 2, keeps per-game offsets only for the core reflection types and resolves everything else by name at runtime ([unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)). The two roots are static TArrays: `static TArray<UObject*> GObjObjects` and `static TArrayNoInit<FNameEntry*> Names` ([UnObjBas.h L1178](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1178); [UnName.h L572](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L572)). So in a non-ASLR exe they sit at fixed addresses in the data section for each build.

### Finding the two tables in DD1

For DD1 the two tables are found with published signatures:

- GObjects: `8B ?? ?? ?? ?? ?? 8B 04 ?? 8B 40 ?? 25 00 02 00 00`
- GNames: `8B 0D ?? ?? ?? ?? 83 3C 81 00 74`

Both read the table's address at `+2` ([DD dumper ObjectsStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/ObjectsStore.cpp)). unrealsdk annotates the same GObjects shape in Borderlands 2 as `mov ecx,[GObjects]; mov eax,[ecx+esi*4]; mov eax,[eax+08]; and eax,00000200`. That is the engine reading an object's flags and testing the class-default-object bit ([unrealsdk bl2 globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)).

Validation invariants are free:

- **Name table.** Names 0, 1 and 2 are hardcoded as `None`, `ByteProperty` and `IntProperty`, and 20 and 21 as `Core` and `Engine` ([UnNames.h L30](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnNames.h#L30)).
- **Object table.** Each object's `Index` is the "Index of object into GObjObjects array" ([UnObjBas.h L1125](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1125)), so `GObjects[obj.Index] == obj` holds for every live object.

### The core layout varies between licensee builds

The UObject header is stable across 2009–2013 PC builds in the fields that matter: Outer at 0x28, Name at 0x2C, Class at 0x34, ObjectArchetype at 0x38. The few core offsets around it do vary between licensee builds. CodeRed's generator template warns outright: "SUPERFIELD CAN EITHER BE HERE, OR IN USTRUCT DEPENDING ON THE GAME!" ([CodeRed-Generator GameDefines.hpp](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L1185)).

| Core field | UDK / BL2 / SMNC order | DD1 (dumper; live-verified by GrandeuReforged) |
|---|---|---|
| UObject internal index | 0x20 | **0x04** |
| SuperField | UStruct +0x48 | **UField +0x3C** |
| UField.Next | 0x3C | **0x40** |
| UStruct.Children / PropertySize | 0x4C / 0x50 | 0x4C / 0x50 |
| UProperty.Offset | 0x60 | **0x64** |
| Subclass slot (BitMask, PropertyClass, Inner) | 0x7C–0x80 | **0x84** |

Sources: [TheFeckless GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h); [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h); [DD dumper EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp); [DD1_INTERNALS.md §3a](../DD1_INTERNALS.md).

### Calibrating the core offsets from the engine's own description

The +4 shift between DD1 and BL2 is exactly the kind of variance a hardcoded bootstrap cannot absorb. UE3 offers a self-referential way to calibrate it. `Core.Object` declares its own header (`ObjectInternalInteger`, `Outer`, `Name`, `Class`, `ObjectArchetype`) as native script variables ([Object.uc L32](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Classes/Object.uc#L32)). The `Offset` fields of those UProperties must therefore equal the header offsets every walker already depends on. That gives five independent equations, enough to pin `UProperty.Offset` to a single slot. The class chain `BoolProperty → Property → Field → Object → null` pins SuperField the same way.

### Objects change identity in place

Slots are recycled: on destruction the engine runs `GObjObjects(Index) = NULL; GObjAvailable.AddItem(Index)`, and the next allocation pops that index ([UnObj.cpp L1452](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L1452); [UnObj.cpp L7799](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L7799)). A cached `(slot, pointer)` pair can silently become a different object. A destroyed object also lingers. `IsPendingKill()` means "dead but memory still valid" ([UnObjBas.h L1410](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1410)), so such an object stays in the table, at its slot, until purge. The flags that tell these states apart, from the 2013 engine headers:

| Flag | Value | Where it sits in the 32-bit object |
|---|---|---|
| `RF_ClassDefaultObject` | 0x200 | Low dword |
| `RF_ArchetypeObject` | 0x400 | Low dword |
| `RF_Unreachable` | `0x0000000200000000` | High dword |
| `RF_PendingKill` | `0x2000000000000000` | High dword |

([CodeRed GameDefines.hpp](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L129))

These values come from 2013 headers, so DD1's 2011 build must confirm them live. Class templates also appear in instance scans: one write-up found "9 instances of the player class, one per player plus the class template" ([dev.to Rocket League UE3](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)).

### Reaching the current player and storing paths by name

The canonical route to the current player is fully reflected:

1. `Engine.GamePlayers`, an array of LocalPlayer ([Engine.uc L351](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Engine.uc#L351))
2. `Player.Actor`, the PlayerController ([Player.uc L12](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Player.uc#L12))
3. `Controller.Pawn` ([Controller.uc L27](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Controller.uc#L27))

An external tool can enter this chain from the GameEngine instance found in GObjects, with no `GEngine` signature at all.

Modern runtimes build their path formats on the same idea:

- asr's `UnrealPointer` mixes field names and literal offsets per hop. It caches resolved offsets incrementally, so a path whose tail is not loaded yet keeps its solved prefix ([asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)).
- asl-help's `Make` resolves `"Object", "Field", "Field"` chains through each class's field map ([asl-help UEMemManager.API.Make.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.API.Make.cs)).
- apple1417's notes add the external-tool discipline: cache name indexes, compare integers rather than strings, and validate offsets by decoding the names they point at ([apple1417 gist](https://gist.github.com/apple1417/b23f91f7a9e3b834d6d052d35a0010ff)).

## Heap sweeps need forensic-grade validation, not one vtable match

### Type identity via RTTI is a dead end for DD1

On x86 MSVC binaries, type identity can be read statically. `vtable[-1]` points to a CompleteObjectLocator, whose TypeDescriptor holds the mangled name `.?AV<Class>@@`, and on x86 every link is an absolute pointer ([OpenRCE, Reversing MSVC Part II](http://www.openrce.org/articles/full_view/23); [LLVM ubsan_type_hash_win.cpp](https://codebrowser.dev/llvm/compiler-rt/lib/ubsan/ubsan_type_hash_win.cpp.html)). Name-to-vtable resolution is therefore a pure-RPM pass over the image, but only when RTTI was compiled in. For DD1 it was not. The installed 14,790,656-byte exe contains **zero** `.?AV` type descriptors, and the constructor-signature alternative was rejected as version-fragile guesswork ([DECISIONS.md](../DECISIONS.md)).

Even with RTTI, a UE3 vtable identifies only the nearest native C++ class. UnrealScript-only subclasses have no C++ class of their own, so a vtable scan returns a superset that the UObject `Class` pointer has to narrow down.

### Walking the address space externally

Region walking is the external substrate for any heap search. `VirtualQueryEx` reports `State`, `Type` and `Protect` per region ([Microsoft MEMORY_BASIC_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-memory_basic_information)), and a sweep keeps committed, private, writable, non-guard pages. The Toolhelp heap walkers are a trap: Microsoft itself calls `Heap32First`/`Heap32Next` inefficient for large heaps ([Microsoft: Traversing the Heap List](https://learn.microsoft.com/en-au/windows/win32/toolhelp/traversing-the-heap-list)). Byte-by-byte reads are worse. One report took **about 50 s to scan about 70 MB**, against under 2–10 s once reads were batched per region ([CodeProject forum](https://forum.codeproject.com/post/692128)). Reads that run past a region's end fail with `ERROR_PARTIAL_COPY` (299) ([python-forum.io](https://python-forum.io/thread-5517.html)). Large regions should therefore be read in bounded chunks that overlap by the candidate struct's size, not read whole or skipped.

### What the forensics literature says about validation

Validation is where the memory-forensics literature earns its place, because its problem (find every instance of a structure in a raw image and reject look-alikes) is exactly the heap-sweep problem.

**Choose only fields the program cannot run without.** Dolan-Gavitt et al. profiled and fuzzed Windows' `EPROCESS`:

- **29 of the 72 frequently accessed fields survived every manipulation.** Since the OS works no matter what they hold, they are useless for a signature.
- The final signature used **the 15 most robust fields**.
- **Only 2 of a prior scanner's 9 hand-picked features** resisted evasion.
- **Train on the full lifecycle.** The robust scanner missed terminated processes because Windows zeroes token fields on exit, which shows "the importance of generating signatures from a training set that represents the full range of objects".

([Dolan-Gavitt et al., CCS 2009](https://www.cs.columbia.edu/~brendan/ccs09_siggen.pdf))

**Pointer topology beats value invariants.** SigGraph's graph signatures identified every instance of **16 of 23** Linux kernel structures with no errors, against **5 of 23** for value-invariant signatures, counting freed-but-not-reallocated objects as false positives ([SigGraph, NDSS 2011](https://friends.cs.purdue.edu/pubs/NDSS11.pdf)).

**Independent agreement beats any single test.** DeepMem's weighted voting, with a bonus when independent voters pick the same address, reached **over 99.5% precision and recall** on `_EPROCESS` and `_ETHREAD`. Precision fell on small objects, to 95.44% on a 44-byte structure ([DeepMem](https://www.cs.ucr.edu/~heng/pubs/deepmem_ccs18.pdf)).

**Unsupervised clustering is weak as an instance finder.** Laika, which clusters objects by word type, had **32% false positives and 35% false negatives** when used to find instances ([Dolan-Gavitt et al.](https://www.cs.columbia.edu/~brendan/ccs09_siggen.pdf)). Its own heap measurements still supply useful priors: under 1% of pointers are unaligned, about 80% point to an object head, and only about 45% of heap address space holds live objects ([Laika, OSDI 2008](https://www.usenix.org/legacy/events/osdi08/tech/full_papers/cozzie/cozzie.pdf)).

**Execution-trace systems are out of reach.** REWARDS and Howard recover types from execution traces under instrumentation or emulation, so an external tool cannot use them ([REWARDS](https://www.cs.purdue.edu/homes/xyzhang/Comp/ndss10.pdf); [Howard](https://www.ndss-symposium.org/wp-content/uploads/2017/09/slow.pdf)). Cheat Engine's group-compare "Dissect data/structures" is the practical manual equivalent ([CE wiki](https://wiki.cheatengine.org/index.php?title=Help_File:Dissect_data/structures)).

### A layered validator

The synthesis is a validator that runs from cheap checks to expensive ones and stops at the first failure:

1. Value checks inside the buffer already read.
2. Pointer-range checks against a cached region map.
3. One-hop edges, of which back-pointers are the strongest because random data almost never points back.
4. Engine identity: self-index, Class and Name.
5. Cross-object counts.

Hard rules apply to structural invariants, while weighted scores apply to soft values like health, which legitimately go out of range during death and spawn.

## Self-healing is an ordered ladder with validation on every rung

### Public tools split reliability into three primitives

LiveSplit's autosplitter stack is the most battle-tested public example of external self-healing:

1. **A path rebuilt on every read.** `DeepPointer` rebuilds the address from the module base on every call and treats a zero intermediate pointer as failure ([DeepPointer.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/DeepPointer.cs)).
2. **A watcher that keeps the last good value.** `MemoryWatcher` defaults to keeping the previous value when a read fails, and never reports a change on its first read ([MemoryWatcher.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/MemoryWatcher.cs)).
3. **A lifecycle that re-attaches.** The script runtime attaches to the newest live instance and reruns `init` after an exception or a game restart ([ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs); [AutoSplitters README](https://github.com/LiveSplit/LiveSplit.AutoSplitters/blob/master/README.md)).

### Ordered fallbacks are common; voting is not

Ordered fallbacks are standard:

- asl-help detects the engine version by signature, then falls back to the file version, then to a manual override, and logs a line telling the user how to set that override ([asl-help Unreal.EngineData.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.EngineData.cs)).
- Dumper-7 says to override offsets "only if the generator doesn't find them" ([Dumper-7](https://github.com/Encryqed/Dumper-7)).
- cs2-dumper stamps its output with a build number and timestamp so consumers can key offsets to a build ([cs2-dumper info.json](https://github.com/a2x/cs2-dumper/blob/main/output/info.json)).

True N-of-M voting between strategies appears in no public codebase the research found. The closest practices are hazedumper's independent second derivation and pointer-map intersection. Commercial trainers do not visibly self-heal either: WeMod threads show features breaking after a patch until staff update them ([WeMod community](https://community.wemod.com/t/magicraft-cheats-and-trainer-for-steam/284852?page=3)). Automatic recovery is a differentiator, not an industry baseline.

### The ladder

The design that falls out is a ladder, cheapest and most trusted first:

1. A cached address, revalidated.
2. A build-keyed pinned value.
3. A signature.
4. Reflection by name.
5. A structural heap sweep.

A later rung's result is promoted to the cache only after it passes validation. Re-resolution triggers on attach, on validation failure and on level transition, never on a fixed timer while the cached value still validates.

### Writes, races and Wine

Writes need their own discipline, because `ReadProcessMemory` promises accessibility, not atomicity ([Microsoft ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory)). The safe pattern:

1. Read the whole struct in one call.
2. Re-check identity immediately before writing.
3. Write only the target field.
4. Read the field back.

Wine adds a performance cliff that matters for Proton users. It first tries `process_vm_readv`, but on `ENOSYS` or `EPERM` it permanently falls back to a ptrace path that **suspends a thread for each request** ([Wine server/ptrace.c](https://gitlab.winehq.org/wine/wine/-/blob/master/server/ptrace.c)). Under that fallback, a design built on many tiny reads pays per call.

### Diagnostics

Every mature tool logs which global it resolved and where. asl-help also fails with actionable typed errors, such as "Ensure correct spelling. Names are case sensitive." ([asl-help Make](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.API.Make.cs)). None of the examined tools reports, per target, which rung resolved it.

## GrandeuReforged already stands on the top rung; its bootstrap is the weak joint

### Where the tool stands today

Mapped against the techniques above, the tool is further along than most public tools: it already finds its key objects and offsets by name and verifies them live ([DD1_INTERNALS.md §3a, §8b](../DD1_INTERNALS.md)). The remaining gaps are narrow and specific.

| Technique | What GrandeuReforged does today | Gap that remains |
|---|---|---|
| UE3 GObjects/GNames + name-based offsets | Walks about 250k objects in about 0.3 s. Finds the HeroManager and WorldInfo by name. Resolves game-class offsets from live UProperty data. A live check of 55 offsets all matched. Self-index liveness via `IsLiveObject` | Core reflection offsets are hardcoded. The trust gate checks game fields, not the reader. No ObjectFlags checks |
| AOB signatures | One signature each for GObjects and GNames, rescanned per attach | Single point of failure. First validated hit wins, with no uniqueness or agreement check. GObjects validated by header shape only ([GameReflection.cs](../Models/GameReflection.cs)) |
| Pointer scanning / path ranking | None. Item/Hero Search is first/next value scanning | Search results are session-only |
| vtable / RTTI | RTTI verified absent. Runtime-learned pawn vtable seed, invalidated by PE `TimeDateStamp` | None worth closing; the seed already self-heals structurally |
| Structure dissection / heuristic validation | TArray fingerprinting with ranking and pinning (the `ItemBoxEntries` fingerprint). PRI, WorldInfo back-reference and PawnList loop-closure gates | Already SigGraph-grade |
| Heap / region walking | `VirtualQueryEx` sweep of private RW regions, one read per region | Regions of 50 MB or more are skipped ([MainWindow.xaml.cs](../MainWindow.xaml.cs)) |
| Self-healing architecture | Object list → seed sweep → structural sweep. Stamp-triggered rediscovery. Per-tick revalidation. Identity-gated writes. COPY REPORT | No per-target "resolved by" reporting. No cross-derivation agreement |
| Code caves / debug registers | None, by policy | None needed |

### Seven upgrades, ordered by reliability gained per unit of risk

**First: make the bootstrap redundant and self-checking.** Everything downstream flows from two signatures, so this is where a code-moving patch would hurt most. The tool can add three independent derivations and require that they agree:

- **A per-build pin.** Store the GObjects and GNames header addresses in `overrides.json`, beside the existing `GameTimeDateStamp`. They are static TArrays in a non-ASLR exe, so their addresses are constant per build.
- **A signature-free data-section scan.** Walk the exe image's writable sections for 4-aligned `{Data, Count, Max}` triples. Accept GNames when entries 0, 1 and 2 decode to `None`, `ByteProperty` and `IntProperty`. Accept GObjects when a sample of slots passes the tool's own self-index round trip and `Class(Class(x))` converges on the object named `Class`.
- **Extra signatures.** Add TheFeckless's `A1 ?? ?? ?? ?? …25 00 02 00 00` variant ([TheFeckless GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)). Optionally add the `"Invalid object index %i"` string anchor, after first checking that DunDefGame.exe actually contains that string.

The existing scan should also change in two ways. It should scan the whole module and require exactly one validated hit, or agreement among hits, rather than accepting the first. And it should validate GObjects by self-index sampling, not header shape alone. This is content-validated discovery in the same spirit as the item-box fingerprint, so it fits the project's "do not guess offsets" rule. It is read-only, and it removes the single point of failure that a patch would hit.

**Second: anchor reflection trust on invariants that cannot move independently.** Today trust requires `Controller 0x22C`, `Player 0x3B8` and `ViewportClient 0x194` to resolve to their known values ([DD1_INTERNALS.md §3a](../DD1_INTERNALS.md)). If a future patch moved one of those fields, the gate would distrust reflection in exactly the situation reflection exists for, and the tool would fall back to the stale hardcoded offset.

The fix is to prove the reader against `Core.Object`'s self-described header, where Outer, Name, Class and ObjectArchetype must resolve to 0x28, 0x2C, 0x34 and 0x38. On top of that:

- Keep the three game anchors as a secondary signal. A mismatch there becomes "field moved; use the reflected value and say so in COPY REPORT", not "reflection disabled".
- When the core check fails, calibrate the core offsets by search instead of giving up.

This changes the standing rule "never trust reflection without the per-attach engine-anchor check" ([DECISIONS.md](../DECISIONS.md)), so it needs a DECISIONS entry and the user's sign-off. It is read-only. Its value is insurance rather than a fix for an observed failure: the core layout matched live on the current build.

**Third: add ObjectFlags-based liveness.** The tool already rejects recycled slots through the self-index round trip. A pending-kill object, however, passes that check, because it stays at its slot until purge. The flags offset can be read directly from code: the tool's GObjects match already contains `8B 40 xx`, where `xx` is the displacement the engine uses for its own `and eax, 0x200` class-default test. Then:

1. Confirm the offset live: `RF_ClassDefaultObject` should be set on every `Default__*` object.
2. Gate cached WorldInfo, viewport and HeroManager handles on `RF_PendingKill` and `RF_Unreachable` being clear, after confirming those bit values live on DD1's 2011 build.

This tightens the map-transition window that the WorldInfo staleness fix currently covers by revalidating on every tick. It is read-only and two reads per check.

**Fourth: make search results persistent by reverse-resolving them to name paths.** This is the UE3-native replacement for a pointer scanner. After a first/next scan narrows to an address:

1. Find the owning UObject by binary search over the walk's objects sorted by address, using each class's cached `PropertySize` as the object's extent.
2. Find the UProperty whose `[Offset, Offset + ElementSize×ArrayDim)` range covers the address, walking SuperField and recursing into struct properties.
3. Store a path from a stable named root, for example HeroManager → `LocalLoadedHeroes[k]` → `HeroExperience`, in the asr/asl-help hybrid format. Use a root rather than the leaf's own name, because instance-number suffixes reflect allocation order.

The next session re-resolves the path by name, so it survives restarts and patches without pointer maps. Values inside TArray data blocks that belong to no UObject need a second index of array data pointers; that is the main implementation gap. It is read-only.

**Fifth: close the 50 MB blind spot.** In the fallback structural sweep, read large regions in roughly 1 MB windows that overlap by the 0x32C candidate span, and fall back to page-sized reads on `ERROR_PARTIAL_COPY`. This is low priority, because the object list now finds WorldInfo first. It becomes relevant only if the bootstrap fails, and the first upgrade makes that rarer. It is read-only.

**Sixth: report the resolution tier per target.** Add one line per target to COPY REPORT: which rung resolved it (pinned, signature, data scan, reflection, content discovery, structural sweep), the build stamp, and the count of consecutive failures. Where reflection and content discovery both run at attach, log any disagreement. This is the per-target health view that no examined public tool provides. It extends the project's existing practice of diagnosing remote machines through COPY REPORT.

**Seventh, for Proton users: coalesce reads in the object walk.** About 250k four-byte Class reads per walk is harmless on Windows. Under Wine's ptrace fallback, each read suspends a game thread. Batching neighboring headers into larger reads removes that cliff.

| Priority | Upgrade | Risk class | Effort |
|---|---|---|---|
| 1 | Redundant GObjects/GNames bootstrap with agreement | Read-only | Medium |
| 2 | Self-referential reflection trust and core-offset calibration | Read-only; changes a trust rule | Medium |
| 3 | ObjectFlags liveness gates | Read-only | Low |
| 4 | Persistent search results via reverse name paths | Read-only | Medium–high |
| 5 | Chunked reads for regions of 50 MB or more | Read-only | Low |
| 6 | Per-target resolution-tier reporting | None (diagnostic) | Low |
| 7 | Coalesced object-walk reads | Read-only | Low |

### What not to build

**Static RTTI discovery and constructor-signature vtable extraction are closed.** Both are recorded as do-not-retry ([DECISIONS.md](../DECISIONS.md)).

**CE-style pointer scanning adds little at runtime.** Nearly every value the tool edits is a UObject property that is already reachable by name. Its only niche is native data that reflection does not describe. If such a case appears, a pointer-map harness under `new dumps/` is the right home, not the shipped exe.

**Code caves and hardware breakpoints should not ship.** DD1's anti-cheat has no `.text` integrity check and no real anti-debug ([DD1_INTERNALS.md §1](../DD1_INTERNALS.md)), so detection is not the issue. The risk is to the user's game:

- A wrong instruction boundary or an early-freed cave crashes it.
- A debugger attach without `DebugSetProcessKillOnExit(FALSE)` kills it when the tool exits.
- A hung debug loop freezes it.
- Under Wine, ptrace contention can break memory access entirely.

These techniques also cross the project's line on executing code in the game. The one code patch already identified (the anti-cheat flag NOP) is blocked pending explicit user authorization ([DECISIONS.md](../DECISIONS.md)). A hardware-breakpoint "find what writes" harness is defensible only as a supervised, development-time research aid.

## Conclusion

For an engine-reflective game, "how do I find the address again?" is the wrong question. The thing worth persisting is an identity plus a name path, and pointer scanning mostly answers a question UE3 already answers natively. A tool's reliability therefore depends on the smallest pieces of code it relies on, not the largest: two signatures and about ten core offsets carry every name-based lookup above them. Engineering effort belongs in making that bootstrap redundant, unique, cross-checked and calibratable. A trust gate should test the reader against invariants that can only move together with it, such as the engine's self-description of its own header, rather than against fields that can move independently.

The forensics literature also explains why GrandeuReforged's hardest-won lessons were the right ones. Signatures built only from fields the game cannot run without, trained on the full object lifecycle, are exactly what the project arrived at independently. Two examples are the rule that item gates must tolerate edited items and the rejection of a bare-PRI check that let UTF-16 fragments through. Discovery is the easy half of the problem. Durable reliability comes from validation that treats every cached address as a hypothesis, and from recording each rejected approach so it is not re-walked.
