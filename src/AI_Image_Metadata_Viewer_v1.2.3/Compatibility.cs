global using System.Runtime.InteropServices;

namespace AIImageMetadataViewer;

internal static class SpanLinqCompatibility
{
    public static int Count(this ReadOnlySpan<byte> source, Func<byte, bool> predicate)
    {
        var count = 0;
        foreach (var item in source) if (predicate(item)) count++;
        return count;
    }
}
