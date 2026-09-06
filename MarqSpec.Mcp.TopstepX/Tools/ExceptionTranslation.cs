using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The one shape "a lower layer's exception becomes this tool surface's refusal" comes in, wherever a call
/// that can throw meets a boundary that must not leak a raw .NET exception type across the wire.
/// </summary>
/// <remarks>
/// Extracted because the shape — catch one or two specific exception types, throw
/// <see cref="McpException"/> with the same message, nothing else — appeared eight times across
/// <see cref="KeyLevelTools"/> and its siblings before this existed, once per place a catalogue or a registry could refuse
/// a name it did not recognise. A ninth call site copies the message, not the type: <c>which</c> filters the
/// exception rather than pattern-matching it, so the set of types a caller translates is still visible at
/// the call site instead of being hidden inside a shared catch list some sites do not want.
/// </remarks>
internal static class ExceptionTranslation
{
    /// <summary>Runs <paramref name="body"/>, translating a matching exception into an <see cref="McpException"/>.</summary>
    /// <param name="body">The call that may throw.</param>
    /// <param name="which">Which exceptions to translate; anything else propagates unchanged.</param>
    public static T Try<T>(Func<T> body, Func<Exception, bool> which)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (which(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>The async shape of <see cref="Try{T}"/> — the exception surfaces on await, not on invocation.</summary>
    /// <param name="body">The call that may throw.</param>
    /// <param name="which">Which exceptions to translate; anything else propagates unchanged.</param>
    public static async Task<T> TryAsync<T>(Func<Task<T>> body, Func<Exception, bool> which)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (which(ex))
        {
            throw new McpException(ex.Message);
        }
    }
}
