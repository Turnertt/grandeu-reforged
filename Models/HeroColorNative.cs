using System.Runtime.InteropServices;

// Heroes store colors in the standard (R, G, B, A) order, unlike items which
// use (A, R, G, B). Kept as a separate struct so the two layouts don't collide.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HeroColorNative
{
	public float R;

	public float G;

	public float B;

	public float A;

	public override string ToString()
	{
		return $"{R:N1}, {G:N1}, {B:N1}";
	}
}
