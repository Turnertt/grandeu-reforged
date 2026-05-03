using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ItemNative
{
	public int EquipmentTemplate;

	public int R0;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
	public int[] StatModifiers;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
	public DamageNative[] DamageReductions;

	public int WeaponDamageBonus;

	public int WeaponNumberOfProjectilesBonus;

	public int WeaponSpeedOfProjectilesBonus;

	public DamageNative WeaponAdditionalDamage;

	public float WeaponDrawScaleMultiplier;

	public float MaxRandomElementalDamageMultiplier;

	public float WeaponSwingSpeedMultiplier;

	public int Flags;

	public int WeaponReloadSpeedBonus;

	public int WeaponKnockbackBonus;

	public int WeaponAltDamageBonus;

	public int WeaponBlockingBonus;

	public int WeaponClipAmmoBonus;

	public int AdditionalAllowedUpgradeResistancePoints;

	public int RequirementLevelOverride;

	public int WeaponChargeSpeedBonus;

	public int WeaponShotsPerSecondBonus;

	public byte NameIndex_Base;

	public Quality2 NameIndex_QualityDescriptor;

	public Quality3 NameIndex_DamageReduction;

	public byte PrimaryColorSet;

	public byte SecondaryColorSet;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
	public byte[] R1;

	public byte Mystery;

	public byte ManualLR;

	public EquipmentType EquipmentType;

	public byte R2;

	public NativeArray PrimaryColorSets;

	public NativeArray SecondaryColorSets;

	public LinearColorNative PrimaryColorOverride;

	public LinearColorNative SecondaryColorOverride;

	public int R4;

	public int MaximumSellWorth;

	public int MinimumSellWorth;

	public int ShopMinimumSellWorth;

	public int MaxEquipmentLevel;

	public NativeArray EquipmentName;

	public NativeArray Description;

	// 164-byte UObject/archetype internal gap.
	// Confirmed by forge diff: ForgerName is at offset 428, DroppedLocation at 440.
	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 164)]
	public byte[] _InstancePad;

	public NativeArray ForgerName;

	public Vector DroppedLocation;

	public int FolderID;

	public int Level;

	public int StoredMana;

	public int UserID;

	public float MyRatingPercent;

	public float MyRating;

	public int EquipmentID1;

	public int EquipmentID2;

	public NativeArray BaseEquipmentName;
}
