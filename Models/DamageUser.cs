using System.ComponentModel;
using System.Globalization;

[TypeConverter(typeof(ExpandableObjectConverter))]
internal class DamageUser
{
	private bool _Resist;

	private int _Value;

	private int _Type;

	[RefreshProperties(RefreshProperties.All)]
	public int Value
	{
		get
		{
			return _Value;
		}
		set
		{
			if (_Resist)
			{
				if (value < -99)
				{
					value = -99;
				}
				if (value > 99)
				{
					value = 99;
				}
			}
			_Value = value;
		}
	}

	[RefreshProperties(RefreshProperties.All)]
	public string Type
	{
		get
		{
			return _Type.ToString("X");
		}
		set
		{
			if (int.TryParse(value, NumberStyles.HexNumber, null, out var result))
			{
				_Type = result;
			}
		}
	}

	public DamageUser(bool resist, DamageNative n)
	{
		_Resist = resist;
		_Value = n.Value;
		_Type = n.DamageType;
	}

	public int GetTypeValue()
	{
		return _Type;
	}

	public override string ToString()
	{
		return string.Empty;
	}
}
