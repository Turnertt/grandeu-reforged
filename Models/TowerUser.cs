using System.ComponentModel;

internal class TowerUser
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

	[Category("B. Combat")]
	public float AttackArc { get; set; }

	[Category("C. Upgrade")]
	public float UpgradeLevel { get; set; }

	[Category("C. Upgrade")]
	public int MaxUpgrades { get; set; }
}
