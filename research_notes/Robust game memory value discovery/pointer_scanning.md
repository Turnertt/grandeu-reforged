# Pointer Scanning, Pointer Maps, and Multi-Level Pointer Path Stability

Scope: how mature tools find a stable path (static base + chain of offsets) to a heap value whose address changes between sessions, how candidates are filtered and ranked, how an external C# (.NET 8, x86) ReadProcessMemory-based editor could implement this itself, and the main failure modes. The target is a 32-bit UE3 Windows game from about 2010-2011.

Notation used below: a path is written `[module+rva] -> +o1 -> +o2 -> ... -> +oN`. Each `->` dereferences a 4-byte pointer, then adds the next offset. The last offset is added without a dereference.

---

## 1. How Cheat Engine's pointer scanner works internally (pointer map, reverse search, parameters, rescan, multi-map compare, file formats)

### Takeaway
Cheat Engine (CE) first takes a snapshot of every pointer-sized value in readable memory that points into valid memory. It stores this "pointer map" as a reverse index from pointer value to the addresses that hold it: a 16-way nibble trie whose leaves are linked in value order. It then runs a depth-first reverse search from the target address. At each level it asks "which slots hold a value in `[target - maxOffset, target]`?", stops at a static (module-relative) slot or at max level, and validates each candidate against one or more pointer maps from other game sessions. Cross-session comparison is the step that removes most of the false positives.

### Cited Findings

**Pointer map construction (`pointervaluelist.pas`, `TReversePointerListHandler`)**
- Regions are enumerated with `VirtualQueryEx` from `start` to `stop`. A region is kept only if it is committed and not `PAGE_GUARD`/`PAGE_NOACCESS`. Private, image and mapped regions are filtered separately by `scan_mem_*` flags. System modules are skipped unless `includeSystemModules` is set. Regions are flagged invalid for `PAGE_NOCACHE`/`PAGE_WRITECOMBINE`, and for read-only or executable pages when `noreadonly` is set. An option (`scanpagedmemoryonly`) uses `QueryWorkingSet` to restrict the scan to resident pages. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- Adjacent regions with the same validity are merged, split into chunks of at most `512*1024` bytes, then sorted (`quicksortmemoryregions`). — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- The map is built in two passes. Pass 1 only counts candidates per value (`addpointer(..., false)` increments `expectedsize`). Pass 2 allocates and fills (`addpointer(..., true)`). With `alligned` set, only slots whose address is a multiple of 4 are considered and the scan steps by pointer size; otherwise it steps byte by byte. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- Optional "class pointer" filter: with `mustbeclasspointers`, a cache (`InModulePointerMap`, `isModulePointer`) checks whether a candidate is module-related. `allowNonModulePointers` relaxes this to readable targets. The intent is to keep only pointers to objects whose vtable lives in a module. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- Data structures:
  - `TPointerlist` is one node per distinct pointer value: `PointerValue`, a dynamic array of `{address, staticdata}`, and `Previous`/`Next` links.
  - `TStaticData` holds `{moduleindex, offset}` (offset = slot address minus module base), and is nil for non-static slots.
  - The index is `TReversePointerListArray = array[0..15]` per nibble. The trie descends with `entrynr := pointervalue shr ((maxlevel-level)*4) and $f`, where `maxlevel` = 7 for 32-bit (8 nibbles) and 15 for 64-bit.
  - Leaf lists start with a capacity hint of 1, raised to 5/10/20/50 for "round" values (multiples of $10/$100/$1000/$10000), and grow by quadrupling.
  - A code comment says a generic tree/map was slower than this custom, non-thread-safe structure.

  — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- Static detection (`isStatic`/`isStatic2`) first checks thread-stack ranges (Windows), then `symhandler.getmodulebyaddress` to map the slot to a module index plus offset. With `specificBaseAsStaticOnly`, only slots inside a user-given `basestart..basestop` range count as bases. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- Range query: `findPointerValue(startvalue, var stopvalue)` descends the trie toward `stopvalue`. If that exact branch is missing, `findClosestPointer` walks to the nearest lower node, and the result is discarded if it falls below `startvalue`. Callers then iterate downward through `previous` links, so results arrive bucket by bucket from high value to low. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)

**Pointer map file format (`exportToStream`; saved as zlib-compressed `.scandata`)**
- Layout, in order:
  1. Magic byte `$CE`, then `ScanDataVersion` (=1).
  2. Module list: DWORD count, then per module a DWORD name length, the name bytes and a QWORD base.
  3. A flag byte. If 1, QWORDs `basestart`/`basestop` follow.
  4. DWORD `maxlevel` (7 or 15), then QWORD `count`.
  5. For each pointer value in linked-list order: QWORD value, DWORD entry count, then per entry a QWORD slot address and a static flag byte. A flag of 1 is followed by DWORD `moduleindex` and DWORD `offset`.

  — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- `.scandata` is written through `Tcompressionstream` (`clfastest`) from `pointerlisthandler.exportToStream`. — [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- Dark Byte (CE author) describes a pointer map as effectively a snapshot of the game's memory at the moment it was generated. The max level setting does not affect the map; it only applies to the later scan. — [CE forum, "Generate PointerMap"](https://www.cheatengine.org/forum/viewtopic.php?t=606159)

**Reverse search (`pointerscanworker.pas`, `rscan(valuetofind, level)`)**
- Search window: `startvalue := valuetofind - structsize; stopvalue := valuetofind`. With `NegativeOffsets`, `stopvalue` is extended by `structsize`. For each bucket returned, the offset at this level is `tempresults[level] := valuetofind - stopvalue`. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Static entries (`staticdata <> nil`) call `StorePath(moduleindex, moduleoffset)`. Non-static entries recurse with `rscan(entry.address, level+1)` while `level+1 < maxlevel`. When `staticonly` is false, non-static dead ends are also stored with module index -1. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Pruning options:
  - `noLoop`: abandon a branch if `valuetofind` already appears in `valuelist[0..level-1]`.
  - `LimitToMaxOffsetsPerNode` / `maxOffsetsPerNode`: for `level>0`, stop after N value buckets per node.
  - `onlyOneStaticInPath`: once a static hit occurs, do not descend into the remaining non-static entries of that node.
  - `mustEndWithSpecificOffset`: at level i the window collapses to `valuetofind - mustendwithoffsetlist[i]`, optionally widened by `mustEndWithSpecificOffsetMaxDeviation`.
  - `alligned`: offsets are stored `shr 2` to save bits.

  — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Heap-data option: with `useheapdata`, a heap allocation lookup (`frmMemoryAllocHandler.FindAddress`) can pin the window to the start of the containing allocation. With `useOnlyHeapData`, the branch exits if no heap match exists. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Parallelism: workers share a bounded `pathqueue` guarded by `pathqueueCS` plus a semaphore. A branch is queued only if `level+3 < maxlevel` and the queue is below a depth-dependent fill threshold: 2/3 of `MAXQUEUESIZE` in general, 7/8 for level ≤2, 15/16 for level ≤1, `MAXQUEUESIZE-1` for level 0. Otherwise the worker recurses inline (DFS). Level 0 blocks on the lock so top-level branches are never dropped. When out of disk, branches are spilled to an overflow queue file to allow resume. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Seeding: in address mode the queue starts with one element, `{startlevel:=0, valuetofind:=automaticaddress}`. In "find by value" mode, every address holding the value seeds the queue. — [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- Results are flushed at about 15 MB (local worker: a 16 MB buffer). Network workers clamp flush size to 4 KB–15 MB. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)

**Result (.PTR) format**
- Header, in order:
  1. `$CE`, then `pointerscanfileversion`.
  2. The module list.
  3. `maxlevel`.
  4. A compressed flag. If compressed, these follow: an alignment flag, then `MaxBitCountModuleIndex`, `MaxBitCountModuleOffset`, `MaxBitCountLevel` and `MaxBitCountOffset`, then the must-end-with offset list.
  5. `mustStartWithBase`, plus `BaseStart` when it is set.
- Bit widths are computed from the data, e.g. `getMaxBitCount(modulelist.Count-1)` and `getMaxBitCount(sz)` minus 2 when aligned.

  — [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- Compressed entries are bit-packed in the order module offset, module index, level count, then `level` offsets of `MaxBitCountOffset` bits each. Uncompressed entries are module id (DWORD), offset (QWORD), count (DWORD) and `maxlevel` DWORD offsets. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)

**Multi-map compare ("instant rescan")**
- The controller loads each additional pointer map into its own `TReversePointerListHandler` (`instantrescanfiles[i].plist`). It realigns module IDs to the main map with `reorderModuleIdList`, and gives every worker `instantrescanlist[j]` (that map's pointer list) and `instantrescanaddress[j]` (the target address in that session). — [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- Before `StorePath` writes a candidate, `DoRescan` walks the chain from the base (`getPointer`) and compares the endpoint to `instantrescanaddress`. A path failing any map is never written. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas)
- Dark Byte (Jan–Mar 2017) says pointermap compare gives an "instant rescan" of each result against the second map. It is faster than a normal rescan and cuts disk needs from about 200 GB to 1 GB or less. He also says to use the smaller map as the main scandata, and that if you have only one map you should use it as the compare map so CE generates a temporary base map at scan start. — [CE forum, "Pointerscan how-to" (Dark Byte, 2017-01-16, replies through 2019-01-14)](https://forum.cheatengine.org/viewtopic.php?p=5745475)
- Dark Byte (2023-09-25): a second map from a different game instance is needed so that you keep only paths valid across two runs instead of processing "several terabytes of invalid potential paths". This is why one map can yield 200K+ results. — [CE forum, "need explanation on the principle of pointer map"](https://forum.cheatengine.org/viewtopic.php?t=621469)
- The workflow Dark Byte recommends:
  1. Find the value and add it to the table.
  2. Generate a pointer map.
  3. Restart the game, or the PC.
  4. Find the value again and generate a second map under a different name.
  5. In the pointer scan dialog, choose "Use saved pointermap" with the smaller map, and tick "Compare results with other pointermap(s)" with the other address.

  To speed this up, set "Max different offsets per node" to 2–3 and the max offset high, e.g. 1048576. This gives fewer but generally good results with small offsets. One map can be reused for many target addresses. Each map costs RAM, and too many cause paging. — [CE forum, "Pointerscan how-to"](https://forum.cheatengine.org/viewtopic.php?p=5745475)

**Other parameters and behavior**
- Official CE help: default max level is 5. Change max offset only if you suspect an offset larger than 2048 bytes. Rescan (by new address after a restart) filters out pointers that no longer reach the target, and the help says to repeat until reliable pointers remain. — [CE help: Pointer scan](https://www.cheatengine.org/help/pointer-scan.htm)
- ParkourPenguin (CE forum regular, 2023-09-24) describes the core loop as: (1) take the target address, (2) scan memory for pointer values between `address - max_offset` and `address`, (3) recurse on each hit. The scanner has no concept of registers or assembly; it only looks at pointer-sized values. — [CE forum thread 621469](https://forum.cheatengine.org/viewtopic.php?t=621469)
- Reported pain points: comparing pointer maps sometimes returns zero results, and a full scan can produce dozens of GB. Dark Byte attributes empty results to the max level and struct size settings. — [CE forum, "Generate PointerMap"](https://www.cheatengine.org/forum/viewtopic.php?t=606159)

### Inferences
- Conceptually, a CE pointer map is: (a) the module table (name, base) for that session, so static slots can be stored module-relative and compared across ASLR or relocation; and (b) the full reverse multimap value → [slot address, optional (module, rva)]. Cross-session comparison needs only these. Comparing does not need the game to still be running, because the "rescan" resolves each path's derefs in the saved map's data.
- The critical insight is that each extra pointer map is a filter with essentially zero false-negative cost. A true stable path survives every map, while random coincidental paths almost never resolve correctly in two independent snapshots. Two or three maps from cold restarts in different game states should remove the vast majority of the junk that ParkourPenguin estimates at over 99.99%.
- CE's "max different offsets per node" is a breadth cap. Because buckets are visited from high value to low, the cap keeps the nearest values, i.e. the smallest offsets. It biases toward small offsets, which correlate with "real" struct fields.

### Gaps
- DeepWiki's CE pointer-scanner page (https://deepwiki.com/cheat-engine/cheat-engine/3.1-pointer-scanner) returned HTTP 429, so it was not used.
- The exact `getPointer` implementation inside `DoRescan` was not read. That it resolves derefs through the saved map's data (not live memory) is inferred from the controller passing `instantrescanlist[j]` per map.
- How CE's heap-data option (`frmMemoryAllocHandler`) gathers allocation data was not confirmed. It likely requires hooking allocations in-process, i.e. injection.
- The source of the "THREADSTACKn" naming of stack bases in CE's UI was not located in the fetched excerpts. Only the `useStacks`/`threadstacks`/`stacksize`/`stacksAsStaticOnly` options were confirmed, with `threadstacks` commented as "number of stacks used as a lookup (counted from first stack to newer ones)".
- The current default max offset in modern CE releases was not confirmed. The help text implies 2048; other tutorials cite different values.

---

## 2. Open-source pointer scanners: algorithms and performance

### Takeaway
All surveyed scanners share the same core: build a value-sorted reverse index of pointer-sized slots, then DFS backward from the target with a `[t - maxOffset, t]` range query per hop. They differ in the index structure (CE's nibble trie, a Rust `BTreeMap`, PyMemoryEditor's lazy map), the pruning options and the parallelism. A different family, NCC Group's PSR, avoids brute force entirely: it traces the instructions the game itself executes to reach the data, recovering the game's own access path.

### Cited Findings
- **ptrscan / PointerSearcher-X (Rust, kekeimiku)**:
  - The index is `BTreeMap<usize, Vec<usize>>`, keyed by pointer value with a list of slot addresses as the value. Each hop does `map.range(min..=max)` with `min = addr - range.1` and `max = addr + range.0`; `range` is documented as "forward offset : backward offset", so negative offsets are supported.
  - Static bases are a caller-supplied list `points`. The scanner uses `binary_search` when it has more than 32 entries and a linear scan otherwise.
  - Traversal is a recursive DFS with a `(location, offset)` stack, with early exit via `ControlFlow::Break` and results delivered by callback.
  - Pruning is only the range filter and `depth`. A `Chain::ref_cycle()` helper exists but is not called during the scan.

  — [ptrscan source (docs.rs)](https://docs.rs/ptrscan/latest/src/ptrscan/lib.rs.html); [ptrscan crate, v1.0.0, docs dated 2026-10-23](https://docs.rs/ptrscan)
- PointerSearcher-X was created to address scanmem/scanmem#431 and korcankaraokcu/PINCE#15.
  - Supported platforms: aarch64-macos, aarch64-ios, x86_64-linux-gnu, aarch64-linux-gnu and nintendo-switch.
  - Android is beta; x86_64-windows is only "alpha".
  - A 2026-03-06 note says the PINCE plugin stays free, while other targets will use license activation.

  — [PointerSearcher-X GitHub](https://github.com/kekeimiku/PointerSearcher-X)
- **PINCE** (Linux, Python) builds pointer scanning on PointerScanner-X and memory scanning on a libscanmem fork. — [PINCE repo mirror](https://gitea.com/Zenoctate/PINCE/src/)
- **PyMemoryEditor** (Python, cross-platform):
  - `scan_pointer_paths` is the inverse of `resolve_pointer_chain`. Its defaults are `max_depth=3`, `max_offset=0x400`, `aligned=True` and `writable_only=True` (the map is built from writable memory only).
  - `ptr_size` is 4 or 8. `static_ranges` defaults to every loaded module's image range. There are also `max_results`, `progress_callback` and `should_stop` options.
  - The map is built and walked lazily as you iterate.
  - Paths are saved as JSON with module plus offsets, not absolute addresses. `rebase(process)` recomputes the base after ASLR.
  - `rescan_pointer_paths` and `compare_pointer_scans` intersect scans. Paths match only if module, module offset, offsets and pointer width all agree.
  - Performance: going from depth 4 to depth 6 is "usually a 10× slowdown". The docs suggest starting at depth about 2 and max_offset 0x100 with a results cap.

  — [PyMemoryEditor: Pointer scan (reverse)](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- **Squalr** (C#): a memory editor with scanning and pointer features, a GUI, a scripting API and NuGet back-end packages. Its speed is credited to multithreading plus SIMD, and it is described as often faster than CE. — [Hakin9 write-up](https://hakin9.org/squalr-game-hacking-tool-written-in-c/); [NuGet profile](https://www-1.nuget.org/profiles/zcanann)
- **game_cheetah** (Rust, mkrueger): a memory editor with pointer-chain support (module-relative offsets) that can automatically search for pointer chains when saving results. From a search-result summary only; the repo was not read. — [game_cheetah GitHub](https://github.com/mkrueger/game_cheetah)
- **NCC Group PSR** (Pointer Sequence Reverser), Nick Collisson, 2017-12-07, C++ on the Win32 debug API:
  - Attaches with `DebugActiveProcess` and sets a one-shot memory breakpoint on the target via `VirtualProtectEx(PAGE_GUARD)`.
  - Single-steps every thread by setting the EFLAGS trap flag (bit 8) through `SetThreadContext`, and records each instruction plus changed general-purpose registers. ESP/EBP are excluded.
  - On a hit it disassembles with Capstone and walks backward: register used for the access → last time that value appeared in the trace → the register read by that instruction → and so on, until it reaches an instruction-pointer-relative read or an immediate (a static address).
  - It skips reads through ESP/EBP "because sequences that traverse the stack are usually unstable".
  - It minimizes traces and marks vtable pointers with a page-protection heuristic: the value points to read-only memory whose first entry points to executable memory.
  - Case study: a 32-bit non-PIE C++ FPS. PSR found paths from static 0xA08F60 to the player object and an alternate path through a circular doubly-linked list of all players. The paper calls PSR faster and usually more accurate than CE's pointer scanner, which "can take hours" for several levels and "produce[s] large numbers of unstable sequences".

  — [NCC Group whitepaper (PDF)](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)

### Inferences
- **Fit for an external C# x86 editor.**
  - Brute-force pointer scanning (CE, ptrscan or PyMemoryEditor style) is fully external. It needs only `OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION)`, `VirtualQueryEx`, `ReadProcessMemory` and module enumeration (`EnumProcessModulesEx` / Toolhelp32). No injection or driver is needed.
  - PSR-style tracing is also out-of-process but uses the Win32 debug API: attach, PAGE_GUARD and single-stepping. It is intrusive (it heavily slows the game while stepping), and attaching a debugger may trip anti-debug checks. It suits a dev-time research mode, not shipped runtime behavior.
  - CE's heap-data mode most likely needs in-process allocation hooking (not confirmed; see section 1 Gaps).
- **Index choice for C#.** The cheapest structure that matches CE and ptrscan semantics is two parallel `uint[]` arrays `(value, slotAddress)` sorted by value, from radix sort or `Array.Sort(keys, items)`. A range query is then `lowerBound(t - maxOff)` up to `upperBound(t)`, a contiguous slice that should be visited from high value to low so the smallest offsets come first.
  - Static info can be a parallel `ushort moduleIndex` array, or recomputed by binary-searching a sorted module-range table.
  - For forward resolution in a saved snapshot (the compare step), keep a second view sorted by slot address. Alternatively, store the raw bytes of the scanned writable regions and deref by region lookup.
  - At 8 bytes per pointer pair, even tens of millions of pointers fit in a few hundred MB. That is acceptable for a one-time research pass, but heavy for a 32-bit host process with a 2–4 GB address space, so consider running the scanner as a separate 64-bit helper or streaming to disk.
- **Parallelism.** CE's pattern (a shared work queue for shallow levels, inline DFS for deep levels) maps directly onto `Parallel.ForEach` over level-0 and level-1 candidates, with each task doing a private recursive DFS and a thread-local result buffer.
- **Implementation blueprint** (combining the cited designs):
  1. **Snapshot.** Walk regions with `VirtualQueryEx`, keeping `MEM_COMMIT` and not `PAGE_GUARD`/`PAGE_NOACCESS`. Prefer writable protections, as in PyMemoryEditor's `writable_only`. Read in chunks of 512 KB or more, as CE does. For every 4-aligned slot, keep the value `v` if `v` falls inside any committed region, using binary search over sorted region bounds. Tag the slot static if it lies inside a module image, ideally only its writable `.data`/`.bss` sections, and store `(moduleIndex, rva)`.
  2. **Reverse DFS** from the target `T` with `maxLevel`, `maxOffset` and options: `noLoop`, `maxOffsetsPerNode`, `onlyOneStaticInPath`, `mustEndWith[]`, aligned offsets only.
  3. **Persist** candidates module-relative.
  4. **Filter** each candidate through N additional snapshots taken in other sessions or game states, each with its own target address.
  5. **Rank** the survivors (see section 3).

### Gaps
- No README-level or source-level description of Squalr's pointer scanner algorithm was retrieved. Its approach and performance remain unverified.
- PointerSearcher-X's map-construction code and benchmarks were not visible (only `main.rs` and the readme were listed). Its performance claims are unverified.
- scanmem/GameConqueror appears to lack a native pointer scanner: PointerSearcher-X exists to address scanmem#431. This is an inference; the scanmem issue itself was not read.
- No peer-reviewed academic paper on automated pointer-chain discovery for games was found. The NCC whitepaper is the closest authoritative technical source.

---

## 3. Ranking pointer paths for robustness (restart vs game update)

### Takeaway
The practical ranking signals are:
1. Survives N cold restarts and different game states.
2. Short chain (depth 1–3).
3. Small, aligned offsets, consistent with real struct field sizes.
4. Base in the main executable's static data rather than a DLL, stack or heap.
5. Intermediate nodes are real objects (a vtable/class pointer at the deref target).
6. Several independent aliases converge on the same object.

Surviving a game update is a different, harder property. It depends on the static base and offsets being re-derivable, through code signatures or engine metadata, rather than on the path being stable.

### Cited Findings
- Most single-map results are coincidental (">99.99%"). A typical tell is an offset larger than the size of the struct actually pointed at. — [CE forum thread 621469 (ParkourPenguin, 2023-09-24)](https://forum.cheatengine.org/viewtopic.php?t=621469)
- CE's "max different offsets per node" set to 2–3 yields fewer results that are "generally good, with low offset values". — [CE forum, "Pointerscan how-to" (Dark Byte, 2017)](https://forum.cheatengine.org/viewtopic.php?p=5745475)
- In PSR's view, "the most helpful sequences begin at a static address relative to the base of the executable. Such sequences are generally stable across application restarts". Intermediate objects can be confirmed by a vtable pointer at offset 0, and the same vtable across instances indicates the same class. — [NCC Group whitepaper](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)
- CE can restrict candidates to "class pointers": values pointing at objects that start with a module pointer (vtable). — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- KH3 trainer case study (dev.to, "Behavior determines the resolution", Jul 7, year not shown):
  - The author capped `MAX_DEPTH` at 2 to avoid coincidental long chains and widened `WINDOW` from 0x100 to 0x1000.
  - Candidates were saved as `(module, RVA, offsets)` and verified after multiple cold restarts.
  - Three chains survived, all rooted in the main exe: `+0x870 -> +0x7E04`, from roots 0x9DE3B68 and 0x9DE3B98 (0x30 apart, likely adjacent fields of one global) and from 0x9DE7378.
  - Keeping several aliases "lets the tool detect disagreement if a future build shifts one global" instead of silently failing.
  - Final checks: all chains agreed on the same object base, `addr - base == 0x7E04`, and a reversible write test succeeded.

  — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)
- PyMemoryEditor's match rule for comparing scans: module, module offset, all offsets and pointer width must all agree. Paths are stored module-relative and `rebase`d to the module's current load address. — [PyMemoryEditor docs](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- The KH3 author started by considering only the main exe as an anchor, then allowed any loaded module, since chains can live in DLLs. — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)

### Inferences
- **Proposed scoring for surviving paths** (most important first):
  1. **Validation breadth.** Count of independent sessions and states in which the path resolves correctly: cold restart, different level/map, different save or hero, after death or respawn, after returning to the menu/tavern. This dominates every other signal.
  2. **Depth.** Fewer derefs mean fewer chances to cross a transient object.
  3. **Base location.** Main exe `.data`/`.bss` is best. System or third-party DLL globals and stack bases rank lower; for stacks, PSR explicitly calls them unstable.
  4. **Offset plausibility.** Offsets should be 4-aligned and bounded by known struct sizes. A final offset equal to a known field offset (from SDK headers) is a strong positive.
  5. **Type checks.** Each intermediate deref target starts with a vtable in the game module. In UE3, the UObject's class pointer resolves to the expected UClass.
  6. **Alias agreement.** Multiple distinct roots converge on the same object.
- **Restart-stable versus update-stable.**
  - A path whose base is a fixed RVA in the main exe survives restarts, especially when the exe does not use ASLR, so the module sits at its preferred base every run.
  - Any recompile can move globals, which changes the RVA, and can change struct layouts, which changes the offsets.
  - Update robustness therefore comes from re-deriving the base at runtime: an AOB signature over the code that references the global (`mov reg, [abs32]` in 32-bit non-PIE code embeds the absolute address), or engine metadata such as UE3's global object and name tables plus reflection. Struct offsets can be re-derived from reflection data or SDK headers.
  - Paths whose intermediate offsets equal known engine/SDK field offsets are more likely to survive updates than arbitrary coincidental offsets, because they reflect the source-level structure.
- For a UE3 title, the "ideal" static base is usually one of a few engine globals (e.g. the engine/world/object-table singletons) in the main exe's data section. A path like `GEngine -> GamePlayers[0] -> Actor/Controller -> Pawn -> field` mirrors how the game reaches its own data, which is the PSR philosophy. This is an inference from general UE3 knowledge plus PSR's argument; no UE3-specific source was fetched in this pass.

### Gaps
- No published empirical study was found that measures which heuristics predict survival across game updates. The ranking above is synthesized practice, not measured.
- No Guided Hacking or UnknownCheats primary tutorial pages were retrieved (searches returned scraped mirrors only). Their specific ranking advice is not captured.

---

## 4. Common failure modes and how tools detect or mitigate them

### Takeaway
Paths fail when they:
- traverse something transient (per-level objects, recreated actors, temporaries, the stack);
- depend on reused or freed memory (ABA);
- are coincidental (most raw results);
- were cut off by search limits (max offset or depth too small).

Mitigations are multi-snapshot intersection across varied states, type and vtable checks, excluding stack and transient bases, sensible offset windows (or scanning from the struct base with a known terminal offset), and runtime validation before every write.

### Cited Findings
- **Coincidental paths.** Over 99.99% of single-map results are coincidences, e.g. offsets larger than the pointed-to struct. — [CE forum thread 621469](https://forum.cheatengine.org/viewtopic.php?t=621469)
- **Search limits hide the real chain.** In the KH3 case, "the final +0x7E04 hop exceeded CE's default max-offset threshold", so the real chain was never searched. The fix was to scan for the struct base (`munny - 0x7E04`) and treat `+0x7E04` as the known terminal offset. — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)
- **State-dependent relocation.**
  - KH3 HP relocated mid-session, while Munny stayed put across warps and deaths but its address changed with the save file.
  - Address-suffix filtering (`(addr & 0xFFFF) == 0x8674`) worked across restarts but broke on warps and area transitions.
  - The author's rule: use execution-hook capture for values that relocate mid-session, and static chains for session-stable values.
  - Hooking the access instruction was impractical because it only fired when the game touched the value (e.g. on a shop purchase).

  — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)
- **Scene-specific pointers.** A CS:GO load-remover author reported that a pointer worked for one loading screen but not all of them. Search-result summary only. — [speedrun.com CS:GO forum](https://www.speedrun.com/csgo/forums/nqa3o)
- **Stack paths.** PSR ignores reads through ESP/EBP "because sequences that traverse the stack are usually unstable due to the stack's inherent volatility". CE makes stack bases opt-in and limited (`useStacks`, `threadstacks` counted from the first thread's stack, `stacksize`, `stacksAsStaticOnly`). — [NCC Group whitepaper](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf); [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- **Freed and reallocated memory (ABA).** A traced sequence can be wrong if the structures it traverses occupy memory that was freed and reallocated. The most recent value may belong to a new allocation at the same address. — [NCC Group whitepaper](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)
- **Loops.** Without `noLoop`, chains can revisit the same address (e.g. a doubly-linked list or a parent/child back-pointer) and blow up the result count. CE's `noLoop` rejects a path containing the same address twice. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas); [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- **Result and disk explosion.** Full scans reach dozens of GB, and roughly 200 GB without pointer-map compare. The compare reduces this to about 1 GB or less. — [CE forum, "Generate PointerMap"](https://www.cheatengine.org/forum/viewtopic.php?t=606159); [CE forum, "Pointerscan how-to"](https://forum.cheatengine.org/viewtopic.php?p=5745475)
- **Empty results after comparing maps**, attributed to max level and struct size settings. — [CE forum, "Generate PointerMap"](https://www.cheatengine.org/forum/viewtopic.php?t=606159)
- **Cost growth.** Depth 4 → 6 is about 10× slower. — [PyMemoryEditor docs](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- **Pages excluded from the map.** CE skips `PAGE_GUARD`/`PAGE_NOACCESS` and, optionally, `PAGE_NOCACHE`/`PAGE_WRITECOMBINE`, read-only or executable pages, and non-resident pages. An overly strict filter can drop the region holding a real link. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)

### Inferences
- **Mitigations an external tool can apply cheaply:**
  - Take snapshots in deliberately different states (different level, after a level reload, different hero or save, after a menu round-trip), not just restarts. This kills paths that only work in one level.
  - Reject paths whose base is in a stack region or in heap memory.
  - Require a vtable or class-pointer sanity check at each intermediate object.
  - Prefer the struct-base-plus-known-offset approach (CE's "must end with specific offsets"). When the field offset is known from SDK headers, this collapses the last hop's window to a single value and avoids the KH3-style max-offset trap.
- **Transient objects (UE3 specific).** Actors such as pawns and projectiles are destroyed and recreated across map loads and respawns. A path through a per-level actor list may be valid only while that level is loaded. Prefer chains through persistent objects (engine, game viewport, local player, player controller, replication info) and re-resolve the actor-level hop every tick rather than caching it.
- **Runtime validation before writes.** Resolve the chain freshly each time. Check that every intermediate pointer lies in committed memory (`VirtualQueryEx` or a cached region map). Check that the final object's vtable or class matches. Check that the read value is within a plausible range. Skip silently on failure; never write through an unvalidated chain.

### Gaps
- No authoritative source was found on thread-local storage (TLS) as a pointer-scan failure mode, e.g. whether CE treats TLS slots specially. Searches did not surface it.
- How many snapshots or states are "enough" has no quantified guidance in the sources found. Dark Byte says only that one map is the minimum and more are better.

---

## 5. Automating pointer-path re-discovery at runtime ("self-healing")

### Takeaway
The robust pattern is:
1. Ship several pre-validated candidate paths (aliases) per value.
2. Resolve all of them at runtime and vote, with type and value sanity checks.
3. If none validate, re-derive. Locate the object by a signature (vtable/class pointer, a known field pattern, or a value the user can trigger), then run a constrained reverse scan (small depth, known terminal offsets, main-module bases only), validate, and cache the result.

### Cited Findings
- **Multi-alias consensus.** Keeping several surviving chains with different static roots lets the tool detect disagreement when a build shifts one global, instead of failing silently. The KH3 trainer verified that all chains agreed on the same object base before trusting them. — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)
- **Two-phase scan then verify.** The KH3 scanner walks from the live base to module-static roots, saves `(module, RVA, offsets)`, and a verify phase re-resolves after cold restarts and keeps only survivors. — [dev.to KH3 trainer article](https://dev.to/bphdev/behavior-determines-the-resolution-building-a-kh3-trainer-in-python-2ec6)
- **Seeding from a value, not an address.** CE's "find by value" mode seeds the reverse search with every address currently holding a value. — [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- **Persistence and helpers.**
  - Save, load, rescan and compare with module-relative storage and `rebase(process)`.
  - The API has `max_results`, `should_stop` (deadline or cancel) and `progress_callback`. These are the hooks needed to run a bounded re-discovery in the background.

  — [PyMemoryEditor docs](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- **Typing objects by vtable.** Once a vtable address is known (e.g. the player vtable at 0x8AC530 in PSR's case study), its appearance identifies objects of that class in future traces. The read-only-to-executable page-protection test detects vtables generically. — [NCC Group whitepaper](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)
- **Constrained scans.** CE's "must end with specific offsets" collapses the last hops to known offsets, and "base address must be in range" (`mustStartWithBase`/`BaseStart`, `specificBaseAsStaticOnly` with `basestart..basestop`) restricts bases to a chosen module or section. Both drastically shrink a runtime re-scan. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas); [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas); [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)

### Inferences
- **Suggested self-healing loop for an external C# editor:**
  1. **Store** each value as `{ name, candidates: [ {module, rva, offsets[], lastOkUtc, okCount, failCount} ], expectedVtable/class, terminalOffset, valueValidator }`.
  2. **Resolve** all candidates, either per tick or on demand. Accept a result only if it passes: committed memory at every hop, the expected vtable or class at the object base, and a plausible value range. If accepted candidates disagree, take the majority object base; ties mean distrust and no writes.
  3. **Demote and promote.** Adjust per-candidate counters, and persist the ordering so the best path is tried first next session.
  4. **Re-discover on total failure.** Find the object without a path. Options:
     - (a) Brute-force scan for the vtable/class pointer value in writable memory; the hits are object bases of that class.
     - (b) Scan for a user-triggered value (CE's "find by value").
     - (c) Use engine metadata (for UE3, walk the global object table and match class and name).

     Then run a reverse scan restricted to depth ≤3, aligned offsets, `noLoop`, bases only inside the main module's writable sections, and the last hop fixed to the known field offset. Validate the result in a second state, for example after a level change, before promoting it.
  5. **Keep it cheap.** The full pointer map for a 32-bit process is built once per re-discovery, not per tick. The bounded DFS with a fixed terminal offset typically explores a tiny fraction of the space.
- When the object can be found directly each session (option 4a or 4c), a pointer path may be unnecessary for that value. Path discovery then becomes an optimization, a cached fast route, rather than a correctness requirement. This is the most robust design for engine-reflective games like UE3.
- **Injection or driver needs.** None of this requires injection; all of it uses `ReadProcessMemory`. Capturing values that relocate mid-session via an execution hook (the KH3 author's recommendation for HP) would need either the debug API (hardware or PAGE_GUARD breakpoints, external but intrusive) or code injection.

### Gaps
- No published, production-grade "self-healing pointer" library or paper was found. The loop above is synthesized from the cited building blocks.
- No source quantified runtime cost (ms) for a bounded re-scan on a 32-bit process. This needs local benchmarking.

---

## 6. 32-bit (x86) specifics

### Takeaway
For a 32-bit target, pointers are 4 bytes and naturally 4-byte aligned. The CE trie is 8 nibbles deep, and compressed offsets lose 2 bits when aligned. User-mode address space is 2 GB, or 4 GB when the exe is large-address-aware and runs on 64-bit Windows. A non-ASLR exe loads at its preferred base, 0x400000 by default, which makes main-exe static bases trivially restart-stable. DLLs still need module-relative storage.

### Cited Findings
- **User-mode virtual address space per 32-bit process:**
  - 32-bit Windows: 2 GB, or up to 3 GB with `IMAGE_FILE_LARGE_ADDRESS_AWARE` plus 4GT.
  - 64-bit Windows: 2 GB with LAA cleared (the default), 4 GB with LAA set via `/LARGEADDRESSAWARE`.

  — [Microsoft Learn: Memory Limits for Windows Releases (updated 2025-06-11)](https://learn.microsoft.com/en-us/windows/win32/memory/memory-limits-for-windows-releases)
- **Default image bases and ASLR.** The default base for a 32-bit EXE is 0x400000 (0x10000000 for a 32-bit DLL). The base must be a multiple of 64K. The loader relocates if the space is occupied, unless the image is `/FIXED`. Microsoft recommends `/DYNAMICBASE` (ASLR, on by default in modern toolchains) instead of fixed bases. — [Microsoft Learn: /BASE (Base address)](https://learn.microsoft.com/en-us/cpp/build/reference/base-base-address)
- **CE nibble trie depth.** `maxlevel` is 7 for 32-bit (8 nibbles of a 4-byte value) and 15 for 64-bit. With alignment on, the pointer map considers only 4-aligned slots and steps by pointer size. — [CE source: pointervaluelist.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointervaluelist.pas)
- **Compressed offset width.** With alignment, CE stores offsets `shr 2` and reduces `MaxBitCountOffset` by 2. The module-offset field is 32 bits unless the target is 64-bit. — [CE source: pointerscanworker.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscanworker.pas); [CE source: pointerscancontroller.pas](https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/pointerscancontroller.pas)
- **Pointer width.** PyMemoryEditor requires `ptr_size=4` for 32-bit targets, and a 32-bit path never matches a 64-bit one. — [PyMemoryEditor docs](https://pymemoryeditor.readthedocs.io/en/latest/guide/pointer-scan.html)
- **Absolute addresses in code.** PSR's case study was a "32-bit, non-position-independent binary", with a static root at 0xA08F60 (inside a main exe loaded at the default 0x400000 region). PSR stops tracing at an immediate or instruction-pointer-relative read, which is how a static address appears. In 32-bit non-PIE code the global's absolute address is an immediate or displacement in the instruction. — [NCC Group whitepaper](https://www.nccgroup.com/media/hflpwbqc/_automated-reverse-engineering-of-relationships-between-data-structures-in-c-binaries.pdf)

### Inferences
- **Valid-pointer test for the snapshot.**
  - A 4-byte value `v` is a candidate pointer only if `0x00010000 ≤ v < 0x7FFF0000` (non-LAA), or below about `0xFFFF0000` if the exe is LAA on 64-bit Windows, and it falls inside a committed region.
  - Check the exe's PE header `IMAGE_FILE_LARGE_ADDRESS_AWARE` flag to pick the bound.
  - The low 64 KB and the top 64 KB of the user range are reserved, so values there are never valid pointers. These exact bounds come from general Windows knowledge, not from a fetched source.
  - Requiring `v % 4 == 0` for intermediate object pointers is a strong, cheap filter on a 2010-era MSVC x86 game, since heap allocations are at least 8-byte aligned. Final field offsets can still be any 4-aligned value.
- **Non-ASLR main exe.** If the main exe was linked without `/DYNAMICBASE`, likely for a 2010–2011 title but verify the `DllCharacteristics` flag in the PE header, then `module base + rva` is a constant absolute address every run. Static bases in the exe can be stored as absolute addresses, but storing module-relative costs nothing and also handles the DLL case.
- **Scan cost and footprint.**
  - The whole user space is at most 2–4 GB, so a full pointer-map snapshot means reading at most that much. Practically it is the committed writable working set, likely a few hundred MB to about 1.5 GB for a UE3 game. Building the map is a seconds-to-tens-of-seconds operation.
  - The result arrays, at 8 bytes per pointer, must fit beside the game's own memory. A .NET x86 host has its own 2–4 GB limit, so large maps should be streamed or kept compact (`uint[]` rather than objects).
- **Fit to the stated setup.** All of this fits the external C# x86 design with `ReadProcessMemory`, `VirtualQueryEx` and Toolhelp32/PSAPI module enumeration. The C# process should match bitness (x86) or use the WOW64-aware APIs for module enumeration.

### Gaps
- The exact reserved user-range bounds for 32-bit processes (0x10000 / 0x7FFEFFFF, or 0xFFFEFFFF with LAA on WOW64) were not confirmed from a fetched Microsoft page in this pass. They are stated from general knowledge.
- Whether typical 2010–2011 UE3 executables were built with `/DYNAMICBASE` was not confirmed from a source. Check the target exe's PE header directly.
- The Lonami "Writing our own Cheat Engine: Multilevel pointers" blog (https://lonami.dev/blog/woce-8/) could not be parsed by the fetcher (garbled output). Its implementation details are not included.
