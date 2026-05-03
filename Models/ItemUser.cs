using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

internal class ItemUser
{
	private int _EquipmentTemplate;

	private int _Blocking;

	private int _Knockback;

	private int _ChargeSpeed;

	private int _ShotsPerSecond;

	private int _NumberOfProjectiles;

	private int _ReloadSpeed;

	public Dictionary<string, LinearColorNative[]> ColorTables;

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
	public DamageUser Generic { get; set; }

	[Category("C. Resistance")]
	public DamageUser Poison { get; set; }

	[Category("C. Resistance")]
	public DamageUser Fire { get; set; }

	[Category("C. Resistance")]
	public DamageUser Lightning { get; set; }

	[Category("D. Damage")]
	public int Damage { get; set; }

	[Category("D. Damage")]
	public int RangedDamage { get; set; }

	[Category("D. Damage")]
	public DamageUser ElementalDamage { get; set; }

	[Category("E. Bonuses")]
	public int Blocking
	{
		get
		{
			return _Blocking;
		}
		set
		{
			_Blocking = Int32Range(value, -128, 128);
		}
	}

	[Category("E. Bonuses")]
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

	[Category("E. Bonuses")]
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

	[Category("E. Bonuses")]
	public int ShotsPerSecond
	{
		get
		{
			return _ShotsPerSecond;
		}
		set
		{
			_ShotsPerSecond = Int32Range(value, -128, 128);
		}
	}

	[Category("E. Bonuses")]
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

	[Category("E. Bonuses")]
	public int SpeedOfProjectiles { get; set; }

	[Category("E. Bonuses")]
	public int ClipAmmo { get; set; }

	[Category("E. Bonuses")]
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

	[Category("F. Quality")]
	public byte Quality1 { get; set; }

	[Category("F. Quality")]
	public Quality2 Quality2 { get; set; }

	[Category("F. Quality")]
	public Quality3 Quality3 { get; set; }

	[Category("F. Quality")]
	public byte QualityFlag { get; set; }

	[TypeConverter(typeof(LinearColorConverter))]
	[Category("G. Visual")]
	public LinearColor Color1 { get; set; }

	[TypeConverter(typeof(LinearColorConverter))]
	[Category("G. Visual")]
	public LinearColor Color2 { get; set; }

	[Category("G. Visual")]
	[TypeConverter(typeof(ExpandableObjectConverter))]
	public LinearColor Color1Override { get; set; }

	[TypeConverter(typeof(ExpandableObjectConverter))]
	[Category("G. Visual")]
	public LinearColor Color2Override { get; set; }

	[Category("G. Visual")]
	public float DrawScale { get; set; }

	[Category("H. Other")]
	public float SwingSpeed { get; set; }

	[Category("H. Other")]
	public float Rating { get; set; }

	[Category("H. Other")]
	public float RatingPercent { get; set; }

	[Category("H. Other")]
	public EquipmentType EquipmentType { get; set; }

	[Category("H. Other")]
	public string EquipmentTemplate
	{
		get
		{
			return _EquipmentTemplate.ToString("X");
		}
		set
		{
			if (int.TryParse(value, NumberStyles.HexNumber, null, out var result))
			{
				_EquipmentTemplate = result;
			}
		}
	}

	[Category("I. Identity")]
	public string ItemName { get; set; }

	[Category("I. Identity")]
	public string Description { get; set; }

	[Category("I. Identity")]
	public string ForgerName { get; set; }

	[Category("I. Identity")]
	public int ID1 { get; set; }

	[Category("I. Identity")]
	public int ID2 { get; set; }

	[Category("J. Value")]
	public int MaximumValue { get; set; }

	[Category("J. Value")]
	public int MinimumValue { get; set; }

	[Category("K. Level")]
	public int Level { get; set; }

	[Category("K. Level")]
	public int MaxLevel { get; set; }

	[Category("K. Level")]
	public int StoredMana { get; set; }

	[Category("K. Level")]
	public byte LevelRequirement { get; set; }

	public ItemUser()
	{
		ColorTables = new Dictionary<string, LinearColorNative[]>();
	}

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

	public void AddColorTable(string name, LinearColorNative[] colors)
	{
		ColorTables.Add(name, colors);
	}
}
