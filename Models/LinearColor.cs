using System;
using System.ComponentModel;
using System.Windows.Media;

public class LinearColor
{
	private float _r;
	private float _g;
	private float _b;
	private float _a = 1f;

	public int R
	{
		get { return (int)Math.Round(_r * 255f); }
		set { _r = value / 255f; }
	}

	public int G
	{
		get { return (int)Math.Round(_g * 255f); }
		set { _g = value / 255f; }
	}

	public int B
	{
		get { return (int)Math.Round(_b * 255f); }
		set { _b = value / 255f; }
	}

	[Browsable(false)]
	public int A
	{
		get { return (int)Math.Round(_a * 255f); }
		set { _a = value / 255f; }
	}

	[Browsable(false)]
	internal float Rf { get { return _r; } set { _r = value; } }

	[Browsable(false)]
	internal float Gf { get { return _g; } set { _g = value; } }

	[Browsable(false)]
	internal float Bf { get { return _b; } set { _b = value; } }

	[Browsable(false)]
	internal float Af { get { return _a; } set { _a = value; } }

	public Color ToColor()
	{
		return Color.FromArgb(ClampByte(A), ClampByte(R), ClampByte(G), ClampByte(B));
	}

	public SolidColorBrush ToBrush()
	{
		return new SolidColorBrush(ToColor());
	}

	public override string ToString()
	{
		return $"{R}, {G}, {B}";
	}

	private static byte ClampByte(int n)
	{
		if (n < 0) return 0;
		if (n > 255) return 255;
		return (byte)n;
	}
}
