using System.ComponentModel;
using System.Globalization;

internal class LinearColorConverter : StringConverter
{
	public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
	{
		return true;
	}

	public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
	{
		return true;
	}

	public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
	{
		string[] array = ((string)value).Split(',');
		LinearColor linearColor = new LinearColor();
		if (array.Length > 0) linearColor.R = ToInt(array[0]);
		if (array.Length > 1) linearColor.G = ToInt(array[1]);
		if (array.Length > 2) linearColor.B = ToInt(array[2]);
		if (array.Length > 3) linearColor.A = ToInt(array[3]);
		return linearColor;
	}

	private int ToInt(string value)
	{
		// Accept either int (0-255) or float (0.0-1.0) for back-compat with
		// anyone who typed values into the old float-based fields.
		string t = value.Trim();
		if (t.Contains("."))
		{
			if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
			{
				if (f < 0f) f = 0f;
				if (f > 1f) f = 1f;
				return (int)System.Math.Round(f * 255f);
			}
			return 0;
		}
		int.TryParse(t, out int n);
		return n;
	}

	public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
	{
		return new StandardValuesCollection(((ItemUser)context.Instance).ColorTables[context.PropertyDescriptor.Name]);
	}
}
