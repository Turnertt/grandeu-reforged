# Reliability Engineering and Self-Healing Architectures for External Memory Tools

Researched 2026-10-28. Primary sources are source code read directly from GitHub (LiveSplit, LiveSplit.ScriptableAutoSplit, asr, asl-help, bl-sdk/unrealsdk, cs2-dumper, hazedumper, GH-Offset-Dumper, Dumper-7, Wine), plus Microsoft Learn and Raymond Chen's blog. Repo activity at time of reading: LiveSplit pushed 2026-10-21, asr pushed 2026-10-11, asl-help pushed 2026-07-08 (repo now redirects to `ero-qt/asl-help`), cs2-dumper pushed 2026-10-28, Dumper-7 pushed 2026-10-26, hazedumper last pushed 2024-03-28. Community and trainer-vendor sources (WeMod, CE forums) were thin and are flagged as lower authority.

Consumer context the inferences are written for: an out-of-process C# (.NET 8, x86) WPF editor with a 50 ms UI timer, targeting a 32-bit Unreal Engine 3 game from about 2010–2011 that no longer receives patches but varies by install (Steam builds, mods), and that may also run under Wine/Proton.

---

## 1. LiveSplit ASL ecosystem, asl-help (Unreal/Unity/Basic), and the asr runtime: how they resolve and keep values alive

### Takeaway
The LiveSplit stack splits reliability into three primitives. (1) A pointer path that re-resolves from the module base on every read and fails soft (DeepPointer). (2) A watcher that keeps old/current values and decides what to do when a read fails (MemoryWatcher). (3) A process-lifecycle state machine: connect, then `init` (version detection plus signature scans, retried on exception), then `update`, then `exit`. asl-help and asr add engine-reflection resolution on top. They find a handful of globals (GEngine, GWorld, FNamePool/GNames, GObjects) via several candidate signatures, then resolve everything else by *name* through the engine's own class/property metadata, and cache the resulting offsets.

### Cited Findings
**DeepPointer (LiveSplit.Core/ComponentUtil)**
- `DerefOffsets` rebuilds the address from scratch on each call: module base (looked up by name via `ModulesWow64Safe()`) or main module base, plus `_base`, then dereferences each offset except the last. It returns `false` if the named module is not found, if any intermediate `ReadPointer` fails, **or if any intermediate pointer is zero**. The last offset is added without a dereference. — [DeepPointer.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/DeepPointer.cs)
- `Deref<T>(process, default_)` returns a caller-supplied default on failure. `Deref<T>(process, out T)` returns a bool. `DerefType.Auto` picks pointer width from `process.Is64Bit()` (implemented via `IsWow64Process`). — [DeepPointer.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/DeepPointer.cs); [ProcessExtensions.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/ProcessExtensions.cs)
- ASL state-descriptor syntax: `state("PROCESS_NAME", "VERSION_IDENTIFIER")`, with pointer paths declared as `TYPE NAME : "module", OFFSET, OFFSET...;`. Several descriptors can exist per process/version. — [LiveSplit.AutoSplitters README](https://github.com/LiveSplit/LiveSplit.AutoSplitters/blob/master/README.md)

**MemoryWatcher / MemoryWatcherList**
- Properties: `Name`, `Enabled`, `Current`, `Old`, `Changed`, `UpdateInterval` (TimeSpan?), and `FailAction` (`ReadFailAction.DontUpdate` is the default, `SetZeroOrNull` the alternative). — [MemoryWatcher.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/MemoryWatcher.cs)
- `Update()` semantics: it returns early if disabled or if `UpdateInterval` has not elapsed (per-watcher throttling). On a failed read with `DontUpdate`, it keeps the previous `Current` and returns false. **The very first successful update never reports `Changed`**, because the `InitialUpdate` guard suppresses a spurious change from default to the first value. `OnChanged(old, current)` fires only when `!Current.Equals(Old)`. `Reset()` clears `Current`/`Old`/`InitialUpdate`. — [MemoryWatcher.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/MemoryWatcher.cs)
- `MemoryWatcherList.UpdateAll(process)` updates every watcher and raises the list-level `OnWatcherDataChanged`. `ResetAll()` exists for re-attach. — [MemoryWatcher.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/MemoryWatcher.cs)

**SignatureScanner / SigScanTarget**
- `SignatureScanner(proc, addr, size)` reads the whole region **once** with a single `ReadBytes` and caches it in `_memory`. If that read fails, the scan yields nothing; it does not throw. For each match it adds `sig.Offset`, then, if set, calls `target.OnFound(proc, scanner, ptr)` so the script can turn the match into the final address (e.g. read a RIP-relative or absolute operand). `ScanAll` and an `align` parameter exist. A `SigScanTarget` can hold **multiple signatures**, which are tried in order. — [SignatureScanner.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)
- Signature strings are hex bytes with `?`/`??` wildcards, parsed in `AddSignature`. — [SignatureScanner.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)

**Version detection in `init`**
- Setting `version` in `init` activates the matching `state` descriptor. The README example switches on `modules.First().ModuleMemorySize`. If nothing matches, the default descriptor is used (the first one with no version, else the first in the file). — [AutoSplitters README](https://github.com/LiveSplit/LiveSplit.AutoSplitters/blob/master/README.md)
- The runtime logs `"No state descriptor for version '{GameVersion}' (will keep using default one)"` rather than failing. — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)
- asl-help's `Basic` helper adds `GetMemorySize()` plus `GetMD5Hash()`, `GetSHA1Hash()`, `GetSHA256Hash()`, `GetSHA384Hash()` and `GetSHA512Hash()`, each per module. The hashes are computed over the **module file on disk** (`File.OpenRead(module.FilePath)`), not over memory. A missing module logs `Debug.Warn` and returns 0/null. — [Basic.Versioning.cs](https://github.com/just-ero/asl-help/blob/main/src/Basic/Helper/QualityOfLife/Basic.Versioning.cs)

**Lifecycle (ASL runtime)**
- `Update()` is a state machine. If there is no game, `TryConnect`. If `_game.HasExited`, `DoExit` (sets `_game = null`, runs `exit`). If init is not completed, `DoInit`. Otherwise `DoUpdate`. — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)
- `TryConnect` picks `Process.GetProcessesByName(name).OrderByDescending(x => x.StartTime).FirstOrDefault(x => !x.HasExited)`, i.e. the **newest live instance**. — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)
- "`init` is restarted if stopped by an exception". It "can occur more than once … (e.g. when you restart the game)". `exit` runs whenever the attached process exits. Actions run about 60 times/s by default, and `refreshRate` lowers that. If `update` returns false, the timer actions are skipped for that tick. — [AutoSplitters README](https://github.com/LiveSplit/LiveSplit.AutoSplitters/blob/master/README.md)
- The idiom this enables: throwing inside `init` when a signature isn't found makes the runtime retry on the next tick. The code comment on `DoInit` says it runs "each time after connecting to the game (usually just once, unless an error occurs before the method finishes)". — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)

**asl-help (just-ero / ero-qt) Unreal helper**
- The Unreal helper is **not on `main`** as of 2026-10. It lives on the `ue-fixes` branch (`src/Unreal/...`, with per-version struct XMLs `Structs/4_0.xml` … `4_27.xml`). `main` contains `Basic` and `Unity` (Mono V1/V2/V3 + Il2Cpp managers). — [asl-help repo](https://github.com/just-ero/asl-help); [ue-fixes tree](https://github.com/just-ero/asl-help/tree/ue-fixes/src/Unreal)
- **Engine version detection**: a `ScanTarget` holding two signatures (one for `GEngineVersion`, one for `FEngineVersion::Current`, with separate 32-bit and 64-bit variants). Its `OnFound` callback does a second, short-range `ScanRel` to follow to the version struct, and the code reads two `ushort`s (major, minor). **Fallback**: the main module's `FileVersionInfo` (commented "not guaranteed to be exported"). **Manual override**: `vars.Helper.UEVersion = new Version(4, 0)`, allowed only in `startup`; the log tells the user how to do it. — [Unreal.EngineData.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.EngineData.cs); [Unreal.Data.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.Data.cs)
- **Version-to-layout dispatch**: the version picks a manager class (4.0–4.7, 4.8–4.10, 4.11–4.19, 4.20–4.22, 4.23–4.24, 4.25+/5.x). Anything else throws `NotSupportedException`, and **UE3 is not supported**. — [Unreal.Load.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.Load.cs)
- **Globals**: `FNamePool` and `GUObjectArray` come from version-specific scan targets, and a missing `FNamePool` throws `FatalNotFoundException`. `GWorld`, `GEngine` and `GSyncLoadCount` come from `ScanRel` with 32/64-bit signatures, e.g. 32-bit GWorld `A1 ???????? 33 C9 3B C6 0F 44 C1 A3` and 32-bit GEngine `A8 01 75 ?? C7 05`. Each resolved address is logged via `Debug.Info`. `IsLoading` is exposed as `Read<bool>(GSyncLoadCount)`. — [UEMemManager.Setup.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.Setup.cs); [Unreal.Data.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.Data.cs)
- **Name-based path resolution**: `Make<T>("BaseObjectName", "Field", "Field", 0x10, ...)` looks the base object up in a `UObjects` cache by name. A base of `"World"` maps to GWorld and `"Engine"` to GEngine. Each string field is looked up in the current class's `UField` map and its `Offset` taken, then `baseObject = next.Class` to descend. Integers pass through as raw offsets. Misses throw `NotFoundException` with "Ensure correct spelling. Names are case sensitive." Property type is derived from the property class's FName (`IntProperty`, `FloatProperty`, `ObjectProperty`, …). — [UEMemManager.API.Make.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.API.Make.cs); [UEMemManager.UFields.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.UFields.cs)

**asr (Rust/WASM auto-splitting runtime) Unreal module**
- `Module::attach(process, version, main_module_address)` reads the PE machine type to get pointer size and `SizeOfImage` for the scan range. It then tries **a list of candidate signatures per global in order (`find_map`, first hit wins)** and resolves RIP-relative operands as `addr + 4 + read::<i32>(addr)` (GEngine uses +8). GEngine has two candidates, GWorld two, FNamePool three. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- Property lookup walks `UStruct.property_link → property_link_next`. If a class adds no fields (null `property_link`), it climbs `super_field`. Comments credit [bl-sdk/unrealsdk ustruct.h](https://github.com/bl-sdk/unrealsdk) and [apple1417's object-structure gist](https://gist.github.com/apple1417/b23f91f7a9e3b834d6d052d35a0010ff). — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- `UnrealPointer::new(base, &["Field", "0x1A8", ...])` mixes names and literal offsets. It **caches resolved offsets incrementally** (`resolved_offsets` counter), so a path whose tail objects are not loaded yet keeps the prefix it already solved and resumes on later ticks. Once fully resolved, later derefs are plain pointer-path reads. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- Engine-layout offsets live in a table keyed by `(version, pointer_size)`. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- Runtime control flow: `retry(|| Process::attach("MyGame.exe")).await` retries each tick until success. `Process::wait_attach` is built on it, and `process.until_closes(async {...})` scopes logic to a process lifetime. Reads return `Result`. — [asr README](https://github.com/LiveSplit/asr)
- `Watcher<T>::update(Option<T>)` updates the old/current pair only on `Some`, so failed reads don't clobber the pair. `Pair::check(f)` detects "true now, false before" edges. — [asr watcher.rs](https://github.com/LiveSplit/asr/blob/master/src/watcher.rs)

### Inferences
- Model for a C# tool: a `Target` object holds (a) a resolver chain, (b) a cached resolved address or offsets with a validation predicate, and (c) a watcher with `FailAction = DontUpdate`. That is almost exactly LiveSplit's DeepPointer + MemoryWatcher split, plus asr's incremental offset cache.
- asr's design, where a **zero intermediate pointer is a failure** and the path re-resolves from the module base every read, is the cheap baseline that self-heals across level loads. That's because only the static root is cached, not the leaf.
- asl-help's "signature first, then file-version fallback, then manual override in config, with a log line telling the user how" is a directly portable three-tier version detector.
- Neither asl-help nor asr supports UE3 directly. For UE3 32-bit, the closest public reference for GObjects/GNames signatures and layout is bl-sdk/unrealsdk's BL2/TPS (UE3) code (see Q3/Q4).

### Gaps
- asl-help's `ue-fixes` branch may be unmerged or experimental. I did not determine which asl-help release binary (`lib/LiveSplit.ASLHelper.bin`) ships the Unreal helper.
- I did not read asr's `Process::is_open` / `until_closes` implementation, only the README description.

---

## 2. How trainers and Cheat Engine table authors survive game patches

### Takeaway
The community norm is code-signature (AOB) relocation via `aobscanmodule` + `registersymbol`, with an `assert` on the original bytes so a mismatched build fails to enable instead of crashing. Robustness comes from *choosing* signatures that avoid embedded absolute addresses and volatile struct displacements. Commercial trainers (WeMod) visibly fall back to "feature broken until we update", so no robust public evidence of automatic self-healing exists there.

### Cited Findings
- CE syntax: `aobScanModule(SymbolName, ModuleName, AOBString)` scans only the named module's memory. Wildcards are `x`, `?` or `*` and can be nibble-level within a byte (`00xx8D6424E0`). `$process` means the main module. — [CE wiki: aobScanModule](https://wiki.cheatengine.org/index.php?title=Auto_Assembler:aobScanModule)
- Cheat Engine 7.2 added `unregistersymbol(*)` (unregisters every symbol a script registered) and a syntax check for AOB scan scripts. — [Cheat Engine 7.2 release post (Patreon, via search summary)](https://www.patreon.com/cheatengine/posts/cheat-engine-7-2-35792319)
- Why patches break hacks: injection at a fixed module offset is guarded by an `assert` of expected bytes. Patches shift code, so the target moves. The author's advice is "Keep the assert. Removing it may let injection succeed but usually causes a crash." — [badecho: Fixing hacks after a patch (2021-10-05)](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)
- Signature selection rules: avoid instructions referencing static addresses ("Don't hook into places that require us to replace original code which is referencing a static address"). Prefer small relative jumps ("Small offsets are less likely to change"). Prefer primary source-of-truth fields. Record the AOB **and its distance from the injection site** per hack. — [badecho](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)
- Concrete breakages where the struct displacement inside the instruction changed: Cyberpunk 2077 `movzx ecx,word ptr [rsi+340]` became `[rsi+350]`, and `mov [rsi+60],bl` became `[rsi+63]`. Nioh 2 `mov rax,[nioh2.exe+1E65038]` was rejected as a signature because it embeds a static address. — [badecho](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)
- hazedumper deliberately offers a **second, independent derivation** of LocalPlayer (`dwClientState` + `dwClientState_GetLocalPlayer` + entity list) because "the signature for the LocalPlayer was broken a few times and/or pointed to something wrong". — [hazedumper README](https://github.com/frk1/hazedumper)
- WeMod: community threads show a patch making WeMod stop recognizing the game or features stop working. Staff respond that it has been "reported to the team", and users roll back to an older trainer version. This is anecdotal, community-forum evidence (2024). — [WeMod community: Magicraft thread](https://community.wemod.com/t/magicraft-cheats-and-trainer-for-steam/284852?page=3)
- Pointer-path stability practice: save a pointer scan, restart the target, rescan, and intersect the saved results so only paths present in every session survive. This is from a secondary source documenting a CE-like library, not the CE docs. — [PyMemoryEditor pointer-scan guide](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)

### Inferences
- For a frozen game with install variance, the CE pattern maps to: each feature declares its signature(s), expected original bytes, and a capture offset. If the scan or assert fails, **the feature is disabled with a reason** rather than guessed. That is the "graceful disable" behavior CE tables get for free from a failed `[ENABLE]`.
- Struct-field displacements inside instructions (`[esi+340]`) are the most fragile bytes. Wildcard them, and if the displacement *is* the thing you need, capture it from the instruction at runtime rather than hardcoding it.
- Multiple independent derivations of the same root (hazedumper's LocalPlayer alternative) are the community version of "redundant resolvers".

### Gaps
- I found no authoritative public documentation of how WeMod/Wand or FLiNG detect versions or disable individual features. Only forum anecdotes are available.
- I could not verify the CE "freeze interval" default (commonly believed to be about 100 ms) from an authoritative source.
- The CE wiki page does not document behavior on not-found or multiple matches (whether a script fails to enable, or the first match is taken).

---

## 3. Offset auto-update pipelines: dumpers, published offset databases, and consumption

### Takeaway
Mature ecosystems separate a **producer** from **consumers**. The producer is a dumper that runs per game build and emits offsets in several languages plus a build stamp. Consumers either compile the offsets in or load a JSON file. The producer itself is signature-driven (config of name/module/pattern/operand-offset/relative), so the signature list is the real long-lived artifact and the offsets are regenerated output.

### Cited Findings
- **cs2-dumper**: an external dumper (via memflow, so it also works through DMA/KVM connectors). Default output formats are `cs, hpp, json, rs, zig`. You run it with the game at the main menu. — [cs2-dumper README](https://github.com/a2x/cs2-dumper)
- cs2-dumper commits regenerated output per game build. Commit messages read `Game update (14192)` (2026-10-23), and `output/info.json` contains `{"build_number": 14192, "timestamp": "2026-10-23T17:14:58Z"}`. That build-stamp file is how consumers key offsets to a game build. — [cs2-dumper commits](https://github.com/a2x/cs2-dumper/commits/main); [output/info.json](https://github.com/a2x/cs2-dumper/blob/main/output/info.json)
- **hazedumper** (CS:GO, last pushed 2024-03): `config.json` declares each signature as `{name, module, pattern ("A1 ? ? ? ? 33 D2 ..."), offsets:[operand index], extra, relative}`. Its README says "we can't see 24/7 if there was an update" and that "the repository always refers to the latest version of the steam store". — [hazedumper README](https://github.com/frk1/hazedumper); [config.json](https://github.com/frk1/hazedumper/blob/master/config.json)
- **GH-Offset-Dumper** (Guided Hacking): game-agnostic. A per-game JSON config is dropped onto the exe and it emits a `.hpp` header, a Cheat Engine table and a ReClass.NET project. It works **at runtime or from disk** (`fileonly: true`, `exefile`, `additionalModules`). It supports relative-branch signatures via `rva`, `opLoc` and `opLength`. — [GH-Offset-Dumper README](https://github.com/guidedhacking/GH-Offset-Dumper)
- **Dumper-7**: an injected DLL that generates a full SDK for UE4 and UE5 only. It auto-finds GObjects/FName/ProcessEvent, with documented manual overrides "only if the generator doesn't find them". Overrides include GObjects layout tables (`FFixedUObjectArrayLayout` for 4.11–4.20, `FChunkedFixedUObjectArrayLayout` for 4.21+) and a per-game `Dumper-7.ini` next to the game exe. — [Dumper-7 README](https://github.com/Encryqed/Dumper-7)
- **Il2CppDumper**: a static (offline) dumper taking the il2cpp binary plus `global-metadata.dat`. It supports Unity 5.3–2022.2 and emits dummy DLLs, struct headers and IDA/Ghidra/Binary Ninja scripts. — [Il2CppDumper README](https://github.com/Perfare/Il2CppDumper)
- **UE3 precedent (bl-sdk/unrealsdk, Borderlands 2, 32-bit UE3)**: GObjects is found with `"8B 0D {????????} 8B 04 ?? 8B 40 ?? 25 00020000"` (`mov ecx,[GObjects]; mov eax,[ecx+esi*4]; mov eax,[eax+08]; and eax,200`). GNames uses `"A3 {????????} 8B 45 ?? 89 03"`. Braces mark the captured operand. Results are logged (`LOG(MISC, "GObjects: {:p}")`), and `sigscan_nullable()` permits a not-found result. — [unrealsdk bl2/globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)
- unrealsdk's multi-sigscan picks the **rarest byte common to all patterns** as an anchor, uses a fast `std::find` on it, and only then does the slow masked compare. It also splits memory into chunks scanned on parallel threads, with overscan at boundaries, leaving one core for the game. — [unrealsdk multi_sigscan.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/multi_sigscan.h)
- UE layout reference (apple1417): UE3 GNames is a plain `TArray<FNameEntry*>` indexed directly. UE4 uses chunked arrays (0x4000 per chunk), and UE4.23+ uses FNamePool. Name indexes 0/1/2 should be `None`/`ByteProperty`/`IntProperty`. Full path = walk `Outer`. Out of process, **cache name indexes** (stable once assigned, but runtime-added names can shift between sessions). Validate offsets by decoding the names they point at. — [apple1417 gist](https://gist.github.com/apple1417/b23f91f7a9e3b834d6d052d35a0010ff)

### Inferences
- For a game that will never update but varies by install, the transferable pattern is a **versioned offsets data file keyed by build identity**. The key can be module `SizeOfImage`, PE `TimeDateStamp`, or a file hash (asl-help hashes the file on disk). Each entry holds statically verified roots and struct offsets, and a signature list serves as the fallback for unknown builds. cs2-dumper's `info.json` (build number + timestamp) is the model for the key/header.
- Shipping signatures plus a GH-Offset-Dumper-style JSON (`pattern`, `opLoc`, `relative`) lets a new build be supported by editing data, not code. Running the same resolver offline against the exe on disk (GH-Offset-Dumper `fileonly`) gives a pre-flight check without the game running.
- The UE3 GObjects/GNames signatures above are from Borderlands 2, not the target game. Their *shape* (instruction context around `[GObjects + idx*4]` and the `and eax, 200` flag test) is what transfers; the bytes need re-verification.

### Gaps
- I did not find a published, maintained offset database for any UE3-era game analogous to cs2-dumper's, nor documentation of a dumper that keys output by PE timestamp specifically (cs2-dumper uses Valve's build number).
- I did not verify UE4SS's reflection-based resolution internals (its AOB "signature" Lua override system); I didn't fetch them for lack of time.

---

## 4. Layered resolution strategy design: ordering, re-running, and cross-checking

### Takeaway
Real projects implement *ordered* fallbacks: several signature candidates (first hit wins), then a file-version fallback, then a user override. They also implement *incremental caching* of resolved name-to-offset paths. True multi-strategy **voting/consensus** is rare in public code. The closest practices are hazedumper's alternative derivation, pointer-scan intersection across restarts, and decode-and-compare validation of resolved offsets (checking that names resolve).

### Cited Findings
- Ordered candidate signatures with first-success semantics: asr GEngine/GWorld/FNamePool each use `[(Signature, offset), ...].iter().find_map(...)`. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- Multiple signatures in one target plus an `OnFound` post-processor: LiveSplit `SigScanTarget` / `SignatureScanner`, and asl-help `ScanTarget` with `OnFound = EngineTrg_OnFound_32`, which itself tries an absolute-form sub-scan and then a relative-form sub-scan. — [SignatureScanner.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs); [Unreal.Data.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.Data.cs)
- Tiered version detection: engine-version signature, then `FileVersionInfo`, then a manual override in `startup`. — [Unreal.EngineData.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.EngineData.cs)
- Hybrid literal/name paths: asr `UnrealPointer` accepts `"0x1A8"` or `"FieldName"` per hop, and asl-help `Make` accepts `int` or `string` per hop. Literal offsets skip reflection, and names fall back to it. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs); [UEMemManager.API.Make.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.API.Make.cs)
- Overrides only when auto-discovery fails: Dumper-7 says "Only override any offsets if the generator doesn't find them, or if they are incorrect". — [Dumper-7 README](https://github.com/Encryqed/Dumper-7)
- Independent derivation as a cross-check: hazedumper's LocalPlayer via ClientState rather than its own signature. — [hazedumper README](https://github.com/frk1/hazedumper)
- Cross-session intersection of pointer paths (keep only paths that survive restarts). — [PyMemoryEditor pointer-scan guide](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- Brute scan cost warning: scanning GObjects for a class can mean "tens of millions of string comparisons", so cache name indexes and compare integers, not strings. — [apple1417 gist](https://gist.github.com/apple1417/b23f91f7a9e3b834d6d052d35a0010ff)

### Inferences
- A recommended order for a frozen 32-bit UE3 game. Each step is cheaper and more trustworthy than the next:
  1. **Cached address, revalidated** with a predicate (vtable in module `.rdata`, or class FName equals expected, or a sane value range).
  2. **Known pointer path from the build-keyed data file**, validated the same way.
  3. **Signature scan** of the main module (read the image once into a buffer, as LiveSplit does), capturing operand addresses.
  4. **Engine reflection by name**: walk GObjects and match by class/name index, then resolve fields via `property_link`. This yields both the object and the field offset.
  5. **Brute heap scan plus structural validation** (vtable, name index, plausible fields). Run it only on demand or with backoff.
- Promote a later-tier result into the cache only if it passes validation. When two tiers both succeed, compare them: if path and signature disagree, prefer the one that passes the stronger structural check and log the disagreement. This is a lightweight "consensus" that costs one extra resolve at attach time only.
- Re-run triggers: on attach (always), on validation failure of the cached result, and on a level-transition signal (see Q5). Never re-run on a fixed timer while the cached value still validates.

### Gaps
- I found **no public codebase** that implements explicit multi-strategy voting (N-of-M agreement) for address resolution. The consensus pattern above is inference, not observed practice.
- I found no source documenting exponential backoff for expensive scans in these tools. asr's `retry` retries every tick, and LiveSplit's `UpdateInterval` is a fixed per-watcher throttle.

---

## 5. Validation and invalidation: sanity checks, level transitions, object respawn, event-driven re-resolution, background scanning

### Takeaway
The proven cheap checks are these: a non-null intermediate pointer on every hop (DeepPointer), a full-length read (`read == count`), and the UE name system as an identity oracle (an object's class/name index must decode to the expected FName). Engines also expose a loading signal (UE `GSyncLoadCount`) that asl-help surfaces as `IsLoading`, which is a natural trigger for re-resolution. Expensive scans should run once per attach in parallel/background, never on the UI tick.

### Cited Findings
- Hop validation: DeepPointer treats a zero intermediate pointer as failure. — [DeepPointer.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/DeepPointer.cs)
- Full-read validation: `ReadBytes` returns false unless `ReadProcessMemory` succeeds **and** `read == count`. `WriteBytes` likewise checks `written == count`. — [ProcessExtensions.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/ProcessExtensions.cs)
- Loading/transition signal: asl-help `public bool IsLoading => Read<bool>(Manager.GSyncLoadCount)`, with GSyncLoadCount found by signature. — [Unreal.Data.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.Data.cs); [UEMemManager.Setup.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.Setup.cs)
- GWorld is "a pointer to the currently loaded UWorld object", while GEngine is "a static object that persists throughout the process' lifetime". So GEngine-rooted paths survive level changes and GWorld-rooted ones are re-read each time. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)
- Name-based identity checks: UE name indexes 0/1/2 are `None`/`ByteProperty`/`IntProperty` (a GNames sanity check). An FName in memory looks like a small index followed by 0. Validate offsets by decoding names and comparing with dumper output. — [apple1417 gist](https://gist.github.com/apple1417/b23f91f7a9e3b834d6d052d35a0010ff)
- UE3 object-flag test seen in the GObjects signature context: `mov eax,[eax+08]; and eax,00000200`, i.e. code reads a flags field at +0x08 of each object. — [unrealsdk bl2/globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)
- Change detection without false positives: MemoryWatcher's first read never reports `Changed`. asr `Watcher::update(None)` leaves the pair untouched. — [MemoryWatcher.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/MemoryWatcher.cs); [asr watcher.rs](https://github.com/LiveSplit/asr/blob/master/src/watcher.rs)
- Module-list caching with invalidation: `ModulesWow64Safe()` caches module arrays in a dictionary and clears it when it exceeds 100 entries. — [ProcessExtensions.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/ProcessExtensions.cs)
- Scan cost control: unrealsdk parallelizes signature scanning across threads with a rare-byte anchor. — [multi_sigscan.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/multi_sigscan.h)
- Incremental, resumable resolution: asr's `UnrealPointer` cache keeps the solved prefix when deeper objects aren't loaded yet. — [asr unreal/mod.rs](https://github.com/LiveSplit/asr/blob/master/src/game_engine/unreal/mod.rs)

### Inferences
- An identity tuple for a cached UObject on UE3: `(address, vtable, class pointer, FName index, object Index in GObjects)`. Revalidate with one small struct read per tick and treat any mismatch as "object unloaded or respawned", which triggers re-resolution from the stable root. A vtable check alone can pass on a freed-then-reused allocation of the same class; the name index plus GObjects index make reuse detectable.
- Event-driven re-resolution: watch GWorld (pointer value) and a loading flag/counter with a MemoryWatcher-style pair. When either changes, invalidate everything rooted in GWorld. This replaces periodic rescans.
- For WPF: run attach-time scans and reflection walks on a background `Task`, publish an immutable "resolved snapshot" (addresses + offsets + strategy used), and let the 50 ms `DispatcherTimer` only read. The scan should read the module image into a buffer in one `ReadProcessMemory`, as LiveSplit's `SignatureScanner` does, rather than issuing per-byte reads.
- Use a per-target throttle (MemoryWatcher's `UpdateInterval` idea) so slow-changing values (inventory) are not re-read every 50 ms while fast ones (HP) are.

### Gaps
- I found no source specifically documenting UE3 `UObject` field offsets (Index, Outer, Name, Class) for 32-bit builds of the target era, other than the flags read at +0x08 visible in the BL2 signature comment. The apple1417 gist gives the structure conceptually, not UE3 32-bit offsets.
- I found no documented use of object-identity "generation counters" in these tools. UE's GObjects serial numbers (a UE4 concept) were not researched.

---

## 6. Race conditions and safe writes from an external process

### Takeaway
`ReadProcessMemory` gives no atomicity or consistency guarantee across a multi-field struct. It only guarantees the whole range is accessible or the call fails. Safe external writes therefore rely on: (a) re-validating identity immediately before writing, (b) verifying the write by reading it back, and (c) preferring "fix the writer" (code patch, applied with `VirtualProtectEx` + `FlushInstructionCache`) over freeze loops for values the game rewrites every frame. Suspending the game's threads around writes is a debugger technique: Microsoft warns that suspension is asynchronous and deadlock-prone, so it should be a last resort.

### Cited Findings
- `ReadProcessMemory`: "Before any data transfer occurs, the system verifies that all data in the base address and memory of the specified size is accessible … if it is not accessible the function fails." It requires `PROCESS_VM_READ`. The documentation makes **no statement about atomicity or about the target modifying memory during the copy**. — [ReadProcessMemory (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory)
- `WriteProcessMemory` requires `PROCESS_VM_WRITE` and `PROCESS_VM_OPERATION` (VM_OPERATION also covers `VirtualProtectEx`). — [Process Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)
- Code modification: "Applications should call FlushInstructionCache if they generate or modify code in memory. The CPU cannot detect the change, and may execute the old code it cached." — [FlushInstructionCache](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-flushinstructioncache)
- LiveSplit's own code-patch helper (`WriteJumpOrCall`) does `VirtualProtect(PAGE_EXECUTE_READWRITE)`, then `WriteBytes`, then restores the old protection, checking `written == length`. That function does not call `FlushInstructionCache`. — [ProcessExtensions.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/ProcessExtensions.cs)
- Thread suspension risks (Raymond Chen, 2025-04-11): a suspended thread "may hold a resource" the resumer needs, causing deadlock. "The thread does not suspend immediately, but rather waits for the kernel to finish its work" (suspension is delivered on return to user mode). If you must suspend, do it from **another process** and avoid waiting on cross-process resources (mutexes, semaphores, file locks) the suspended thread may hold. `SuspendThread` exists "primarily for debuggers to use". — [The Old New Thing: The case of the UI thread that hung in a kernel call](https://devblogs.microsoft.com/oldnewthing/20250411-00/?p=111066)
- Suspending a process requires the `PROCESS_SUSPEND_RESUME` (0x0800) right. — [Process Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)
- The CE convention of asserting expected original bytes before patching (a TOCTOU guard for code patches) is "Keep the assert … removing it … usually causes a crash". — [badecho](https://badecho.com/index.php/2021/10/05/fixing-hacks-after-patch/)
- Under Wine, the ptrace fallback path for both read and write **suspends a thread for each request** (`suspend_for_ptrace`) and then uses `/proc/<pid>/mem` `pread`/`pwrite`, or `PTRACE_PEEKDATA` for tiny reads. The fast path (`process_vm_readv`/`process_vm_writev`) does not suspend. — [Wine server/ptrace.c](https://gitlab.winehq.org/wine/wine/-/blob/master/server/ptrace.c)

### Inferences
- **Torn reads**: read the whole struct in one `ReadProcessMemory` call (one snapshot, fewer tears than per-field reads). For values that must be consistent (e.g. a pointer plus count), use double-read-and-compare: read, read again, accept only if equal, otherwise retry once next tick. This is the seqlock idea, applied externally.
- **TOCTOU between validate and write**: shrink the window by re-reading the identity tuple (vtable/name index) in the same tick, immediately before `WriteProcessMemory`. Write only the specific field (4–8 bytes), never the whole struct, so you don't overwrite fields the game changed since your read. Then read back to verify. A failed verify means "game overwrote it" or "object gone", and the cause is diagnosable from the identity re-read.
- **Values the game rewrites every frame**: a freeze loop at 50 ms races the game's tick and produces visible flicker and inconsistent gameplay. The durable fix is patching the writer instruction (NOP or redirect) with byte-assert, then `VirtualProtectEx`, write, restore protection, `FlushInstructionCache`. On disable, restore the original bytes, which must be saved at enable time.
- **NtSuspendProcess**: tolerable from an external process for a single brief write if (a) the tool waits on nothing the game might hold and (b) resume is in a `finally`. Under Wine, each RPM/WPM may already suspend a thread on the fallback path, so stacking explicit suspension adds latency and risk for little gain.

### Gaps
- I found no Microsoft documentation for `NtSuspendProcess` itself (undocumented ntdll API). Its risks are inferred from the SuspendThread guidance.
- I found no authoritative source quantifying torn-read frequency or documenting double-read patterns in LiveSplit/asr. The recommendation is inference.

---

## 7. Handle/process lifecycle robustness: exit/restart, re-attach, minimal rights, WOW64, Wine/Proton

### Takeaway
Open the game with the minimal rights mask (`PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`). Detect exit by waiting on the handle (SYNCHRONIZE) or checking `HasExited`. On exit, drop every cached address, reset watchers, and re-run init/version detection on the newest live instance. A 32-bit tool reading a 32-bit game is the simplest WOW64 case (module enumeration filters are ignored for WOW64 callers). Under Wine the read path changes materially, and the supported launch pattern is running the tool inside the same prefix.

### Cited Findings
- Rights (Microsoft Learn): `PROCESS_VM_READ` (0x0010) is needed for ReadProcessMemory. `PROCESS_VM_WRITE` (0x0020) is needed for WriteProcessMemory. `PROCESS_VM_OPERATION` (0x0008) is needed for VirtualProtectEx/WriteProcessMemory. `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000) covers `GetExitCodeProcess` and `QueryFullProcessImageName`. `SYNCHRONIZE` is "Required to wait for the process to terminate using the wait functions". `PROCESS_ALL_ACCESS` changed size across Windows versions, and the advice is to "specify the minimum set of access rights required". Protected processes deny VM_READ/VM_WRITE/VM_OPERATION and QUERY_INFORMATION but allow QUERY_LIMITED_INFORMATION. — [Process Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)
- `EnumProcessModulesEx` "may fail or return incorrect information" if the module list is "corrupted or not yet initialized, or if the module list changes during the function call" (e.g. right after launch or during DLL loads). "If the function is called by a 32-bit application running under WOW64, the dwFilterFlag option is ignored and the function provides the same results as EnumProcessModules." — [EnumProcessModulesEx](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocessmodulesex)
- LiveSplit uses `EnumProcessModulesEx(..., LIST_MODULES_ALL)` and throws `Win32Exception` on failure, so module lookup failure during startup surfaces as an exception that the ASL runtime catches and retries. — [ProcessExtensions.cs](https://github.com/LiveSplit/LiveSplit/blob/master/src/LiveSplit.Core/ComponentUtil/ProcessExtensions.cs); [AutoSplitters README](https://github.com/LiveSplit/LiveSplit.AutoSplitters/blob/master/README.md)
- Exit/restart handling: ASL checks `_game.HasExited` every tick, runs `exit`, nulls the process, and re-connects to the newest non-exited instance, re-running `init`. — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)
- asr: `Process::wait_attach` / `retry` / `until_closes` scope all logic to one process lifetime. — [asr README](https://github.com/LiveSplit/asr)
- **Wine implementation (current master)**: `read_process_memory` first tries `process_vm_readv`. On `ENOSYS`/`EPERM` it sets a **static `not_supported` flag** and permanently falls back to the ptrace path, which suspends a thread and `pread`s `/proc/<pid>/mem` for sizes above 3 longs, otherwise `PTRACE_PEEKDATA`. A short read returns `STATUS_PARTIAL_COPY`. Reads are denied (`STATUS_ACCESS_DENIED`) until the target's process init is done. Writes mirror this with `process_vm_writev` / `pwrite`. — [Wine server/ptrace.c](https://gitlab.winehq.org/wine/wine/-/blob/master/server/ptrace.c)
- Historical Wine performance: a 2010 bug traced game lag to RPM going through wineserver (a temporary buffer plus a thread pause per request). — [wine-bugs 2010-07](https://www.winehq.org/pipermail/wine-bugs/2010-July/241478.html)
- Wine: RPM fails when the target is already under gdb, because wineserver's ptrace-based suspend fails (bug 41221). — [wine-bugs 2019-05](https://www.winehq.org/pipermail/wine-bugs/2019-May/515418.html)
- Proton practice for LiveSplit: set the game's Steam launch option `PROTON_REMOTE_DEBUG_CMD` to the tool's exe so it runs in the game's prefix and can read its memory. Native Linux LiveSplit One doesn't run `.asl`. LiveSplit ≥1.8.30 needs .NET Framework 4.8.1, which (as of June 2025) winetricks cannot install. — [speedrun.com Linux autosplitting guide](https://www.speedrun.com/coe33/guides/o2ir7); [speedrun.com Diablo Linux guide](https://www.speedrun.com/diablo/guides/muv82)

### Inferences
- Lifecycle state machine for the C# tool: `Detached → Attaching (open handle, wait until module list stable, detect build) → Resolving (background) → Live → Lost (any read returns failure and the wait handle is signaled, or GetExitCodeProcess != STILL_ACTIVE) → Detached`. In Lost, dispose the handle and reset all watchers and caches. Never reuse addresses across PIDs.
- Do not treat a single failed read as process exit. Under Wine, an early `STATUS_ACCESS_DENIED` before process init completes, or a transient partial copy, is expected. Confirm exit via the process handle (`WaitForSingleObject(h, 0)` with SYNCHRONIZE, or `GetExitCodeProcess`).
- For a .NET 8 tool under Proton, the self-contained single-file build avoids the .NET Framework 4.8.1/winetricks problem that blocks LiveSplit. That's an inference from the LiveSplit limitation, not tested. Launch it inside the game's prefix via `PROTON_REMOTE_DEBUG_CMD`.
- Under Wine, prefer fewer, larger reads (snapshot a struct once). If the process falls back to the ptrace path (e.g. Yama `ptrace_scope` or permissions cause `EPERM` on `process_vm_readv`), every RPM pauses a game thread.

### Gaps
- I found no authoritative source stating how Linux Yama `ptrace_scope` interacts with Wine's `process_vm_readv` path for a tool and game in the same prefix. The EPERM fallback behavior is from code; the trigger conditions are not documented.
- I did not verify whether `PROCESS_QUERY_LIMITED_INFORMATION` alone suffices for `EnumProcessModulesEx` (the fetched doc page does not state required rights). Conventional usage pairs it with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`.

---

## 8. Diagnostics: logging which strategy resolved each target, health panels, breakage telemetry

### Takeaway
Every mature tool examined logs each resolved global with its address and which version/descriptor was chosen. It fails with actionable messages ("not found, set X manually in startup", "check spelling, names are case sensitive"). The offset producers stamp outputs with the game build. None ships a formal health panel or telemetry in the code examined, so per-target resolution status is the gap to fill.

### Cited Findings
- asl-help logs `"Retrieving Unreal Engine version..."`, then `"=> Unreal {Major}.{Minor}."` and a hint: "Doesn't look right? You can set the helper's `UEVersion` manually in 'startup {}'". It also logs `FNamePool: 0x…`, `GWorld: 0x…`, `GEngine: 0x…`, `GSyncLoadCount: 0x…`. — [Unreal.EngineData.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/Helper/Unreal.EngineData.cs); [UEMemManager.Setup.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.Setup.cs)
- asl-help uses typed failures: `FatalNotFoundException("FNamePool could not be found.")`, `NotFoundException("UObject '…' could not be found. Ensure correct spelling. Names are case sensitive.")`, `InvalidAddressException("Base address was 0.")`. It also ships `FileLogger` / `DebugLogger` and a `TextComponentManager` for on-screen text. — [UEMemManager.API.Make.cs](https://github.com/just-ero/asl-help/blob/ue-fixes/src/Unreal/UE/Managers/Base/UEMemManager.API.Make.cs); [asl-help tree](https://github.com/just-ero/asl-help/tree/main/src/Basic/IO)
- The ASL runtime logs connect and version decisions: `"Connected to game: {0} (using default state descriptor)"`, `"Switched to state descriptor for version '{GameVersion}'"`, `"No state descriptor for version … (will keep using default one)"`, `"Init completed, running main methods"`. — [ASLScript.cs](https://github.com/LiveSplit/LiveSplit.ScriptableAutoSplit/blob/master/src/LiveSplit.ScriptableAutoSplit/ASL/ASLScript.cs)
- unrealsdk logs each found global (`LOG(MISC, "GObjects: {:p}")`, `"GNames: {:p}"`) and uses nullable sigscans so a miss is logged, not fatal at scan time. — [unrealsdk bl2/globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)
- Build stamping of offset outputs: cs2-dumper `output/info.json` (`build_number`, `timestamp`) and `Game update (N)` commit messages. — [cs2-dumper info.json](https://github.com/a2x/cs2-dumper/blob/main/output/info.json)
- GH-Offset-Dumper console output reports each pattern found and the resolved relative-branch target (`[+] Found pattern WinMain at …`, `[?] Processing Relative Branch`, `[+] Resolved call location: …`). — [GH-Offset-Dumper README](https://github.com/guidedhacking/GH-Offset-Dumper)

### Inferences
- A health panel for the C# tool should show one row per target: name, status (OK / degraded / disabled), **which tier resolved it** (cache, path, signature, reflection, heap scan), resolved address, last validation time, consecutive-failure count, and the build key (module size, PE timestamp, hash). This is the per-target analogue of asl-help's global log lines.
- Persist a session log of "build key → which tier succeeded per target". If a known build suddenly needs tier 3+ for a target that used to resolve at tier 2, that is the signal a mod or install variant changed layout. This is local telemetry, with no network needed.
- Make failures actionable in the asl-help style: name the target, the strategies tried, and the override knob (config key), rather than failing silently.

### Gaps
- I found no public source showing a trainer or autosplitter with an in-app health/self-test panel or remote breakage telemetry. WeMod's internal practices are not public. The panel design above is inference.
