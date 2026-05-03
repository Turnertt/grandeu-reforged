using System.ComponentModel;

internal class TowerSearch
{
	[Category("A. HP")]
	public int CurrentHP { get; set; }

	[Category("A. HP")]
	public int MaxHP { get; set; }

	[Category("B. Combat")]
	public float AttackDamage { get; set; }

	[Category("B. Combat")]
	public float AttackRate { get; set; }

	[Category("B. Combat")]
	public float AttackRange { get; set; }
}
