namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The little arithmetic <see cref="decimal"/> does not ship with.
/// </summary>
/// <remarks>
/// Everything here exists so a calculation can stay in <see cref="decimal"/> end to end. Rounding through
/// <see cref="double"/> for one square root and back would be easy and is exactly the shortcut that puts a
/// binary-floating-point artefact into a price-derived number.
/// </remarks>
public static class DecimalMath
{
    /// <summary>
    /// Square root by Newton's method, exact to the limits of <see cref="decimal"/>.
    /// </summary>
    /// <param name="value">The value. Must not be negative.</param>
    /// <returns>The square root.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static decimal Sqrt(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot take the square root of a negative value.");
        }

        if (value == 0m)
        {
            return 0m;
        }

        // Seed from the double approximation — it is already close, so this converges in a handful of
        // iterations rather than dozens, and every subsequent step is pure decimal arithmetic.
        decimal current = (decimal)Math.Sqrt((double)value);
        if (current <= 0m)
        {
            current = value > 1m ? value / 2m : 1m;
        }

        // Newton's method on f(x) = x^2 - value. Bounded rather than while(true): near the limits of decimal
        // precision the iteration can oscillate between two adjacent representable values and never settle.
        for (int i = 0; i < 32; i++)
        {
            decimal next = (current + (value / current)) / 2m;
            if (next == current)
            {
                break;
            }

            current = next;
        }

        return current;
    }
}
