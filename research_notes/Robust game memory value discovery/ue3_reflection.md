# UE3 Runtime Reflection & Global Object Tables (GObjects / GNames) for External, Self-Healing Offset Resolution

Source-quality note (applies to the whole file):
- **Generator/SDK source code** (TheFeckless UE3 SDK Generator mirror, CodeRed-Generator, bl-sdk/unrealsdk, a Dungeon-Defenders-specific SDK dumper) is the primary evidence: it is code that ran against real 32-bit UE3 games.
- **"UE3 2013 source" mirror** (`CodeRedModding/UnrealEngine3`, self-described as "Full source from Unreal Engine 3 2013 (Build 10897)") is used to quote member order, flag values, and script declarations. It is a third-party public mirror of Epic's licensee-only code, it is not an official release, and it is about 2 years newer than DD1. Treat its member *order* as authoritative for that build. Exact *offsets* for DD1 must be confirmed at runtime.
- I did **not** read any local project files. Every DD1-specific claim below comes from public GitHub repositories.

---

## Q1. UE3 32-bit memory layouts (TArray, FName, FNameEntry, UObject, UField, UStruct, UClass, UProperty and subclasses) and how they vary across builds/licensees

### Takeaway
The UObject header is very stable across 2009–2013 32-bit PC UE3 builds. It is 0x3C bytes, with **Outer @0x28, Name @0x2C (FName = 8 bytes), Class @0x34, ObjectArchetype @0x38**, and ObjectFlags (QWORD) @0x08. Two things vary by build and licensee:
- **Field order inside 0x04–0x27**: ObjectInternalInteger sits @0x20 in UDK-era builds but @0x04 in the older order the DD1 dumper uses.
- **Where `SuperField` lives**: in `UField` or in `UStruct`.

The `SuperField` placement shifts every UProperty field, including **UProperty::Offset**, which is @0x60 in BL2/SMNC-style builds and @0x64 in the DD1 dumper. The only robust approach is to calibrate these few reflection offsets at runtime against invariants. See Q4.

### Cited Findings

**TArray / FString / FName (identical across all sources)**
- `TArray<T>` = `{ T* Data; int32 Count; int32 Max; }` = 12 bytes on 32-bit. `FString` = `TArray<wchar_t>`, and Count includes the terminating null. The DD-specific dumper's `ToString()` uses `Count - 1` — [DD SDK Dumper EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp); [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- `FName` = `{ int32 Index; int32 Number; }` (8 bytes) — [DD SDK Dumper EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp); UE3 source `NAME_INDEX Index; INT Number;` — [UnName.h L565-568](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L565)
- In the UE3 2013 source, `Number` is stored **+1** internally: `NAME_NO_NUMBER_INTERNAL 0`, `NAME_INTERNAL_TO_EXTERNAL(x) (x - 1)`. Internal Number 0 means "no suffix", and internal N>0 displays as `_<N-1>` — [UnName.h L16-26](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L16). The DD dumper instead appends `'_' + Number` with no -1 — [DD SDK Dumper GenericTypes.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/GenericTypes.cpp). The two disagree, so the encoding is build-dependent or the dumper is sloppy. Verify it at runtime.
- The global name table type is `static TArrayNoInit<FNameEntry*> Names;` — a flat TArray of FNameEntry pointers in UE3, not chunked like UE4 — [UnName.h L572](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L572). BL2 also uses `TArray<bl2::FNameEntry*>* gnames_ptr` — [unrealsdk bl2 globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)

**FNameEntry (32-bit)**
- UE3 source order is `EObjectFlags Flags; NAME_INDEX Index; FNameEntry* HashNext; union { ANSICHAR AnsiName[NAME_SIZE]; TCHAR UniName[NAME_SIZE]; }` with `NAME_SIZE = 1024`, `NAME_UNICODE_MASK 0x1`, `NAME_INDEX_SHIFT 1`. `IsUnicode()` is `Index & NAME_UNICODE_MASK`, and the real index is `Index >> 1` — [UnName.h L11, L68-139](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L68)
- That gives 32-bit offsets of Flags (QWORD) @0x00, Index @0x08, HashNext @0x0C, and **string @0x10**: [CodeRed-Generator Template GameDefines.hpp L741-748](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L741). In BL2 the string is also @0x10 and the wide/ANSI choice is `(entry->Index & NAME_WIDE_MASK) != 0` — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h), [bl2 globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp), [unrealsdk gnames.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/unreal/structs/gnames.h)
- **DD1**: the DD dumper reads the ANSI name at **+0x10** (`char pad[0x10]; char Name[...]`) — [DD NamesStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/NamesStore.cpp). TheFeckless's generator uses the same `UnknownData00[0x10]; char Name[]` — [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- The hardcoded names have fixed indices: `0 None, 1 ByteProperty, 2 IntProperty, 3 BoolProperty, 4 FloatProperty, 5 ObjectProperty, 6 NameProperty, 7 DelegateProperty, 8 ClassProperty, 9 ArrayProperty, 10 StructProperty, 11 VectorProperty, 12 RotatorProperty, 13 StrProperty, 14 MapProperty, 15 InterfaceProperty, 20 Core, 21 Engine` — [UnNames.h L30-51](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnNames.h#L30)

**UObject header (32-bit PC), standard / UDK-era order**

| Off | Field | Notes |
|---|---|---|
| 0x00 | VfTableObject | vtable ptr |
| 0x04 | HashNext | |
| 0x08 | ObjectFlags (QWORD) | low dword @0x08, high dword @0x0C |
| 0x10 | HashOuterNext | |
| 0x14 | StateFrame | |
| 0x18 | _Linker | |
| 0x1C | _LinkerIndex | |
| 0x20 | Index / ObjectInternalInteger | "Index of object into GObjObjects array" |
| 0x24 | NetIndex | |
| 0x28 | Outer | |
| 0x2C | Name (FName, 8 bytes) | |
| 0x34 | Class | |
| 0x38 | ObjectArchetype | sizeof = 0x3C |

- Order source: [UnObjBas.h ~L1098-1141](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1098). The same fields in the same order are in BL2's `generic::UObject` (vftable, HashNext, `uint64_t ObjectFlags`, HashOuterNext, StateFrame, _Linker, _LinkerIndex, InternalIndex, NetIndex, Outer, Name, Class, ObjectArchetype) — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h). TFL/Super Monday Night Combat has `ObjectInternalInteger // 0x0020`, `Outer // 0x0028`, `Name // 0x002C`, `Class // 0x0034`, `ObjectArchetype // 0x0038`, and class range `(0x0000 - 0x003C)` — [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- **The header is also exposed to script reflection.** `Core.Object` declares `pointer VfTableObject; pointer HashNext; qword ObjectFlags; // This needs to be 8-byte aligned in 32-bit!; pointer HashOuterNext; pointer StateFrame; Object Linker; pointer LinkerIndex; int ObjectInternalInteger; int NetIndex; Object Outer; name Name; class Class; Object ObjectArchetype;` as native vars. So each of these has a UProperty whose `Offset` is that field's real offset — [Object.uc L32-44](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Classes/Object.uc#L32)
- **DD1 dumper's UObject**: `VTableObject; uint32 InternalIndex; UnknownData00[0x20]; Outer; Name; Class; ObjectArchetype` → InternalIndex @**0x04**, Outer @0x28, Name @0x2C, Class @0x34, ObjectArchetype @0x38 (commented "Looks good.") — [DD EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp). Outer/Name/Class/Archetype agree with every other source. Only the Index position differs.
- Both the DD and BL2 GObjects signatures contain `8B 40 ?? / 25 00 02 00 00`, which unrealsdk annotates as `mov eax,[eax+08]` / `and eax,00000200`. That confirms ObjectFlags @0x08 and a test of `RF_ClassDefaultObject` (0x200) — [unrealsdk bl2 globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp); [DD ObjectsStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/ObjectsStore.cpp)

**EObjectFlags (QWORD) values useful for filtering**
- `RF_ClassDefaultObject 0x200`, `RF_ArchetypeObject 0x400`, `RF_RootSet 0x4000`, `RF_BeginDestroyed 0x8000`, `RF_FinishDestroyed 0x10000`, `RF_Unreachable 0x0000000200000000`, `RF_Public 0x0000000400000000`, `RF_TagGarbage 0x0000004000000000`, `RF_DisregardForGC 0x0000008000000000`, `RF_Transient 0x0000400000000000`, `RF_Native 0x0400000000000000`, `RF_PendingKill 0x2000000000000000` — [CodeRed Template GameDefines.hpp L129-186](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L129); `RF_PendingKill DECLARE_UINT64(0x2000000000000000)` — [UnObjBas.h L376](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L376)
- The CDO name prefix is `#define DEFAULT_OBJECT_PREFIX TEXT("Default__")` — [UnObjBas.h L588](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L588)

**UField / UStruct (the main source of variance)**
- UE3 2013 source: `UField { UField* Next; }` and `UStruct { #if !CONSOLE UTextBuffer* ScriptText; UTextBuffer* CppText; #endif UStruct* SuperStruct; UField* Children; INT PropertiesSize; TArray<BYTE> Script; ... INT MinAlignment; UProperty* RefLink; UProperty* PropertyLink; UProperty* ConstructorLink; ...}` — [UnClass.h L84, L175-196](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnClass.h#L175). On PC that gives Next @0x3C, ScriptText @0x40, CppText @0x44, **SuperField @0x48, Children @0x4C, PropertiesSize @0x50**.
- TFL/SMNC matches: `UField::Next // 0x003C`, `UStruct: UnknownData00[0x8] // 0x0040, SuperField // 0x0048, Children // 0x004C, PropertySize // 0x0050` — [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h). BL2 matches as well (UnknownData00[0x8], SuperField, Children, `uint16_t PropertySize`, …, `PropertyLink`) — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)
- CodeRed-Generator says outright: `UField::SuperField ... [SUPERFIELD CAN EITHER BE HERE, OR IN USTRUCT DEPENDING ON THE GAME!]`, and on `UStruct::SuperField` `[... COMMENT OUT ACCORDINGLY!]` — [CodeRed Template GameDefines.hpp L1189, L1276](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L1185)
- **DD1 dumper (the structs it actually dereferences)**: `UField { UField* SuperField; UField* Next; }` (SuperField @0x3C, Next @0x40), `UStruct { char UnknownData00[0x08]; UField* Children; uint32 PropertySize; char UnknownData01[0x3C]; }` (Children @0x4C, PropertySize @0x50) — [DD EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp). **Conflict**: the same repo's `Generator.cpp` *emits* `Core.Field { UField* Next }` and `Core.Struct { UnknownData00[0x08]; SuperField; Children; PropertySize; UnknownData01[0x48] }`, which is the standard layout — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp). Both layouts put Children @0x4C and PropertySize @0x50.

**UProperty and subclasses**
- UE3 2013 source: `UProperty : UField { INT ArrayDim; INT ElementSize; QWORD PropertyFlags; WORD RepOffset; WORD RepIndex; #if !CONSOLE FName Category; UEnum* ArraySizeEnum; #endif INT Offset; UProperty* PropertyLinkNext; UProperty* ConstructorLinkNext; UProperty* NextRef; ...}` — [UnType.h ~L134-161](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnType.h#L134). With UField = 0x40 on PC this is ArrayDim @0x40, ElementSize @0x44, PropertyFlags @0x48, RepOffset @0x50, RepIndex @0x52, Category @0x54, ArraySizeEnum @0x5C, **Offset @0x60**.
- Subclass members: `UByteProperty::Enum`, `UBoolProperty: BITFIELD BitMask`, `UObjectProperty: UClass* PropertyClass`, `UClassProperty: UClass* MetaClass`, `UStructProperty: UScriptStruct* Struct`, `UArrayProperty: UProperty* Inner`, `UMapProperty: Key, Value` — [UnType.h L612, L700, L789, L938, L1134, L1238, L1358](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnType.h#L700)
- TFL/SMNC: `ArrayDim 0x40, ElementSize 0x44, PropertyFlags (FQWord) 0x48, PropertySize 0x50, Offset 0x60`, UProperty range `(0x0040 - 0x007C)`, so `UBoolProperty::BitMask`, `UObjectProperty::PropertyClass`, `UByteProperty::Enum` etc. are @**0x7C** — [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- BL2: `ArrayDim, ElementSize, uint32 PropertyFlags, UnknownData00[0x14], Offset_Internal, PropertyLinkNext, UnknownData01[0x18]`. That puts Offset @0x60 and sizeof = 0x80, so `ZBoolProperty::FieldMask` is @0x80 — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)
- **DD1 dumper**: `UProperty { ArrayDim; ElementSize; PropertyFlags /* "Not sure about where the fuck this should be." */; PropertySize; UnknownData00[0x10]; Offset; UnknownData01[0x1C]; }`. With its 0x44-byte UField this gives ArrayDim @0x44, ElementSize @0x48, PropertyFlags @0x4C, **Offset @0x64**, sizeof 0x84. All subclass pointers (`BitMask`, `PropertyClass`, `Struct`, `Inner`, `Enum`) are therefore @**0x84** — [DD EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp)
- CodeRed's template uses `ArrayDim, ElementSize, uint64 PropertyFlags, Offset` and a `REGISTER_MEMBER` macro so per-game offsets are set in one place. Its README says the macro "computes offsets and unknown data automatically" — [CodeRed-Generator README](https://github.com/CodeRedModding/CodeRed-Generator); [Template GameDefines.hpp L1249-1255](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L1249)

**UFunction / UClass (only needed for CDO / function work)**
- UE3 source UFunction: `DWORD FunctionFlags; WORD iNative; WORD RepOffset; FName FriendlyName; BYTE OperPrecedence; BYTE NumParms; WORD ParmsSize; WORD ReturnValueOffset;` and UClass contains `ClassFlags, ClassCastFlags, ClassUnique, ClassWithin, ClassConfigName, ..., UObject* ClassDefaultObject` — [UnClass.h L384-398, L638-659](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnClass.h#L384)
- The DD dumper emits `Core.Class { UnknownData00[0x88]; UObject* ClassDefaultObject; UnknownData01[0x70] }` relative to the end of UState — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp). BL2 has `UClass: UnknownData00[0xCC]; ClassDefaultObject` — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)

### Inferences
- **The DD1 dumper's internal structs are self-consistent with an older UE3 layout** where UField = `{SuperField, Next}` (0x44 bytes). That layout gives Offset = 0x64, which is exactly 4 more than the standard 0x60. Children/PropertySize then land on the same 0x4C/0x50 because ScriptText/CppText (8 bytes) fill 0x44–0x4B. The dumper dereferences these structs to produce its SDK, while Generator.cpp's emitted Core.Field/Core.Struct are just printed text. So **the most likely DD1 values are SuperField @0x3C, Next @0x40, Children @0x4C, PropertySize @0x50, UProperty::Offset @0x64, and subclass pointer @0x84**. Treat this as a hypothesis to verify at runtime (Q4), not as fact.
- DD1's InternalIndex @0x04 (dumper) versus @0x20 (UDK order) is plausibly the older-engine order: Index, ObjectFlags, HashNext, … with Outer still @0x28. One runtime check settles it: `ReadInt32(GObjects.Data[i] + X) == i` for many i (UE3 `Index` is literally "Index of object into GObjObjects array").
- Because `ObjectFlags` is a QWORD on 32-bit, `RF_PendingKill` (bit 61) is bit 0x20000000 of the **high dword @0x0C**. `RF_Unreachable` (bit 33) is 0x2 in the high dword. `RF_ClassDefaultObject` (0x200) is in the low dword @0x08.
- `UStruct::PropertySize` is 4 bytes in the UE3 source but `uint16` in BL2. Read it as UInt16 when using it as a sanity bound.

### Gaps
- No primary source gives DD1's exact UE3 engine version (licensee build number). UEViewer confirms DD1 is a licensee-modified package format (see Q6), but not the in-memory layout.
- The FName.Number encoding (+1 internal vs raw) for DD1 is unconfirmed (dumper vs 2013 source disagree).
- I found no published, independently verified DD1 UProperty/UField offsets beyond the single dumper repo. That repo's README is empty and it has no issue tracker history to show it was validated.

---

## Q2. How GObjects and GNames are located in a UE3 binary (signatures, string xrefs, heuristics, validation)

### Takeaway
32-bit UE3 games are located with two classic signatures. Both read a `mov reg, [imm32]` whose imm32 is the **address of the static TArray** (Data/Count/Max), so you read 4 bytes at match+2 (or +1 for the `A1` form) to get it.
- **GObjects**: `8B ?? [imm32] 8B 04 ?? 8B 40 ?? 25 00 02 00 00`
- **GNames**: `8B 0D [imm32] 83 3C 81 00 74`

These exact signatures are published **for DunDefGame.exe** by a DD1 dumper. Validation is cheap: GNames[0] == "None", the hardcoded names follow at fixed indices, and GObjects entries satisfy `obj->Index == i`.

### Cited Findings
- **DD1 GNames sig** (searched in `GetModuleHandleW(L"DunDefGame.exe")`): pattern `\x8B\x0D\x00\x00\x00\x00\x83\x3C\x81\x00\x74`, mask `xx????xxxxx`, then `GlobalNames = *(uint32_t*)(address + 2)` as `TArray<FNameEntry*>*` — [DD NamesStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/NamesStore.cpp)
- **DD1 GObjects sig**: pattern `\x8B\x00\x00\x00\x00\x00\x8B\x04\x00\x8B\x40\x00\x25\x00\x02\x00\x00`, mask `x?????xx?xx?xxxxx`, then `GlobalObjects = *(uint32_t*)(address + 2)` as `TArray<UObject*>*` — [DD ObjectsStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/ObjectsStore.cpp)
- These match TheFeckless's generic UE3 sigs. The commented-out originals are `8B ?? ?? ?? ?? ?? 8B 04 ?? 8B 40 ?? 25 00 02 00 00` (GObjects) and `8B 0D ?? ?? ?? ?? 83 3C 81 00 74` (GNames, offset 0x2). The active RL variant is `A1 ???????? 8B ?? ?? 8B ?? ?? 25 00 02 00 00` (GObjects, offset 0x1) — [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- BL2's annotated GObjects sig: `"8B 0D {????????}" // mov ecx,[Borderlands2.exe+1682BD0]`, `"8B 04 ??" // mov eax,[ecx+esi*4]`, `"8B 40 ??" // mov eax,[eax+08]`, `"25 00020000" // and eax,00000200`. BL2 uses a *different* GNames sig (`A3 {????????} 8B 45 ?? 89 03` = `mov [GNames],eax`). The GNames sig is less portable across licensees — [unrealsdk bl2 globals.cpp](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/globals.cpp)
- CodeRed-Generator supports either hardcoded offsets or patterns for GObjects/GNames, configured in `Configuration.cpp` (`m_useOffsets`, `m_gobjectOffset`, `m_gobjectPattern`, `m_gnameOffset`, `m_gnamePattern`) — [CodeRed-Generator README](https://github.com/CodeRedModding/CodeRed-Generator)
- **String-xref route**: UE3's object validity check logs `TEXT("Invalid object index %i")` — [UnObj.cpp L322](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L322). The Rocket League write-up finds GObjects by searching that string in IDA, following its xref into `IsValid`, and taking the array/count globals used there. GNames comes from the same function, where the object name is formatted — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)
- **Validation invariants**: index 0 is `None`, 1–15 are the property-type names, 20 is `Core`, 21 is `Engine` — [UnNames.h L30-51](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnNames.h#L30). The object `Index` field is "Index of object into GObjObjects array" — [UnObjBas.h](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1125)
- GObjObjects and GNames are **static TArrays** (`static TArray<UObject*> GObjObjects;`, `static TArrayNoInit<FNameEntry*> Names;`), so they live at fixed addresses in the module's data section — [UnObjBas.h L1178](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1178); [UnName.h L572](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnName.h#L572)

### Inferences
- **External signature scan**: read the DunDefGame.exe image (`MainModule.BaseAddress`, `ModuleMemorySize`) with ReadProcessMemory in large chunks, run the masked match locally, then `RPM(match+2, 4)` → `&GObjects` (TArray address). Then `RPM(&GObjects, 12)` → `{Data, Count, Max}`. Nothing here needs injection. The dumper's own `FindPattern` is an in-process walk over `GetModuleInformation(...).SizeOfImage`, which ports directly.
- **Signature-free heuristic fallback**: scan the module's writable data section for 12-byte candidates `{Data, Count, Max}` with `0 < Count <= Max`, Count in roughly [1e4, 2e6], and Data in heap range.
  - Accept as **GNames** if `RPM(RPM(Data+0)+0x10)` == `"None\0"` and entry 1 == `"ByteProperty"` (and the Index field @+0x08 >> 1 == i).
  - Accept as **GObjects** if for a sample of i, `obj = Data[i]` is null or satisfies `RPM(obj + IndexOff) == i` and `RPM(obj+0x34)` (Class) points to an object whose own Class's name is `"Class"`.
  - This gives a self-healing path even if the code bytes around the sigs change after a patch.
- The `and eax, 0x200` in the GObjects sig is a CDO-flag check (RF_ClassDefaultObject). Its presence confirms ObjectFlags @+0x08 in DD1's build.

### Gaps
- No DD1-specific UnknownCheats/Guided Hacking thread was found that independently confirms the two DD sigs still match the current Steam build of DunDefGame.exe. The dumper repo reports game version "9.3.0" (its own string) and was last pushed 2024-04-20.
- I did not find whether DunDefGame.exe has ASLR (DYNAMICBASE) enabled. If it does not, the static TArray address is constant per build and could also be cached.

---

## Q3. How UE3 SDK generators work (TheFeckless and forks, CodeRed, KN4CK3R-style dumpers, unrealsdk)

### Takeaway
Every UE3 generator follows the same steps. It hand-defines a tiny "bootstrap" set of core structs (UObject/UField/UStruct/UProperty offsets), then lets the engine's own reflection describe everything else:
1. Iterate GObjects.
2. Pick out Class/ScriptStruct/Enum/Const objects by full name.
3. For each struct, walk `Children` → `Next` to get UProperties.
4. Sort them by `Offset` (BoolProperty ties broken by `BitMask`).
5. Fill gaps with `UnknownData` padding.

Inheritance comes from the `SuperField` chain plus `PropertySize`. All of these tools are **injected DLLs**, but the logic is pure memory reads and ports cleanly to an external RPM tool.

### Cited Findings
- **Full name** format is `"<ClassName> <Outer...>.<Name>"`, e.g. `"Class Core.Object"`. The DD dumper walks the whole Outer chain: `for (auto p = Outer; p; p = p->Outer) temp = p->GetName() + "." + temp; name = Class->GetName() + " " + temp + GetName();` — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp). TheFeckless's version only handles 1–2 Outer levels (`Outer->Outer->GetName() . Outer->GetName() . GetName()`) — [TFL PiecesOfCode.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/PiecesOfCode.h)
- **Core class lookup**: `FindClass("Class Core.Object")`, `"Class Core.Field"`, `"Class Core.Struct"`, `"Class Core.Property"`, `"Class Core.BoolProperty"`, `"Class Core.ObjectProperty"`, `"Class Core.StructProperty"`, etc., done by linear scan of GObjects comparing full names (cached in function-local statics) — [DD GenericTypes.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/GenericTypes.cpp); [TFL PiecesOfCode.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/PiecesOfCode.h)
- **IsA**: `for (auto super = Class; super; super = super->SuperField) if (super == cmp) return true;` — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp). The RL write-up uses the same Class → SuperField walk for `GetInstancesByClass` — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)
- **FName lookup** caches found indices (`static std::unordered_set<size_t> cache` / `static TArray<int> NameCache`) before falling back to a full GNames scan — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp); [TFL GameDefines.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/GameDefines.h)
- **Property enumeration** (TheFeckless): `for (UProperty* p = (UProperty*)pClass->Children; p; p = (UProperty*)p->Next)`, skipping `UFunction` children and keeping only `ElementSize > 0`, plus a `!IsA(UScriptStruct)` check "for ME3". Properties are then sorted by `SortProperty`: by `Offset`, and when Offsets are equal and both are BoolProperty, by `BitMask` — [TFL_SdkGen.cpp L190-213, L655-671, L940-995](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/TFL_SdkGen.cpp)
- **Inheritance / padding**: the generator emits only the class's *own* properties. The super's size is taken as `pSuperField->PropertySize`, the own-region is `PropertySize - Super->PropertySize`, and gaps between `lastOffset` and `p->Offset` become `UnknownDataNN[...] // MISSED OFFSET`. `lastOffset = p->Offset + ElementSize*ArrayDim` — [TFL_SdkGen.cpp L599-642, L685-828](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/TFL_SdkGen.cpp)
- **Type sizes by property class**: Byte → 1, Int/Float → 4, **Bool → sizeof(unsigned long) = 4** (a bitfield dword shared by consecutive bools), Str → sizeof(FString)=12, Name → 8, Delegate → FScriptDelegate (0xC), Object → 4 — [TFL_SdkGen.cpp L341-380](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/TFL_SdkGen.cpp); [DD GenericTypes.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/GenericTypes.cpp)
- **Generator lineage**:
  - CodeRed-Generator "began from the source of TheFeckless's UE3 SDK Generator" (C++20, REGISTER_MEMBER auto-offsets) and is injected ("Generation starts automatically after the DLL is injected") — [CodeRed-Generator README](https://github.com/CodeRedModding/CodeRed-Generator)
  - RLSDK-Generator (matix2) also derives from TheFeckless — [RLSDK-Generator](https://github.com/matix2/RLSDK-Generator)
  - LExSDKGenerator is "An adapted version of TheFeckless' UE3 SDK generator" for Mass Effect LE — [LExSDKGenerator](https://github.com/llamathings/LExSDKGenerator)
  - sean-halpin's repo is TheFeckless's original packaged for VS2017: "Inject this dll into an UE3 game to generate an SDK" — [unrealEngine3SdkGenerator](https://github.com/sean-halpin/unrealEngine3SdkGenerator)
- **DD1 dumper is injected** (`DllMain` → `DLL_PROCESS_ATTACH` → `CreateThread(OnAttach)`) and resolves sigs via `GetModuleHandleW(L"DunDefGame.exe")` — [DD Engine/Main.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Engine/Main.cpp); [DD NamesStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/NamesStore.cpp)
- **bl-sdk/unrealsdk** (BL2/TPS, UE3 32-bit) does not hardcode game classes. It keeps per-game offset structs for only the core reflection types (UObject, UField, UStruct, ZProperty, UClass, FNameEntry, FFrame) and resolves everything else by name at runtime through reflection — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)

### Inferences
- The generator pattern of bootstrap core offsets plus reflection for everything else is exactly the self-healing design wanted. Only ~10 core offsets are version-fragile (UObject Index/Outer/Name/Class, UField Next, UStruct SuperField/Children/PropertySize, UProperty ArrayDim/ElementSize/Offset, subclass-pointer slot). Every **game-class** field offset (e.g. a hero's stat array) comes from `UProperty.Offset` and follows the game's own data across patches.
- To get *all* properties of a class (including inherited ones), walk `cls → SuperField → ...` and, at each level, walk `Children → Next`. The first match by name wins, since UnrealScript forbids shadowing a parent's variable name.

### Gaps
- I did not locate TheFeckless's original GameDeception/UnknownCheats release thread (the code is mirrored on GitHub; the forum origin is not citable here).
- No Guided Hacking UE3-specific article was retrieved within the tool budget.

---

## Q4. External (ReadProcessMemory-only) design: find UClass by name, enumerate instances, read property by name (version-resilient resolver), plus performance

### Takeaway
All of it is achievable with RPM alone; nothing requires injection except *calling* UnrealScript functions (ProcessEvent). The resolver is:
1. **Bootstrap**: GObjects/GNames via sig, with a heuristic fallback.
2. **Self-calibrate** the ~10 core reflection offsets against invariants. This works because `Core.Object` exposes its own header as script properties, and the property Offsets must equal the header offsets.
3. **Resolve** `(className, propName) → (UProperty.Offset, ElementSize, ArrayDim, kind, BitMask/Struct/Inner)` once per session and cache it.
4. **Enumerate** instances via a bulk read of the GObjects pointer table, plus a per-object Class check against a precomputed set of matching classes.

### Cited Findings
- `Core.Object`'s script vars `VfTableObject, HashNext, ObjectFlags, HashOuterNext, StateFrame, Linker, LinkerIndex, ObjectInternalInteger, NetIndex, Outer, Name, Class, ObjectArchetype` are declared `native` in Object.uc, i.e. they are real UProperties describing the C++ header — [Object.uc L32-44](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Classes/Object.uc#L32)
- The generators' algorithm for listing a class's fields (Children→Next walk, `Offset`/`BitMask` sort, `ElementSize*ArrayDim` extent) — [TFL_SdkGen.cpp](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/TFL_SdkGen.cpp)
- IsA via the SuperField chain — [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp)
- `UStruct` also has a pre-linked `PropertyLink` list (`UProperty* PropertyLink;` in UStruct, `UProperty* PropertyLinkNext;` in UProperty), which covers *all* properties including inherited ones in a single chain — [UnClass.h L195](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnClass.h#L195); [UnType.h L159](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnType.h#L159); BL2 exposes `PropertyLink` / `PropertyLinkNext` too — [unrealsdk bl2 offsets.h](https://github.com/bl-sdk/unrealsdk/blob/master/src/unrealsdk/game/bl2/offsets.h)
- Instance search by class returned "9 instances of the player class, one per player plus the class template". The CDO/template shows up among instances and must be filtered — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)
- CDOs carry `RF_ClassDefaultObject (0x200)` and the `Default__` name prefix; archetypes carry `RF_ArchetypeObject (0x400)` — [CodeRed Template GameDefines.hpp](https://github.com/CodeRedModding/CodeRed-Generator/blob/main/Engine/Template/GameDefines.hpp#L139); [UnObjBas.h L588](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L588)

### Inferences (concrete external algorithm; C#/.NET 8 x86, RPM only)
1. **Bootstrap tables**: `gobj = RPM<TArray>(sigGObjects)`, `gnames = RPM<TArray>(sigGNames)`. Validate `Name(0)=="None"` and `Name(1)=="ByteProperty"`.
2. **Name reader**: `entry = RPM<uint>(gnames.Data + 4*idx)`; `flagsIdx = RPM<int>(entry+0x08)`; the string is at `entry+0x10`, wide if `(flagsIdx & 1) != 0`, else ANSI. Read 64–128 bytes, cut at NUL, fall back to a longer read if no NUL. **Cache by index in a `string[]`/`Dictionary<int,string>`.** Names are only ever appended in UE3 (the global table is an append-style TArray), so a name cached at index i stays valid. Grow the cache when `gnames.Count` grows.
3. **Calibrate the UObject header** (try candidates and score over ~2,000 sampled non-null slots):
   - IndexOff ∈ {0x04, 0x20}: score how often `RPM<int>(obj+IndexOff)==i`.
   - Name @0x2C: check `0 <= idx < gnames.Count`.
   - Class @0x34: check that `RPM(Class+0x34)` is the self-referential "Class" class, i.e. `Class(Class(Class(x)))` converges to the object named "Class".
   - Outer @0x28: the chain terminates in an object of class "Package".
4. **Find core classes**: one pass over GObjects builds `Dictionary<string fullName, uint addr>` *only for objects whose Class is the "Class" class* (a few thousand), using `"<Outer>.<Name>"`. Get `Core.Object`, `Core.Class`, `Core.Property`, `Core.BoolProperty`, `Core.StructProperty`, `Core.ArrayProperty`, `Core.ObjectProperty`, `Core.ByteProperty`.
5. **Calibrate UField::Next / UStruct::Children / UProperty::Offset using `Core.Object`'s own properties**:
   - For Children ∈ {0x4C (expected), scan 0x40–0x60} and Next ∈ {0x3C, 0x40}, walk `Core.Object.Children` and require that the visited objects' names include `ObjectInternalInteger`, `Outer`, `Name`, `Class`, `ObjectArchetype`.
   - Then for UProperty::Offset ∈ {0x60, 0x64, scan 0x40–0x90}, require `Offset(Outer)==0x28`, `Offset(Name)==0x2C`, `Offset(Class)==0x34`, `Offset(ObjectArchetype)==0x38`, and `Offset(ObjectInternalInteger)==IndexOff`.
   - The one slot that satisfies all five is UProperty::Offset. **This step also double-checks the header calibration from step 3.**
   - ElementSize is the dword where `Outer→4, Name→8, ObjectFlags→8`. ArrayDim is the dword where all of these are 1.
6. **Calibrate SuperField**: try 0x3C (UField variant) and 0x48 (UStruct variant). The right one makes `SuperField(Class "BoolProperty")` = "Property", `SuperField("Property")` = "Field", `SuperField("Field")` = "Object", `SuperField("Object")` = null.
7. **Calibrate the subclass-pointer slot** (`sizeof(UProperty)`, expected 0x7C/0x80/0x84): find any StructProperty in `Core.Object`, e.g. a `Vector` member of some struct, or use `Engine.Actor.Location`. The slot whose value is a pointer to an object whose Class is "ScriptStruct" is the `Struct` field. ArrayProperty `Inner` and ObjectProperty `PropertyClass` share that slot. BoolProperty `BitMask` shares it too: it is a power of two, and consecutive bools at the same Offset have ascending masks.
8. **Resolve (className, propName)**: walk `cls → SuperField*` and at each level `Children → Next`, matching the UProperty Name and skipping Functions/Consts/Enums/Structs by Class name. Return `{Offset, ElementSize, ArrayDim, PropClassName, BitMask?, Struct?, Inner?}`. For nested struct members (`HeroStats.Health`), resolve the StructProperty's `Struct` and recurse. Cache in `Dictionary<(string,string), PropInfo>` for the session. Re-resolve only on process restart, since reflection data is loaded with the class's package and is immutable afterwards.
9. **Enumerate instances of class C**:
   - Precompute `HashSet<uint> matchClasses` = every class object whose SuperField chain hits C (one pass over the class list).
   - Read the whole GObjects pointer table in **one RPM** (`Count*4` bytes; about 0.4–1.2 MB for 100k–300k objects).
   - For each non-null pointer, read only the Class dword (`obj+0x34`) and keep it if it is in `matchClasses`.
   - Exclude CDO/archetypes: name starts with `Default__`, or `(flagsLo & 0x600) != 0`. Exclude dying objects: `(flagsHi & 0x20000000)` (PendingKill) or `(flagsHi & 0x2)` (Unreachable).
10. **Performance**:
    - The dominant cost is per-object RPM syscalls, roughly a few µs each, so 200k reads takes on the order of a second (estimate, not measured).
    - Do the *full* scan rarely, then **index-reuse**: remember `(slotIndex, objPtr)` for hits. On each tick, re-read only `GObjects.Data[slot]` for known slots. If the pointer is unchanged and `RPM(obj+IndexOff)==slot` and Class is unchanged, the hit is still valid.
    - Detect new objects cheaply by diffing the bulk pointer table against the previous snapshot (one RPM, then a memcmp in managed code) and inspect only changed slots.
    - Optionally coalesce: objects allocated near each other can be fetched with one larger RPM spanning several headers, then sliced locally.
11. **Reading values**: `RPM(obj + prop.Offset, ElementSize*ArrayDim)`. Bool: `(dword & BitMask) != 0`. Write a bool by RMW of the containing dword, keeping other bits. TArray: `{Data, Count, Max}`, elements at `Data + i*Inner.ElementSize`. FString: wide chars, Count includes the NUL. FName: `(Index, Number)` → name cache.
12. **Injection flag**: everything above is RPM/WPM-only. **Calling** UnrealScript/native functions (ProcessEvent, `CreateDefaultObject`, the generators' `ProcessEvent` wrapper) requires executing code in the game process (DLL injection or CreateRemoteThread shellcode). The DD dumper and all generators above are injected.

### Gaps
- No public source benchmarks external (cross-process) GObjects iteration speed for UE3. The cost figures above are reasoning, not measurements.
- I found no public external UE3 tool written in C# to cite as precedent. The RL article's tool dereferences pointers directly but does not say whether it is external — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)

---

## Q5. Identifying the "current" instance (local player's controller/pawn/hero) via engine globals

### Takeaway
UE3's canonical chain is **GEngine (GameEngine instance) → `GamePlayers[0]` (LocalPlayer) → `Actor` (PlayerController) → `Pawn`**, with `Actor.WorldInfo` for the world. Every link is a reflected script property, so an external tool can resolve each offset by name. It can also skip the GEngine C++ global entirely by finding the single live `GameEngine` (or `LocalPlayer`) instance in GObjects.

### Cited Findings
- `Engine.uc`: `var init array<LocalPlayer> GamePlayers;` (L351) and `var const GameViewportClient GameViewport;` (L354) — [Engine.uc L351-354](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Engine.uc#L351)
- `Player.uc`: `var transient const playercontroller Actor;` (LocalPlayer extends Player) — [Player.uc L12](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Player.uc#L12)
- `PlayerController.uc`: `var const Player Player; // Player info` (back-link to the LocalPlayer) — [PlayerController.uc L17](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/PlayerController.uc#L17)
- `Controller.uc`: `var editinline repnotify Pawn Pawn;` — [Controller.uc L27](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Controller.uc#L27)
- `Actor.uc`: `var const transient WorldInfo WorldInfo;` — [Actor.uc L288](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Engine/Classes/Actor.uc#L288)
- Real UE3 game code uses the same chain, e.g. Renegade X's console: `GamePlayers.Length > 0 && GamePlayers[0].Actor != None` — found via GitHub code search in `TotemArts/Renegade-X:Development/Src/RenX_Game/Classes/Rx_Console.uc` ([repo](https://github.com/TotemArts/Renegade-X))
- The Dungeon Defense (UDK precursor of DD1) developer blog references `DunDefPlayerController.uc` and `DunDefPlayerCamera.uc` and describes "implementing my own GameInfo, Pawn/Player, Player Controller, and Camera classes" — [UDK Dungeon Defense Developer Blog](https://docs.unrealengine.com/udk/Three/DungeonDefenseDeveloperBlog.html)

### Inferences
- **External recipe without any extra signatures**:
  1. Enumerate instances of `Engine.GameEngine` (IsA-aware, excluding `Default__GameEngine`). There should be exactly one.
  2. Resolve `GamePlayers` by name (it lives on `Engine.Engine`, found via the SuperField walk) and read its TArray; `[0]` is the LocalPlayer.
  3. Read `Player.Actor` → PlayerController (DD subclass is likely `DunDefPlayerController`).
  4. Read `Controller.Pawn` → the hero pawn.
  5. Read `Actor.WorldInfo` for world state.

  **Alternative**: enumerate `Engine.LocalPlayer` instances directly (one per split-screen player), or enumerate `PlayerController` instances and keep the one whose `Player` property points to a LocalPlayer rather than a NetConnection.
- The `GEngine` C++ global would need its own signature. The GObjects-instance route avoids that and is version-resilient.
- Because DD1 supports local split-screen, `GamePlayers` can have >1 entry. Index 0 is the primary player, but a robust tool should expose all of them.
- In UE3 2013, `GamePlayers` is declared in `Engine.uc`. A name-based resolver that walks SuperField is indifferent to which class declares it, so differences in older builds don't matter.

### Gaps
- I could not confirm the exact DD1 subclass names (e.g. `DunDefPlayerController`, `DunDefHero`, `HeroEquipment`) or their package (`UDKGame` vs another) from a public script dump. Only `DunDefPlayerController.uc` is attested, from the 2009 Dungeon Defense UDK blog. `HeroEquipment` returned no public hits.

---

## Q6. Dungeon Defenders 1 specifics (DunDefGame.exe, ~2011 UE3): SDKs, GObjects/GNames, class names, communities

### Takeaway
There is a public, DD1-targeted UE3 SDK dumper (Apail59820/Dungeon-Defenders-SDK-Dumper). It gives the **GObjects and GNames signatures for DunDefGame.exe** and a working core-struct layout (Outer 0x28 / Name 0x2C / Class 0x34 / FNameEntry string at 0x10 / UProperty::Offset 0x64 under its UField-has-SuperField assumption). Trendy's official DD Development Kit shipped the game's UnrealScript source. That is the authoritative place for class/property names, while reflection supplies offsets at runtime.

### Cited Findings
- **Apail59820/Dungeon-Defenders-SDK-Dumper**: a KN4CK3R-style generator (`Engine/` + `Target/DungeonDefenders/`) with `GetGameName() "Dungeon Defenders"`, `GetGameNameShort() "DD"`, `GetGameVersion() "9.3.0"`. It scans `GetModuleHandleW(L"DunDefGame.exe")`. Last push 2024-04-20 (GitHub API) — [repo](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper); [Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp)
  - GNames sig `8B 0D ?? ?? ?? ?? 83 3C 81 00 74` (+2) — [NamesStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/NamesStore.cpp)
  - GObjects sig `8B ?? ?? ?? ?? ?? 8B 04 ?? 8B 40 ?? 25 00 02 00 00` (+2) — [ObjectsStore.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/ObjectsStore.cpp)
  - Core structs: UObject (Index @0x04, Outer @0x28, Name @0x2C, Class @0x34, Archetype @0x38), UField (SuperField @0x3C, Next @0x40), UStruct (Children @0x4C, PropertySize @0x50), UProperty (ArrayDim @0x44, ElementSize @0x48, Flags @0x4C, Offset @0x64), subclass slot @0x84, `UFunction { FunctionFlags; iNative; RepOffset; FriendlyName; NumParms; ParamSize; ReturnValueOffset; ...; Func }` — [EngineClasses.hpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/EngineClasses.hpp)
  - FNameEntry string at +0x10. In the generated SDK, `Flags & 0x4000 ? NamePtr : Name` (likely a template artifact) — [Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp)
- **DD is a licensee-modified UE3 package format**: UEViewer needs `-game=dundef`, applies `PatchDunDefExports(ExportTable, Summary)` to de-obfuscate export tables, and has `ArLicenseeVer >= 21` mesh branches — [UEViewer UnPackage.cpp L553-579](https://github.com/gildor2/UEViewer/blob/master/Unreal/UnrealPackage/UnPackage.cpp#L553); GitHub code search hits in `gildor2/UEViewer` (`GameDefines.h: #define DUNDEF 1`, `UnMesh3.cpp: Ar.Game == GAME_DunDef && Ar.ArLicenseeVer >= 21`, readme "added Dungeon Defenders support (-game=dundef is required)") — [UEViewer repo](https://github.com/gildor2/UEViewer)
- **Official Dungeon Defenders Development Kit (DDDK)**: released by Trendy (Nov 2011) with source code and assets, described as one of the largest publicly released UE3 codebases — [Nexus Mods news](https://www.nexusmods.com/news/8); the wiki notes that modifying item stats in Ranked counts as cheating and that third-party stat tools are banned outside local play — [DD wiki: Development Kit](https://dungeondefenders.wiki.gg/wiki/Development_Kit)
- DD1 grew out of the UDK game "Dungeon Defense", whose developer blog cites `DunDefPlayerController.uc` / `DunDefPlayerCamera.uc` — [UDK Dungeon Defense Developer Blog](https://docs.unrealengine.com/udk/Three/DungeonDefenseDeveloperBlog.html)
- GitHub code search for `DunDefGame.exe` found only launchers/macros (x360ce, AHK scripts, LAN launchers) and no other memory/SDK tooling — search performed via `gh search code "DunDefGame.exe"` (results: x360ce/x360ce, Nitale/Betsy-Script, GregoireF/dd-toolkit, eti-lan/LAN-Launcher, plus the user's own public repo).

### Inferences
- The DDDK's UnrealScript `.uc` files are the right offline reference for exact class/property **names** to feed a name-based resolver. The resolver then gets offsets live, so a DDDK/retail version mismatch costs at most a renamed property, never a wrong offset.
- Since DD1's packages have a custom export-table scheme, but in-memory reflection objects are standard UObjects (the dumper works with generic UE3 traversal), runtime reflection is more reliable than parsing `.upk` files for offsets.
- The dumper's UField/UProperty layout differs from BL2/SMNC by exactly +4. Calibrate rather than trust either (Q4 steps 5–7).

### Gaps
- No UnknownCheats / Guided Hacking / DDHacks thread for DD1 GObjects was found via web search (searches returned only generic UE4 tools). I could not verify a "DD_ModMenu" public repo by that name on GitHub (repo search for "Dungeon Defenders SDK" returned only the Apail59820 dumper).
- I did not obtain the DDDK script sources online to confirm `DunDefHero` / `HeroEquipment` / `DunDefPlayerController` property names.

---

## Q7. Pitfalls: pending-kill / GC, null slots, stale pointers, thread safety

### Takeaway
GObjects has **null holes and slot reuse**: a destroyed object's slot is nulled and pushed onto `GObjAvailable`, and the next new object pops it. A cached `(slot, ptr)` can therefore silently become a different object. Objects stay in the array while flagged `RF_PendingKill`/`RF_Unreachable` until purge, and purge can be incremental across frames. External reads are inherently racy (TArray reallocation, frees mid-read), so every read must be validated and tolerated when it fails.

### Cited Findings
- On destruction: `GObjObjects(Index) = NULL; GObjAvailable.AddItem( Index );` — [UnObj.cpp L1452-1453](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L1452)
- On allocation: `if( GObjAvailable.Num() ) { InIndex = GObjAvailable.Pop(); check(GObjObjects(InIndex)==NULL); } else { InIndex = GObjObjects.Add(); }`. Freed indices are reused LIFO — [UnObj.cpp L7799-7806](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L7799)
- Objects below `GObjFirstGCIndex` are in a "disregard for GC" permanent pool (`GObjFirstGCIndex` "First index into objects array taken into account for GC", `GObjLastNonGCIndex`, `GPermanentObjectPool`). Incremental purge state is tracked by `GObjIncrementalPurgeIsInProgress` / `GObjCurrentPurgeObjectIndex` — [UnObjBas.h ~L1178-1215](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1178); allocation into the non-GC range when `RF_DisregardForGC` is set — [UnObj.cpp ~L7790-7815](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Src/UnObj.cpp#L7790)
- `IsPendingKill()` is `HasAnyFlags(RF_PendingKill)`: "dead but memory still valid" — [UnObjBas.h L1410-1422](https://github.com/CodeRedModding/UnrealEngine3/blob/main/Development/Src/Core/Inc/UnObjBas.h#L1410)
- All generator loops null-check GObjects entries (`if (!Object) continue;`) — [TFL PiecesOfCode.h](https://github.com/sean-halpin/unrealEngine3SdkGenerator/blob/master/sdkgen/UE3BasicCore/SMNC/PiecesOfCode.h); [DD Generator.cpp](https://github.com/Apail59820/Dungeon-Defenders-SDK-Dumper/blob/main/Target/DungeonDefenders/Generator.cpp)
- Objects load dynamically, so a scan only sees what is loaded "so far" — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)
- CDOs and templates appear in instance scans ("plus the class template") — [dev.to RL article](https://dev.to/drkn1ght/how-to-extract-rocket-league-data-in-real-time-reverse-engineering-in-unreal-engine-games-505l)

### Inferences
- **Validate cached instance handles every use**:
  - `GObjects.Data[slot] == ptr`
  - `RPM(ptr+IndexOff) == slot`
  - `RPM(ptr+0x34) == expectedClass`
  - Name index unchanged
  - `(flagsHi & 0x20000000) == 0` (not PendingKill)
  
  If any check fails, drop the handle and rescan. Slot reuse makes "same slot, same pointer" alone insufficient right after a GC when the allocator reuses the same memory, which is why the Class and Name checks matter.
- **Class/Property objects are stable**: native and startup-package classes live in the disregard-for-GC/root range (reflection data for loaded packages is not collected while the package is loaded). So the property-offset cache from Q4 is safe for the whole session. *Instance* pointers (pawns, controllers, items) are transient and change on map load / respawn.
- **TArray reallocation race**: GObjects.Data and GNames.Data can move when the arrays grow (`GObjObjects.Add()` when no free slot). Always re-read `{Data, Count}` from the static TArray address before a bulk scan, and treat partially failed RPMs as "skip this tick". Never hold the Data pointer across ticks.
- **No thread safety is available externally**: there is no lock to take. Prefer reading during steady gameplay, retry on inconsistency, and bound every pointer walk (Outer/SuperField/Next chains) with a max depth (e.g. 64) to avoid infinite loops on torn reads. Swallowing transient RPM failures is the correct pattern here.
- **Map transitions** (and DD1's tavern ↔ mission loads) trigger GC. Expect a burst of nulls and PendingKill objects, so pause instance resolution until the LocalPlayer → Actor → Pawn chain re-validates.
- **Writes**: WPM into a PendingKill object is harmless but useless. WPM into a reused slot's new occupant is dangerous, which is why the full validation above should precede any write.

### Gaps
- The UE3 source shown is the 2013 build. Whether DD1's 2011 build already had `GObjAvailable` LIFO reuse and incremental purge is likely (both are long-standing UE features; UE2-era `UnObjBas.h` mirrors also declare `GObjAvailable`, e.g. [wrstone/ut432pubsrc](https://github.com/wrstone/ut432pubsrc) per GitHub code search) but not directly verified for DD1.
- No source quantifies how often DD1 runs GC during a mission.
