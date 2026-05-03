using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LinearColorNative
{
	// Memory layout confirmed empirically: game stores A first, then R, G, B.
	// Reading as (R,G,B,A) caused every channel to shift one slot and the hardcoded
	// A=1 on write to clobber the game's B, turning items blue.
	public float A;

	public float R;

	public float G;

	public float B;

	public override string ToString()
	{
		return $"{R:N1}, {G:N1}, {B:N1}";
	}
}
