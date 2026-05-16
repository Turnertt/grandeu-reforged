using System.ComponentModel;

internal class ItemSearch
{
	private int _Generic;

	private int _Poison;

	private int _Fire;

	private int _Lightning;

	private int _Knockback;

	private int _ChargeSpeed;

	private int _NumberOfProjectiles;

	private int _ReloadSpeed;

	[Category("A. Hero Stats")]
	public int HeroHealth { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSpeed { get; set; }

	[Category("A. Hero Stats")]
	public int HeroDamage { get; set; }

	[Category("A. Hero Stats")]
	public int HeroCasting { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSkill1 { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSkill2 { get; set; }

	[Category("B. Tower Stats")]
	public int TowerHealth { get; set; }

	[Category("B. Tower Stats")]
	public int TowerSpeed { get; set; }

	[Category("B. Tower Stats")]
	public int TowerDamage { get; set; }

	[Category("B. Tower Stats")]
	public int TowerRange { get; set; }

	[Category("C. Resistance")]
	public int Generic
	{
		get
		{
			return _Generic;
		}
		set
		{
			_Generic = Int32Range(value, -99, 99);
		}
	}

	[Category("C. Resistance")]
	public int Poison
	{
		get
		{
			return _Poison;
		}
		set
		{
			_Poison = Int32Range(value, -99, 99);
		}
	}

	[Category("C. Resistance")]
	public int Fire
	{
		get
		{
			return _Fire;
		}
		set
		{
			_Fire = Int32Range(value, -99, 99);
		}
	}

	[Category("C. Resistance")]
	public int Lightning
	{
		get
		{
			return _Lightning;
		}
		set
		{
			_Lightning = Int32Range(value, -99, 99);
		}
	}

	[Category("D. Bonuses")]
	public int Knockback
	{
		get
		{
			return _Knockback;
		}
		set
		{
			_Knockback = Int32Range(value, -128, 128);
		}
	}

	[Category("D. Bonuses")]
	public int ChargeSpeed
	{
		get
		{
			return _ChargeSpeed;
		}
		set
		{
			_ChargeSpeed = Int32Range(value, -128, 128);
		}
	}

	[Category("D. Bonuses")]
	public int NumberOfProjectiles
	{
		get
		{
			return _NumberOfProjectiles;
		}
		set
		{
			_NumberOfProjectiles = Int32Range(value, -128, 128);
		}
	}

	[Category("D. Bonuses")]
	public int SpeedOfProjectiles { get; set; }

	[Category("D. Bonuses")]
	public int ReloadSpeed
	{
		get
		{
			return _ReloadSpeed;
		}
		set
		{
			_ReloadSpeed = Int32Range(value, -128, 128);
		}
	}

	[Category("E. Identity")]
	public string Description { get; set; } = "";

	[Category("E. Identity")]
	public EquipmentType EquipmentType { get; set; }

	[Category("F. Level")]
	public int Level { get; set; }

	[Category("F. Level")]
	public int MaxLevel { get; set; }

	private int Int32Range(int value, int min, int max)
	{
		if (value < min)
		{
			return min;
		}
		if (value > max)
		{
			return max;
		}
		return value;
	}
}
