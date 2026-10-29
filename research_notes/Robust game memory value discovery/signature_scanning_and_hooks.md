# AOB/Signature Scanning and Code-Based Address Capture for an External x86 Memory Editor

Scope: (a) building, scanning for, and resolving code signatures that survive game updates; (b) capturing the address of a dynamic value from the code that touches it (hooks, code caves, hardware/page breakpoints). The consumer is an **external** C#/.NET 8 x86 process targeting a 32-bit Unreal Engine 3 game (~2010-2011) that probably has no ASLR.

Technique risk legend used throughout:
- **[READ-ONLY]**: only ReadProcessMemory / VirtualQueryEx, or reading the PE file on disk. Cannot crash the game.
- **[PATCH]**: writes code bytes into the game (WriteProcessMemory/VirtualAllocEx). Can crash the game if done wrong.
- **[DEBUG]**: attaches as a debugger or sets debug registers. Can freeze, slow down, or kill the game.

Research budget note: about 30 tool calls were used. Several community sources (Guided Hacking and UnknownCheats threads, the Cheat Engine template source, Il2CppDumper, BinDiff/Diaphora docs) could not be fetched or returned nothing useful. Where these notes rely on general reverse-engineering knowledge instead of a fetched source, that is stated explicitly in **Inferences** or **Gaps**.

---

## Q1. Signature design: robust byte patterns, wildcarding, generators, mask formats

### Takeaway
A robust signature keeps opcode and register-encoding bytes, wildcards every byte that depends on build or load layout (absolute addresses, rel32 call/jmp displacements, struct/stack displacements that may shift), and is grown only until it matches exactly once. In practice that is about 12-20 bytes. Generators (IDA SigMaker family, ByteHunter, nvapi/sigmaker) automate the growth and can emit IDA-style, x64dbg-style, or C byte-array-plus-mask output.

### Cited Findings
- **What a signature is and why to use one:** a byte pattern that uniquely identifies a function's machine code, found at runtime by scanning the module's memory. AlliedModders presents it as more reliable than hand-maintained vtable offsets, which "are typically broken by updates." The page was last edited 16 Aug 2018 and its examples are Source-engine era. — [AlliedModders wiki: Signature Scanning](https://wiki.alliedmods.net/Signature_Scanning)
- **Bytes to wildcard (AlliedModders examples, x86-32):**
  - absolute addresses, such as the `mov ecx, dword_...` operand in the Teleport example;
  - relative call displacements (the bytes after `E8` in the `Ignite` signature);
  - stack/variable offsets that shift when code changes.

  Bytes kept fixed: function prologues (`83 EC 18 53 56`) and register-only instructions (`33 F6`, `33 C0`). — [AlliedModders wiki](https://wiki.alliedmods.net/Signature_Scanning)
- **AlliedModders workflow for uniqueness:**
  1. Disassemble the module (IDA on Windows).
  2. Locate the function by PDB or by unique instructions.
  3. Pick an instruction run unlikely to repeat and check every hit for duplicates.
  4. Compare the function across builds.
  5. Wildcard the unstable bytes and emit sig + mask (`sigcreator.c`, `makesig.idc`).

  The page also warns that its sample scanner returns only the first match, so uniqueness must be checked separately, and that the sample code has an off-by-one mask bug. — [AlliedModders wiki](https://wiki.alliedmods.net/Signature_Scanning)
- **Mask convention ("code style"):** the mask is the same length as the byte string, where `x` means the byte must match and `?` means ignore. Example: `"xxx????xx"`. — [AlliedModders wiki](https://wiki.alliedmods.net/Signature_Scanning)
- **The four output formats of IDA-Pro-SigMaker (A200K), for the same signature:**

  | Format | Example |
  |---|---|
  | IDA | `E8 ? ? ? ? 45 33 F6 66 44 89 34 33` |
  | x64dbg | `E8 ?? ?? ?? ?? 45 33 F6 66 44 89 34 33` |
  | C byte array + string mask | `\xE8\x00\x00\x00\x00\x45\x33\xF6...` with `x????xxxxxxxx` |
  | C raw bytes + bitmask | `0b1111111100001` |

  The plugin is invoked with Ctrl+Alt+S and copies the result to the clipboard. It also supports "Generating code Signatures by data or code xrefs and finding the shortest ones." Its search box auto-detects any of these formats and uses an AVX2-optimized search, falling back to IDA's builtin search otherwise. — [A200K/IDA-Pro-SigMaker](https://github.com/A200K/IDA-Pro-SigMaker)
- **Other generators:**
  - mahmoudimus/ida-sigmaker: zero-dependency, IDA 9.0+, "configurable wildcard operand patterns, nibbles". — [oosmetrics: ida-sigmaker](https://oosmetrics.com/repo/mahmoudimus/ida-sigmaker)
  - nvapi/sigmaker: IDA 9.x, Ctrl+Alt+S, IDA- and x64dbg-style output. — [nvapi/sigmaker](https://github.com/nvapi/sigmaker)
  - CPScript/ByteHunter: IDA plugin with a "unique signature" action that produces minimal signatures, "architecture-aware operand analysis" for wildcarding, and AVX2 search. — [ByteHunter](https://github.com/CPScript/ByteHunter)

  All three came from search snippets; their READMEs were not fetched in full.
- **Nibble wildcards:** Cheat Engine AOB strings accept `x`, `?` or `*` as wildcards, either for a whole byte or a single nibble (for example `5*`, `*D`, `1?`). Bytes can be space-separated or run together (`00xx8D6424E0`). — [Cheat Engine wiki: aobScanModule](https://wiki.cheatengine.org/index.php?title=Auto_Assembler:aobScanModule)
- **FFXIVClientStructs signature guidance (via search snippet only):** do not wildcard opcodes; register encodings are usually stable; aim for about 12-20 bytes; test uniqueness at runtime. When fetched on 2026-10-28 the page returned **HTTP 410 Gone**, so this is unverified secondhand content. — [Mintlify: FFXIVClientStructs signatures (snippet)](https://www.mintlify.com/aers/FFXIVClientStructs/dev/signatures)

### Inferences
These are general x86 and reverse-engineering knowledge, not fetched this session.
- **Which bytes to wildcard on x86-32** (standard IA-32 encodings):
  - **Always wildcard:**
    - `E8 rel32` (call) and `E9 rel32` (jmp) displacements;
    - `0F 8x rel32` (Jcc near) displacements;
    - absolute `disp32`/`moffs32` that point at globals, as in `A1 <abs32>` (mov eax,[abs]), `8B 0D/15/1D/35/3D <abs32>` (mov ecx/edx/ebx/esi/edi,[abs]), `FF 15 <abs32>` (call [IAT]), `68 <abs32>` (push imm string/global), `B9 <abs32>` (mov ecx, imm, often a global `this`).
  - **Usually wildcard:** struct field displacements (`8B 86 <disp32>`, `8B 46 <disp8>`) if the class layout may change between versions.
  - **Keep fixed:** opcodes, ModRM/SIB bytes (they encode which registers are used and are stable for a given compiler output), and short `EB rel8` jumps inside small functions, unless the function body changes.
- **Picking a site:** prefer instruction runs right at or near the access of interest that combine one rare opcode/ModRM combination (for example a specific SSE op, an unusual immediate, or `push` of a unique string address) with ordinary context. Avoid compiler boilerplate such as prologues (`55 8B EC 83 EC`), `CC` padding, and `__security_check_cookie` sequences, which repeat thousands of times.
- **Minimal-length uniqueness algorithm (what SigMaker-style tools do):**
  1. Start at the target instruction and append whole instructions, with operands wildcarded per the rules above.
  2. After each append, rescan the whole `.text` and stop when the count is exactly 1.
  3. If no unique signature appears within N bytes, try XREF signatures, that is, signatures at call sites that reference the target, and keep the shortest.
- **Mask format interchange:** IDA-style `?` and x64dbg-style `??` differ only in how a wildcard is written. "Code style" is a byte array plus an `x`/`?` mask string. A C# scanner should parse all three, as SigMaker's search does, so signatures from any tool paste in directly.

### Gaps
- The exact wildcarding heuristics inside SigMaker and ByteHunter (which operand types count as "volatile", and the max-length defaults) were not documented on the fetched README.
- No primary Guided Hacking or UnknownCheats tutorial was fetched. Forum guidance on signature length and uniqueness could not be dated or verified.
- No source was found on Binary Ninja or Ghidra sigmaker plugins, or on x64dbg's SwissArmyKnife pattern output.

---

## Q2. Resolving addresses from a match: x86-32 absolute operands, E8/E9 following, multi-hop, .reloc and ASLR

### Takeaway
On x86-32 the address of a global is embedded directly in the instruction as an absolute 32-bit value, so resolution is just reading 4 bytes at `match + operandOffset`. This contrasts with x64's RIP-relative `next_ip + disp32`. Relative branches (E8/E9) resolve as `site + 5 + rel32`. Every absolute address in a relocatable image has a matching `IMAGE_REL_BASED_HIGHLOW` entry in `.reloc`. For an EXE without relocations (the linker default) loaded at ImageBase 0x00400000, those absolute values are already final and never change between sessions.

### Cited Findings
- **The address formula:** LiveSplit's `SigScanTarget(int offset, params string[] signature)` returns `_address + off + sig.Offset`. An optional `OnFoundCallback(Process proc, SignatureScanner scanner, IntPtr ptr)` replaces the returned address, and is the intended hook for dereferencing or converting a relative operand into an absolute address. — [LiveSplit SignatureScanner.cs](https://raw.githubusercontent.com/LiveSplit/LiveSplit/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)
- **How GH-Offset-Dumper models relative branches:**
  - each signature has `pattern` (with `?` wildcards), `opLoc` (operand offset in the match) and `opLength` (instruction length);
  - the example `"E8 ? ? ? ? 8B D8"` uses `opLoc: 1, opLength: 5`, which matches a 5-byte E8 call;
  - `relativeByDefault` "treats all signatures that don't specify if they're relative or not as relative";
  - the log shows "Processing Relative Branch" and then "Resolved call location". — [GH-Offset-Dumper README](https://raw.githubusercontent.com/guidedhacking/GH-Offset-Dumper/master/README.md)
- **Mistdumper's config,** a static-analysis variant inspired by hazedumper, has per-signature `relative`, `offset`, `extra`, `pattern`, and optional dereference settings (example: `"offset": 3, "extra": 12`). The exact semantics of `extra` were not documented in the results. — [mistdumper](https://github.com/trendingtechnology/mistdumper)
- **PE `.reloc` format (Microsoft PE spec):**
  - The base relocation table "is divided into blocks. Each block represents the base relocations for a 4K page."
  - Each block starts with **Page RVA** (4 bytes) and **Block Size** (4 bytes, "including the Page RVA and Block Size fields"), followed by WORD entries: **Type** in the high 4 bits and **Offset** in the low 12 bits, "an offset from the starting address that was specified in the Page RVA field."
  - `IMAGE_REL_BASED_ABSOLUTE` (0) "is skipped... can be used to pad a block."
  - `IMAGE_REL_BASED_HIGHLOW` (3) "applies all 32 bits of the difference to the 32-bit field at offset."
  - `IMAGE_REL_BASED_DIR64` (10) is the 64-bit equivalent.

  — [Microsoft Learn: PE Format, .reloc Section](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
- **How the loader uses relocations:** "To apply a base relocation, the difference is calculated between the preferred base address and the base where the image is actually loaded. If the image is loaded at its preferred base, the difference is zero and thus the base relocations do not have to be applied." Also: "The loader is not required to process base relocations that are resolved by the linker, unless the load image cannot be loaded at the image base." — [Microsoft Learn: PE Format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
- **EXE defaults:**
  - `IMAGE_FILE_RELOCS_STRIPPED` (0x0001): "the file does not contain base relocations and must therefore be loaded at its preferred base address... **The default behavior of the linker is to strip base relocations from executable (EXE) files.**"
  - Default EXE ImageBase on NT-family Windows: **0x00400000**.
  - `IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE` (0x0040): "DLL can be relocated at load time."

  — [Microsoft Learn: PE Format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
- **Mapping between RVAs and file offsets:** a section's `VirtualAddress` is "the address of the first byte of the section relative to the image base when the section is loaded"; `PointerToRawData` is "the file pointer to the first page of the section"; if `SizeOfRawData < VirtualSize` the remainder is zero-filled. So `fileOffset = rva - VirtualAddress + PointerToRawData` for an RVA inside a section's raw data. — [Microsoft Learn: PE Format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)

### Inferences
These are standard x86 and PE mechanics, not fetched this session unless noted.
- **x86-32 resolution recipes** (in each, `m` is the match address):

  | Instruction at the match | Encoding | Result |
  |---|---|---|
  | `mov eax,[g]` | `A1 g32` | `g = Read<uint>(m+1)` |
  | `mov ecx,[g]` | `8B 0D g32` | `g = Read<uint>(m+2)` |
  | `call rel32` | `E8 r32` | `target = m + 5 + Read<int>(m+1)` (same for `E9`) |
  | `Jcc rel32` | `0F 8x r32` | `target = m + 6 + Read<int>(m+2)` |
  | `call [IAT]` | `FF 15 a32` | `a` is an import-table slot; deref once for the API address |

  The x64 `[rip+disp32]` form (`target = nextIP + disp32`) does **not** apply to a 32-bit target. Code borrowed from x64-focused tutorials will silently compute garbage on x86.
- **Multi-hop resolution** is a chain of these steps. Example: sig → E8 call → function start → second sig *inside that function* (or a fixed offset from its start) → `A1 g32` → global `g` → `Read<uint>(g)` (the heap object) → field offsets.
  - Each hop should validate its result: the call target is inside `.text`; the global is inside `.data`/`.bss` of the module; the dereferenced pointer lies in a committed MEM_PRIVATE region.
- **No ASLR means global addresses never move.** If the game EXE has `RELOCS_STRIPPED` set and/or lacks `DYNAMIC_BASE` (the linker defaults for that era, per the PE-spec quote above), it always loads at its ImageBase. Every absolute operand read from code is then a final VA, and global (static) addresses are identical every session.
  - Session-to-session "movement" is then purely heap allocation: the pointers *stored in* globals change, while the globals themselves do not.
  - So a signature is only needed for surviving **game patches**. Between sessions, a recorded static address plus a pointer chain is enough.
  - Verify by reading `Characteristics` and `DllCharacteristics` from the PE header, either on disk or at `0x00400000 + e_lfanew` in memory [READ-ONLY].
- **Module base for DLLs:** for DLLs, or an EXE that *does* relocate, compute `delta = actualBase - ImageBase` and add it to any VA taken from disk or IDA. Values read *from memory* are already relocated by the loader.
- **Using `.reloc` to enumerate every absolute reference to a global** (only possible if the image keeps relocations):
  1. Parse `.reloc`.
  2. For each HIGHLOW entry, compute `site = ImageBase + PageRVA + Offset` and read the 32-bit value stored there.
  3. If the value equals (or falls within `[g, g+sizeof(obj))`) the global's VA, that site is a code or data reference to `g`.

  This gives a complete xref list without a disassembler, because every embedded absolute address must have a reloc entry for the image to be relocatable. Pair it with a backward scan to the containing instruction start to recover the opcode. It is also a cheap way to re-find a global after a patch: find the string or function you can identify, then follow reloc-backed references.

  If relocs are stripped (likely for this game's EXE), you must instead brute-force scan `.text` for the 4-byte little-endian VA. On x86-32 this works well because addresses are embedded literally, though false positives are possible and should be confirmed by checking that the preceding bytes form a valid opcode.
- **Note on mandatory ASLR:** Windows' "Mandatory ASLR / Force randomization for images" exploit-protection setting can relocate images that lack DYNAMIC_BASE, but only if they have relocations, and it is off by default. This is from general knowledge, not fetched; see Gaps.

### Gaps
- It was not confirmed whether Visual Studio 2005/2008-era linkers (likely used for a 2010-11 UE3 title) defaulted `/DYNAMICBASE` on or off for EXEs. The PE spec only says relocations are stripped from EXEs by default. The authoritative check is to read the game's own PE header.
- Windows "Mandatory ASLR" behavior was not verified against a fetched Microsoft source.
- hazedumper's own config semantics (`extra`, `relative`, `offsets[]` dereference list) were not fetched from its repository. Only mistdumper's similar fields were seen via a search snippet.

---

## Q3. Scanning performance and correctness from an external process

### Takeaway
For one game module (tens of MB), the simplest correct approach is one ReadProcessMemory per committed, readable region, followed by an in-process masked search; this is what LiveSplit does for a whole module. ReadProcessMemory fails outright if any byte in the range is inaccessible, so wide scans must walk VirtualQueryEx regions and skip free, reserved, guard and no-access pages. Speed comes from searching for the longest solid (non-wildcard) run with a vectorized search and then verifying the masked bytes. Reloaded.Memory.SigScan reports roughly 5 GB/s non-vectorized and more than 30 GB/s with AVX on one thread, so scan time is dominated by the cross-process copy, not the matching.

### Cited Findings
- **ReadProcessMemory semantics:**
  - It needs a handle with `PROCESS_VM_READ`.
  - "Before any data transfer occurs, the system verifies that all data in the base address and memory of the specified size is accessible for read access, and if it is not accessible the function fails."
  - "The function fails if the requested read operation crosses into an area of the process that is inaccessible."

  Doc dated 2020-03-10, updated 2025-07-01. — [Microsoft Learn: ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory)
- **MEMORY_BASIC_INFORMATION (from VirtualQueryEx):**
  - `RegionSize` is "the size of the region beginning at the base address in which all pages have identical attributes."
  - `State` is `MEM_COMMIT` (0x1000), `MEM_FREE` (0x10000; "AllocationBase, AllocationProtect, Protect, and Type members is undefined"), or `MEM_RESERVE` (0x2000; "Protect member is undefined").
  - `Type` is `MEM_IMAGE` (0x1000000, "mapped into the view of an image section"), `MEM_MAPPED` (0x40000), or `MEM_PRIVATE` (0x20000).
  - Explicit `MEMORY_BASIC_INFORMATION32`/`64` layouts exist for cross-bitness debuggers.

  — [Microsoft Learn: MEMORY_BASIC_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-memory_basic_information)
- **How LiveSplit's SignatureScanner reads and scans:**
  - It reads the **whole** region in one `ReadBytes(_address, _size, out byte[] bytes)` call, lazily on first scan. If that fails the scan yields nothing.
  - It scans with `fixed` pointers.
  - It supports an `align` stride, and throws if the start address is not aligned.
  - `Scan` = `ScanAll(...).FirstOrDefault()`, and `ScanAll` lazily yields every match.
  - Pattern parsing: spaces are stripped, the string is read as hex pairs, and any pair that fails hex parsing becomes a wildcard, so `??` works.

  — [LiveSplit SignatureScanner.cs](https://raw.githubusercontent.com/LiveSplit/LiveSplit/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)
- **How AlliedModders gets the module range:** `VirtualQuery` plus the PE header's `SizeOfImage` give base and length. The page notes that using on-disk file size instead (as its Linux path does) "may not match the in-memory image size." — [AlliedModders wiki](https://wiki.alliedmods.net/Signature_Scanning)
- **Reloaded.Memory.SigScan:**
  - Claims "upwards of 5000MB/s per core (30000+MB/s with AVX)".
  - Benchmarks on 3 MiB: Avx 13,535 MB/s; Sse 11,050; "Compiled" 4,396; Simple 1,015. On 200 MiB: 30,025 MB/s.
  - Test machine: i7-4790k with .NET 5.
  - Pattern format is space-delimited hex with `??` wildcards.
  - API: `new Scanner(Process, ProcessModule)` or `new Scanner(byte[])`, then `FindPattern(string)` returns an offset. `FindPatterns(string[])` is multithreaded, and the author notes slowdown past about 16 patterns from cache misses.

  — [Reloaded.Memory.SigScan](https://github.com/Reloaded-Project/Reloaded.Memory.SigScan)
- **AobscanFast (NuGet)** uses a cascade of AVX-512 → AVX2 → SSE2 → scalar, with a pre-filter that "only verifies positions where the longest solid run matches." — [NuGet: AobscanFast](https://www.nuget.org/packages/AobscanFast)
- **The common design across SIMD scanners:** a vectorized first-byte or prefix pre-filter, then per-candidate wildcard verification.
  - pe-sigscan: SWAR first-byte filter, optional memchr AVX2/SSE2 path.
  - simdsieve: multi-pattern prefix filter on the first 1-4 bytes. Its README warns it degrades when patterns share long prefixes or differ only after byte 4.

  — [docs.rs: pe-sigscan](https://docs.rs/crate/pe-sigscan/0.4.0); [docs.rs: simdsieve](https://docs.rs/crate/simdsieve/0.1.3/source/README.md)
- **On-disk vs runtime scanning:** GH-Offset-Dumper supports `fileonly` mode with `exefile` ("supply path to dumped game") as well as runtime mode. Disk mode "does not support netvars", which are runtime-built structures. — [GH-Offset-Dumper README](https://raw.githubusercontent.com/guidedhacking/GH-Offset-Dumper/master/README.md)

### Inferences
- **Recommended external scan for this project [READ-ONLY]:**
  1. Find the module base (`Process.MainModule.BaseAddress`, or `0x00400000` if not relocated) and `SizeOfImage` from the in-memory PE header.
  2. Walk `VirtualQueryEx` from base to base+SizeOfImage. Keep only `MEM_COMMIT` regions whose `Protect` is readable and lacks `PAGE_GUARD` (0x100) and `PAGE_NOACCESS` (0x01). For code signatures, restrict to executable protections (`PAGE_EXECUTE_READ` 0x20 / `PAGE_EXECUTE_READWRITE` 0x40), which is effectively `.text` and roughly halves the work and false positives.
  3. Read each region in chunks of 1-4 MB, overlapping consecutive chunks by `patternLength - 1` bytes so a match straddling a chunk boundary is not lost.
  4. If a read fails with ERROR_PARTIAL_COPY (299), fall back to page-by-page (4 KB) reads for that region.
- **Images rarely need region walking.** Image (MEM_IMAGE) sections are normally all committed and readable, which is why LiveSplit's single whole-module read works. Region walking matters for heap and value scans (MEM_PRIVATE), and for protected or packed EXEs. The guard-page check matters because reading a guard page from another process does not trigger it, but `Protect` reports it, and some games use guard pages for stack probing.
- **Algorithm choice in .NET 8 (no third-party NuGet, matching this project's constraint):**
  - Pick the longest contiguous non-wildcard run in the pattern as the anchor.
  - Use `Span<byte>.IndexOf(ReadOnlySpan<byte> anchor)`, which is vectorized internally in modern .NET, to jump between candidates.
  - At each candidate, verify the full pattern with the mask, then continue from candidate+1.
  - This "longest solid run" approach (the AobscanFast design) is simple, allocation-free, and fast enough: a 20-40 MB `.text` scans in milliseconds.
  - Hand-rolled `Vector256<byte>` code is only worth it for heap-wide scans of hundreds of MB.
  - Boyer-Moore-Horspool with wildcards works, but its skip table degrades to a shift of 1 at any wildcard near the end of the pattern. An anchor-plus-verify design avoids that.
- **Batch scanning:** scan all signatures against the same buffer in one pass (read once, match many). The cross-process copy costs far more than the matching, so never re-read the module per signature. Cache the module bytes for a scan session.
- **Disk-file scanning [READ-ONLY, works with the game closed]:** parse section headers, scan raw `.text`, and convert hits with `rva = fileOff - PointerToRawData + VirtualAddress` and `VA = ImageBase + rva`.
  - Pros: deterministic bytes (no runtime hooks from overlays or other tools); can be validated offline before shipping.
  - Cons: if the EXE is packed or encrypted by a DRM wrapper (for example Steam's SteamStub), the on-disk `.text` is ciphertext and only the in-memory or dumped image works. That is why GH's tool asks for a "dumped game" path.
  - Absolute operands in the file hold ImageBase-relative VAs, which is correct when there is no relocation.
- **Concurrency correctness:** game code (`.text`) does not change while running, so tearing is not a concern for code scans. For data reads, RPM gives no snapshot guarantee: values can change mid-read. That is consistent with the project's existing practice of tolerating racy reads.

### Gaps
- No source confirmed whether Reloaded.Memory.SigScan's `Scanner(Process, ProcessModule)` reads a *foreign* process's module (external) or only the current process. The README example uses `Process.GetCurrentProcess()`.
- No authoritative blog post benchmarking .NET 8 `Span.IndexOf`/`Vector<T>` pattern scanning was found. The vectorized-IndexOf claim above is general .NET knowledge.
- The `PAGE_*` constant values quoted above (0x01, 0x20, 0x40, 0x100) are standard winnt.h values from general knowledge. The memory-protection-constants page was not fetched.
- Whether this particular game's Steam EXE is SteamStub-wrapped is unknown. Compare on-disk `.text` against in-memory `.text` to find out.

---

## Q4. Surviving game updates: fallback signatures, string anchors, fuzzy matching, offset dumpers, validation

### Takeaway
Community tools treat a signature as a recipe, not an address: pattern, operand offset, relative or absolute, dereference count, plus extra adjustment. They regenerate headers or offsets on each game update, either at runtime or from the EXE on disk. Robustness comes from layering:
- several independent signatures per target;
- anchors that change rarely (unique string references, exported or imported calls, engine globals);
- strict validation (exactly-one-match plus semantic checks) before trusting a result.

### Cited Findings
- **Offset dumpers:**
  - hazedumper is described as a tool that "scans for signatures and netvars and dumps their relative offsets". mistdumper is a static-analysis version "inspired by hazedumper which is a runtime signature finder." — [git.tdem.in hazedumper topic](https://git.tdem.in/topics/hazedumper); [mistdumper](https://github.com/trendingtechnology/mistdumper)
  - GH-Offset-Dumper takes a JSON config and emits `.hpp` (C++ header), `.ct` (Cheat Engine table) and `.rcnet` (ReClass.NET project). It supports scanning a main EXE plus `additionalModules` DLLs, works at runtime "without changing any of the code" or from disk, and supports relative-branch signatures. — [GH-Offset-Dumper README](https://raw.githubusercontent.com/guidedhacking/GH-Offset-Dumper/master/README.md)
- **XREF signatures:** SigMaker can generate signatures from data or code xrefs and pick the shortest. You can sign a call site that references the target instead of the target itself, which helps when the target function's own bytes are not unique or are likely to change. — [A200K/IDA-Pro-SigMaker](https://github.com/A200K/IDA-Pro-SigMaker)
- **Verify across builds:** AlliedModders recommends comparing the candidate function across builds (HL2:DM vs CS:S) to confirm it is the same function. It notes signatures may need regenerating per binary because "the code changes between versions." — [AlliedModders wiki](https://wiki.alliedmods.net/Signature_Scanning)
- **Static versus runtime-only data:** netvars, which are runtime-built structures, cannot be dumped from disk. Data built at runtime (reflection tables, name tables) needs runtime resolution, while code-derived offsets can be dumped statically. — [GH-Offset-Dumper README](https://raw.githubusercontent.com/guidedhacking/GH-Offset-Dumper/master/README.md)

### Inferences
These are general practice, not from a fetched source.
- **Multiple fallback signatures:** store 2-4 signatures per target, built at *different* sites. Examples:
  - one inside the function that reads the global;
  - one at a call site (XREF signature);
  - one on a different function that also references the same global.

  Accept the first signature that yields exactly one match *and* passes validation. If two signatures resolve to *different* addresses, treat that as a hard failure and fall back to the recorded last-known-good value, rather than guessing.
- **String-anchor technique [READ-ONLY]** (very effective on x86-32 because string addresses are embedded as `imm32`):
  1. Find a unique literal in `.rdata`. UE3 uses UTF-16LE `TEXT("...")` strings heavily, so search both ASCII and UTF-16.
  2. Compute its VA.
  3. Search `.text` for the 4-byte little-endian VA, typically after `68` (`push imm32`) or `B8..BF` (`mov r32, imm32`), or use `.reloc` if present.
  4. Walk back to the containing function start (scan backward to `CC`/`90` padding after a `C3`/`C2 iw` return), then take a fixed offset or apply a small local signature from there.

  Log/assert strings and config key names survive patches far better than instruction bytes.
- **Engine-level anchor for UE3 (strong recommendation for this target class):** UE3 keeps global arrays of all live objects and names (commonly called `GObjects`/`GObjObjects` and `GNames`). Find those two globals once by signature, then locate any object by *walking the object array and matching class and object names* rather than by a per-feature signature. Only two signatures then need to survive a patch, and everything else is resolved by reflection names, which change only if the content changes. This is general UE3 SDK-generator knowledge and was not fetched this session.
- **Function-level fuzzy matching (BinDiff/Diaphora concept):** these tools match functions between two builds by structural features (control-flow-graph shape, basic-block and call counts, referenced strings and constants, call-graph neighbors), not by exact bytes. Workflow: when a patch breaks every byte signature, diff old vs new EXE in IDA with Diaphora or BinDiff, map the known function to its new address, then regenerate signatures. This is an offline developer workflow, not something to ship in the tool.
- **Validation checklist before trusting a resolved address [READ-ONLY]:**
  1. Match count is exactly 1, using ScanAll and counting.
  2. The resolved global lies inside the module's `.data`/`.bss` range, and a resolved function lies inside `.text`.
  3. Dereferencing the global gives an address in a `MEM_COMMIT`/`MEM_PRIVATE` region.
  4. That object's first dword (vtable pointer) points into the module's `.rdata`.
  5. A semantic check passes. For UE3: the object's class name read via the name table equals the expected one, or a known field holds a value in a plausible range.
  6. On failure, keep the last-known-good recorded value and surface a warning rather than writing to an unvalidated address.
- **Practical relevance:** DD1 updates are rare to nonexistent now, so the main value of signatures here may be as a *self-check*. That means confirming the recorded static addresses still hold the expected code bytes before writing, which detects a different build (for example a Steam beta or a modded EXE) and refuses to run, instead of silently writing to the wrong place.

### Gaps
- No fetched source documented Il2CppDumper's validation logic, or hazedumper's exact config semantics and validation.
- BinDiff/Diaphora feature lists were not fetched; the description above is from general knowledge.
- No source was found that specifically documents the string-xref anchoring technique; the search returned nothing relevant. It is described here from general reverse-engineering practice.
- No dated Guided Hacking or UnknownCheats thread on "update-proof signatures" was retrieved.

---

## Q5. Capturing addresses dynamically: debug registers, page guards/VEH, external debugging, code caves

### Takeaway
Two families exist. Choose the cheapest one that works, in this order:
1. **[READ-ONLY]** Resolve a static root by signature and follow pointers. Prefer this whenever the value is reachable from a global.
2. **[PATCH]** A code-cave "pointer grabber": overwrite at least 5 bytes at an instruction that has the object in a register with `jmp cave`. The cave stores the register into a slot (`mov [slot], esi`), runs the displaced original instructions, and jumps back. The external tool then reads the slot.
3. **[DEBUG]** Hardware data breakpoints (DR0-DR3/DR7 via SetThreadContext on every thread) or PAGE_GUARD/no-access page tricks. These are what Cheat Engine's "find out what accesses/writes this address" uses. Externally they need a debugger attachment (DebugActiveProcess plus a WaitForDebugEvent loop), because otherwise the resulting single-step or guard exception goes to the game and crashes it. They are best used as a developer-time discovery tool, not a shipped runtime mechanism.

### Cited Findings
- **DebugActiveProcess (Microsoft Learn, updated 2024-02-22):**
  - The debugger "must be able to open the process for PROCESS_ALL_ACCESS" (or hold SE_DEBUG_NAME).
  - After attach, "the system suspends all threads in the process, and sends the debugger events that represent the current state": one CREATE_PROCESS_DEBUG_EVENT, a CREATE_THREAD_DEBUG_EVENT per thread, and a LOAD_DLL_DEBUG_EVENT per DLL. Then "the first thread in the process... executes a breakpoint instruction that causes an EXCEPTION_DEBUG_EVENT."
  - **"Exiting the debugger also exits the process unless you use the DebugSetProcessKillOnExit function"**; detach with DebugActiveProcessStop.

  — [Microsoft Learn: DebugActiveProcess](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-debugactiveprocess)
- **SetThreadContext (Microsoft Learn, 2025-06-13):**
  - The handle needs `THREAD_SET_CONTEXT`. `ContextFlags` selects which parts are set.
  - Some values "are silently set to the correct value. This includes... global enabling bits in the debugging register." So only local enable bits are honored from user mode.
  - **"Do not try to set the context for a running thread; the results are unpredictable. Use the SuspendThread function to suspend the thread before calling SetThreadContext."**
  - The thread is "typically being debugged, but the function can also operate even when the thread is not being debugged."
  - "A 64-bit application can set the context of a WOW64 thread using the Wow64SetThreadContext function."

  — [Microsoft Learn: SetThreadContext](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadcontext)
- **Cheat Engine's watchpoints:** Cheat Engine uses hardware breakpoints for its watchpoint feature. DR0-DR3 each hold a watched address, DR6 reports which breakpoint fired, and DR7 controls them. CE was observed using *local* breakpoints (DR7 bit 0). Hardware breakpoints are "the fastest and cleanest approach" but "not the only way." This is from search-result summaries of Lonami's "Writing our own Cheat Engine" Rust series; the full page did not render on fetch. — [Lonami: Writing our own Cheat Engine (Pointers)](https://lonami.dev/blog/woce-6); [mirror of woce-5](https://git.birabittoh.duckdns.org/gemini-redirect.git/blob/1cbe681dd6dce46d52cfdd93fea315e84be80b33/blog/woce-5/index.html)
- **Where the feature lives in CE:** the Cheat Engine help file lists "Find out what writes/accesses this address" and "Find out what addresses this instruction accesses" under Debugging (wiki help, last edited 6 May 2024, via a Scribd copy). A Spanish CE 6.3-era guide describes three debugger interfaces: Windows debugger, VEH debugger, and a kernel-mode debugger. Its claim that VEH is "practically undetectable" is the author's opinion. — [Scribd: CE Help File](https://www.scribd.com/document/902861753/Cheat-Engine-Help-File-Cheat-Engine); [Tutorial Cheat Engine Avanzado (PDF)](https://ns2.elhacker.net/Tutorial_Cheat_Engine_Avanzado.pdf)
- **CE Auto Assembler:**
  - `aobScanModule(SymbolName, ModuleName, AOBString)` "scans the memory used by the module" and defines `SymbolName` at the match; `$process` can stand in for the main module. — [CE wiki: aobScanModule](https://wiki.cheatengine.org/index.php?title=Auto_Assembler:aobScanModule)
  - CE 7.2 release notes: a 64-bit AOB injection script that `alloc`s without a preferred base now warns "the jump will be 14 bytes", and `unregistersymbol(*)` was added. — [Cheat Engine 7.2 release (Patreon)](https://www.patreon.com/cheatengine/posts/cheat-engine-7-2-35792319)
  - CE generates injection scripts from the disassembler via "template" → "full injection". — [CE wiki: Injection full tutorial](https://wiki.cheatengine.org/index.php?title=Tutorials:Auto_Assembler:Injection_full)
- **Why patching live code is dangerous (Raymond Chen, 2011-09-21):** `MOV EDI,EDI` exists as a 2-byte hot-patch point because patching more than one instruction risks a thread "in the middle of executing an instruction that overlaps the first five bytes of the function." With two 1-byte NOPs, a thread that had executed the first NOP would treat "the second half of your `JMP $-5` as the start of a new instruction." — [The Old New Thing: Why do Windows functions all begin with a pointless MOV EDI, EDI instruction?](https://devblogs.microsoft.com/oldnewthing/20110921-00/?p=9583); x64 follow-up (2022-11-09): [The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20221109-00/?p=107373)

### Inferences
These are general Windows and x86 knowledge plus reasoning from the cited docs. Exact bit layouts are in Gaps for verification.

**A. Code-cave pointer grabber [PATCH]: the most practical dynamic-capture technique for an external x86 tool.**
- **Shape**, reconstructed from general knowledge of CE's "AOB injection" template; not quoted from a fetched source:
  ```
  [ENABLE]
  aobscanmodule(INJECT,Game.exe,8B 86 ?? ?? ?? ?? 89 45 FC)   // must be unique
  alloc(newmem,$1000)            // x86: no "near" requirement; rel32 reaches all 4 GB
  label(code)
  label(return)
  label(pObj)
  registersymbol(INJECT)
  registersymbol(pObj)
  newmem:
    mov [pObj],esi               // capture the object pointer the game is using
  code:
    mov eax,[esi+000001F4]       // displaced original instruction(s), byte-exact
    jmp return
  pObj:
    dd 0
  INJECT:
    jmp newmem                   // E9 rel32 = 5 bytes
    nop                          // pad to the end of the last overwritten instruction
  return:
  [DISABLE]
  INJECT:
    db 8B 86 F4 01 00 00         // restore original bytes
  unregistersymbol(INJECT)
  unregistersymbol(pObj)
  dealloc(newmem)
  ```
- **The same thing from C# (external):**
  1. Scan for the hook site (READ-ONLY part).
  2. Read and save the original bytes.
  3. `VirtualAllocEx(MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE)` for the cave plus a 4-byte slot.
  4. Assemble the cave: `89 35 <slot32>` (mov [slot],esi; the ModRM byte differs per register), the displaced instructions, then `E9 <rel32 back to site+len>`.
  5. Write the cave with WriteProcessMemory, then `FlushInstructionCache`.
  6. **Last:** `VirtualProtectEx` the site to RWX, write `E9 <rel32 to cave>` plus NOP padding to the instruction boundary, restore the protection, and FlushInstructionCache.
  7. Poll the slot with ReadProcessMemory on the existing 50 ms timer.
- **Risks and mitigations:**
  - **Instruction-boundary correctness.** The overwritten length must be at least 5 and end exactly on an instruction boundary. Displaced instructions must be copied byte-exact. Any displaced instruction with a *relative* operand (E8/E9/Jcc rel, or a `jmp` target inside the overwritten range) must be re-encoded for its new location or the hook moved. Hook only at sites whose displaced instructions are position-independent, such as `mov r32,[r32+disp]`.
  - **Atomicity and threads (Chen's "Detours problem").** A 5-byte WriteProcessMemory is not atomic with respect to executing threads, and a thread may be paused *inside* the overwritten range.
    - Safest: SuspendThread every game thread, GetThreadContext each one, and if any thread's EIP is in `[site+1, site+len)`, resume it briefly and retry. Then write, flush, and resume all threads.
    - Cheaper: patch while the game is in a state where that code is not running (menu or loading).
  - **Disabling.** Restore the original bytes first. Do **not** free the cave immediately, since a thread may be executing inside it. Leak it, or free it after a delay and only after confirming no thread EIP is in the cave.
  - **Register liveness.** The cave must preserve flags and registers. A bare `mov [abs],reg` changes neither, while anything more complex should wrap in `pushfd/pushad ... popad/popfd`.
  - **Stale pointers.** The captured pointer is only as fresh as the last time the hooked instruction ran. After a level change the slot can hold a freed object, so validate it (vtable in `.rdata`, class name) before every write, which the project already does for racy reads. Clear the slot on map change.
  - **Tool crash or exit.** The cave lives in the game's address space, so it keeps working, and stays harmless, if the tool exits without unhooking. The next tool start should detect the existing `E9` at the site (the saved original bytes no longer match) and adopt or restore it, not hook it twice.
  - **Shared code.** If the hooked instruction runs for many objects (for example a per-actor tick), the slot holds "whichever ran last." Either filter in the cave (`cmp [esi+classOff], expectedClass` / `jne skip`) or capture into a small ring buffer.
- **Risk level:** moderate. Crashes come almost entirely from wrong boundaries, relative operands in displaced bytes, or freeing the cave too early. All of these are deterministic and testable in-game.

**B. Hardware data breakpoints [DEBUG].**
- **Mechanism:**
  1. For every thread in the game (`CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD)`), `OpenThread(THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_SUSPEND_RESUME)`, then SuspendThread.
  2. GetThreadContext with `CONTEXT_DEBUG_REGISTERS`, set `Dr0 = targetAddress` and the DR7 local-enable, RW and LEN bits, then SetThreadContext and ResumeThread. Threads created later need the same treatment, which a debugger learns about from CREATE_THREAD_DEBUG_EVENT.
  3. When the CPU hits a data breakpoint it raises a single-step exception *after* the accessing instruction executes. The reported EIP is the *next* instruction, so the tool must disassemble backward or use the previous instruction to identify the accessor. Registers in the context at that moment hold the base pointer, for example `esi` in `mov [esi+1F4],eax`.
- **External requirement:** without an attached debugger or an in-process VEH handler, that exception is delivered to the game's own SEH chain and will almost certainly be unhandled, crashing the game.
  - An external tool must therefore DebugActiveProcess, run a WaitForDebugEvent/ContinueDebugEvent loop, and on EXCEPTION_SINGLE_STEP read the context and continue with `DBG_CONTINUE`.
  - Call `DebugSetProcessKillOnExit(FALSE)` immediately after attaching, or a tool crash kills the game (per the doc quote above).
  - Clear DR7 on all threads and call DebugActiveProcessStop when done.
- **Bitness:** an x86 tool debugging an x86 game uses plain SetThreadContext. Wow64SetThreadContext only matters for a 64-bit debugger.
- **Costs:**
  - Only 4 addresses can be watched.
  - Every hit is a kernel round-trip that stalls the faulting thread. A per-frame access can drop the game to single-digit FPS while the breakpoint is armed.
  - `IsDebuggerPresent` becomes true. That is fine here, since this game's anti-cheat only checks process names, but other titles react to it.
  - If the tool hangs while attached, the game hangs.
- **Recommendation:** a *developer-time discovery* tool (find which instruction touches a value, then build a signature for it) rather than a shipped runtime path.

**C. Page-guard / no-access tracing [DEBUG].**
- **Mechanism:** VirtualProtectEx the 4 KB page holding the value with `PAGE_GUARD` (one-shot, cleared on first touch) or `PAGE_NOACCESS`. Every access to *any* address in that page faults, and the handler filters for the exact address, single-steps, and re-arms the protection.
- **Trade-off:** no 4-address limit, but very slow. Heap pages are shared with many other hot objects.
- **Danger:** if the tool dies while a page is NOACCESS, the next game access is a fatal access violation. Highest crash risk of all the options.
- **Requirement:** like B, it needs a debugger loop externally, or an injected VEH. CE's "VEH debugger" option installs a vectored exception handler *inside* the target, which requires DLL injection and is out of scope for a pure-external tool.

**D. Choosing among them for an external x86 tool against UE3:**
- First choice [READ-ONLY]: signature → global (GObjects / an engine singleton / the world or player-controller global) → pointer chain, or walk the object array by class name.
- Second [PATCH]: a code-cave grabber, only for values reachable solely through a register.
- Third [DEBUG]: hardware breakpoints, at development time only, to discover the instruction to sign or hook.

### Gaps
- **DR7 bit layout, from Intel SDM Vol. 3 Ch. 17 general knowledge, not fetched; verify before implementing:**
  - L0-L3 local enables at bits 0, 2, 4, 6.
  - R/W0 at bits 16-17 (00 execute, 01 write, 10 I/O, 11 read/write), LEN0 at bits 18-19 (00 = 1 byte, 01 = 2, 11 = 4, 10 = 8 on x64 only).
  - Each subsequent breakpoint's R/W and LEN fields are shifted by 4 bits.
  - Data breakpoints are trap-class, reported after the instruction executes.
- The full text of CE's generated "AOB injection" / "full injection" template was not retrieved. The CE wiki tutorial page describes it only in prose, and the community example repo required login. The template above is a reconstruction.
- No fetched source detailed CE's VEH or page-exception debugger internals.
- No Microsoft source was fetched for `FlushInstructionCache`, `DebugSetProcessKillOnExit`, or `VirtualAllocEx`. Their use above follows standard practice.
- No source quantified the frame-rate cost of debug-event round trips. "Single-digit FPS" is an expectation from the mechanism, not a measured figure.

---

## Q6. Practical C#/.NET implementations

### Takeaway
LiveSplit's `SignatureScanner`/`SigScanTarget` (MIT, battle-tested on hundreds of autosplitters against old 32-bit games) is the best reference design for an external, read-only .NET scanner: whole-module read, `??` wildcards, offset, `OnFound` resolver callback, and `ScanAll` for uniqueness checks. Reloaded.Memory.SigScan and AobscanFast show the SIMD ceiling. Given this project's "no third-party NuGet" rule, the practical path is to re-implement a small LiveSplit-style scanner on `Span<byte>` inside the existing scanner class.

### Cited Findings
- **LiveSplit's API:**
  - Constructors: `SignatureScanner(Process proc, IntPtr addr, int size)` and `SignatureScanner(byte[] mem)`.
  - Targets: `SigScanTarget(int offset, params string[] signature)` with an `OnFound` delegate of type `IntPtr OnFoundCallback(Process, SignatureScanner, IntPtr)`.
  - Scanning: `Scan(target, align)` and `ScanAll(target, align = 1)`. — [LiveSplit SignatureScanner.cs](https://raw.githubusercontent.com/LiveSplit/LiveSplit/master/src/LiveSplit.Core/ComponentUtil/SignatureScanner.cs)
- **Reloaded.Memory.SigScan's API:** `Scanner` with `FindPattern`/`FindPatterns`/`FindPatternsCached`, `??` wildcards, and automatic selection of the fastest available method. Benchmarks are given in Q3. — [Reloaded.Memory.SigScan](https://github.com/Reloaded-Project/Reloaded.Memory.SigScan)
- **AobscanFast:** a .NET AOB scanner with an AVX-512/AVX2/SSE2/scalar cascade and a longest-solid-run pre-filter. — [NuGet: AobscanFast](https://www.nuget.org/packages/AobscanFast)
- **GH-Offset-Dumper:** a JSON-driven external dumper (C++) whose config schema (`pattern`, `opLoc`, `opLength`, `rva`, `relativeByDefault`, `module`, `fileonly`) is a good model for a data-driven signature table. — [GH-Offset-Dumper README](https://raw.githubusercontent.com/guidedhacking/GH-Offset-Dumper/master/README.md)

### Inferences
- **A minimal design that fits this project** (backend-only, no WPF, no NuGet):
  - A `SigTarget` record: `Name`, `Pattern`, `OperandOffset`, `Kind` (Absolute32 | Rel32 | None), `ExtraDerefs`, `PostAdd`, `Validator`.
  - A static `SigScan.FindUnique(byte[] moduleBytes, uint moduleBase, SigTarget t)` that:
    1. parses the pattern into `byte[] bytes` + `bool[] mask`;
    2. anchors on the longest solid run via `Span.IndexOf`;
    3. verifies the mask;
    4. requires exactly one hit;
    5. resolves `Absolute32` → `BitConverter.ToUInt32(bytes, hit + off)`, or `Rel32` → `moduleBase + hit + off + 4 + ToInt32(...)`, both computed against the module-relative buffer;
    6. runs the validator through the existing scanner's read path.
  - Results should feed into, and be cross-checked against, the recorded last-known-good addresses rather than replacing them blindly.
- **Use x86 resolution, not x64.** Most C# examples online (including the illustrative RIP-relative snippet commonly shown with LiveSplit) assume x64. For this target, operands are absolute VAs, so plain reads with no `nextIP + disp` arithmetic, except for E8/E9/Jcc.
- **Libraries not verified this session:** memory.dll (erfg12) also offers external `AoBScan` in C#, but its source was not reviewed. It is commonly reported to scan all regions by default and to be slower than an image-only `.text` scan.

### Gaps
- memory.dll, Reloaded.Memory (the non-SigScan package) and Reloaded-II hooking (Reloaded.Hooks) were not fetched. Reloaded.Hooks is in-process only, as far as known, so it is not applicable to an external tool, but this was not verified.
- No C# example of an *external* code-cave hook (VirtualAllocEx + WriteProcessMemory + assembled bytes) was retrieved from a primary source. Such code would have to be written from the mechanics in Q5.
