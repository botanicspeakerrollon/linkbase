using Bunit;

namespace Linkbase.Tests;

// bUnit keeps its JS invocations in a plain List that the page appends to from its own thread, so enumerating it while the
// page is still rendering throws "Collection was modified". Read a copy instead, trying again if the copy itself collides.
internal static class JsInvocationExtensions
{
    public static IReadOnlyList<JSRuntimeInvocation> Safe(this IEnumerable<JSRuntimeInvocation> invocations)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return invocations.ToList();
            }
            catch (InvalidOperationException) when (attempt < 50)
            {
                Thread.Sleep(5);
            }
        }
    }
}
