internal enum Quality2 : byte
{
	// High tiers verified against the v10.0 SDK CONST_EQUIPMENT_* set
	// (CONST_EQUIPMENT_QUALITY_MAX = 20): ULTIMATE 16, ULTIMATE93 17,
	// ULTIMATE_PLUS 18, ULTIMATE_PLUS_PLUS 19 — and these match the
	// codebase's own QualityRank, so the stored byte == SDK == rank here.
	UltimatePlusPlus = 19,
	UltimatePlus = 18,
	Ultimate93 = 17,
	Ultimate = 16,
	Supreme = 15,
	Transcendent = 14,
	Mythical = 13,
	Godly = 0,
	Legendary = 1,
	Epic = 2,
	Amazing = 3,
	Powerful = 4,
	Shining = 5,
	Polished = 6,
	Sturdy = 7,
	Solid = 8,
	Stocky = 9,
	Worn = 10,
	Torn = 11,
	Cursed = 12
}
