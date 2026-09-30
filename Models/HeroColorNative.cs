using System.Runtime.InteropServices;

// Heroes store colors in the standard FLinearColor (R, G, B, A) order — as do
// items since the 2026-09-27 ItemNative realignment (the old item "(A, R, G,
// B)" was a misaligned-struct artifact). Kept separate so the types don't collide.
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
