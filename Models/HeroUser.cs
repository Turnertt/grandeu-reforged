using System.ComponentModel;

internal class HeroUser
{
	[Category("A. Hero Stats")]
	public int HeroHealth { get; set; }

	[Category("A. Hero Stats")]
	public int HeroDamage { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSpeed { get; set; }

	[Category("A. Hero Stats")]
	public int HeroCasting { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSkill1 { get; set; }

	[Category("A. Hero Stats")]
	public int HeroSkill2 { get; set; }

	[Category("B. Tower Stats")]
	public int TowerHealth { get; set; }

	[Category("B. Tower Stats")]
	public int TowerDamage { get; set; }

	[Category("B. Tower Stats")]
	public int TowerRange { get; set; }

	[Category("B. Tower Stats")]
	public int TowerSpeed { get; set; }

	[TypeConverter(typeof(ExpandableObjectConverter))]
	[Category("C. Visual")]
	public LinearColor Color1 { get; set; }

	[Category("C. Visual")]
	[TypeConverter(typeof(ExpandableObjectConverter))]
	public LinearColor Color2 { get; set; }

	[TypeConverter(typeof(ExpandableObjectConverter))]
	[Category("C. Visual")]
	public LinearColor Color3 { get; set; }

	[Category("D. Identity")]
	public string HeroName { get; set; }

	[Category("E. Level")]
	public int Level { get; set; }

	[Category("E. Level")]
	public int Experience { get; set; }
}
