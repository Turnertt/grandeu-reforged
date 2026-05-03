using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct NativeArray
{
	public int Address;

	public int CurrentLength;

	public int MaximumLength;

	public NativeArray(int length)
	{
		this = default(NativeArray);
		CurrentLength = length;
		MaximumLength = length;
	}
}
