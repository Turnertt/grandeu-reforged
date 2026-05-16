// DD1 UE3 enum UDKGame._DataTypes.EWeaponType (uint8). This is the weapon's
// class gate — read as a single byte at item Address + MaxCompat.WeaponTypeOffset
// (0x7EC), OUTSIDE the marshaled ItemNative. Display-only here.
//
// Observed class map is counter-intuitive (see DD1_INTERNALS.md §5):
// Apprentice→APPRENTICE, Squire→SQUIRE, Monk→RECRUIT, Ranger→INITIATE,
// Jester→ANYONE — so we show the raw EWeaponType, not an assumed class name.
internal enum EWeaponType : byte
{
	Anyone = 0,
	Apprentice = 1,
	Squire = 2,
	Initiate = 3,
	Recruit = 4,
	None = 5
}

internal static class WeaponClass
{
	// Friendly name for a raw weaponType byte. Unknown values (outside the
	// known enum range) render as their decimal number rather than guessing.
	public static string Name(byte weaponType) =>
		weaponType <= (byte)EWeaponType.None
			? ((EWeaponType)weaponType).ToString()
			: weaponType.ToString();
}
