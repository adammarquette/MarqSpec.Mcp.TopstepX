using System.Reflection;
using System.Reflection.Emit;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Reads the compiled body of a gateway and answers <i>which <c>operation</c> strings it actually names</i>
/// at its <see cref="VenueCallGuard.RunAsync{T}"/> call sites (gh#559).
/// </summary>
/// <remarks>
/// <para>
/// <b>The test this serves used to assert over a literal beside itself</b>, so "the <c>operation</c>
/// vocabulary is closed" was a claim about the test's own list rather than about
/// <c>ProjectXMarketDataGateway</c>. A gateway method that invented its own string — the exact mistake the
/// vocabulary exists to prevent — passed it, because nothing in it ever looked at the gateway.
/// </para>
/// <para>
/// <b>The compiled body rather than the source file.</b> A source scan would depend on a path that exists
/// only on the machine that built the assembly, and it would be fooled by a comment, a string in a doc block
/// or a line break in the middle of a call. The IL is what actually runs: <c>VenueOperation.GetBars</c> is a
/// <c>const string</c>, so the compiler inlines it as an <c>ldstr</c>, and an invented literal is
/// indistinguishable from it at this level — which is the property that makes the check mean something.
/// </para>
/// <para>
/// <b>The rule per call site: exactly one of the string literals loaded for it is a value the vocabulary
/// knows.</b> That avoids simulating the evaluation stack to work out <i>which</i> literal is the first
/// argument, and it is sound in the direction that matters — an invented operation leaves the call site with
/// no known value among its literals and fails. The other literals in the window are the <c>what</c> prose
/// and any logging format strings that precede the call in the same body, and prose is not a vocabulary
/// value. Note the window is per method body, and async bodies are compiled into a state machine's
/// <c>MoveNext</c>, which is why nested types are walked too.
/// </para>
/// <para>
/// <b>It fails when it finds nothing.</b> A scanner that returns an empty set on a body it could not read
/// would let the caller's set comparison pass vacuously if the caller compared it to an empty expectation —
/// so the caller asserts non-emptiness first, and an unreadable call site throws from here rather than being
/// skipped.
/// </para>
/// </remarks>
internal static class GatewayOperationScan
{
    private static readonly Dictionary<ushort, OpCode> _opCodes = BuildOpCodeTable();

    /// <summary>Every <c>operation</c> value the type names at a <see cref="VenueCallGuard"/> call site.</summary>
    /// <param name="gateway">The gateway type to read.</param>
    /// <param name="vocabulary">The values the vocabulary knows, used to pick the operation out of a window.</param>
    /// <returns>One entry per call site, in the order the bodies were walked.</returns>
    /// <exception cref="InvalidOperationException">
    /// A call site's literals contain no vocabulary value, or more than one — the first is an operation the
    /// vocabulary does not know, and the second is a window this rule cannot read, which is a failure and
    /// never a skip.
    /// </exception>
    internal static IReadOnlyList<string> OperationsNamedBy(Type gateway, IReadOnlyCollection<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(vocabulary);

        List<string> named = [];

        foreach (Type type in WithNestedTypes(gateway))
        {
            foreach (MethodBase method in Bodies(type))
            {
                ScanBody(type, method, vocabulary, named);
            }
        }

        return named;
    }

    /// <summary>The type and every type nested inside it, however deep.</summary>
    /// <param name="type">The outer type.</param>
    /// <returns>The type and its nested types.</returns>
    /// <remarks>
    /// An <c>async</c> method's body does not live on the method: the compiler moves it into a nested state
    /// machine's <c>MoveNext</c>, and a lambda's into a nested display class. Every call site this scan is
    /// looking for is inside one of those.
    /// </remarks>
    private static IEnumerable<Type> WithNestedTypes(Type type)
    {
        yield return type;

        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (Type inner in WithNestedTypes(nested))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Every method and constructor on a type that carries IL.</summary>
    /// <param name="type">The type to read.</param>
    /// <returns>The methods with a body.</returns>
    private static IEnumerable<MethodBase> Bodies(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        return type.GetMethods(All).Cast<MethodBase>()
            .Concat(type.GetConstructors(All))
            .Where(method => method.GetMethodBody() is not null);
    }

    /// <summary>Walks one body and appends the operation of every guard call site in it.</summary>
    /// <param name="type">The type the body belongs to, for messages.</param>
    /// <param name="method">The method to walk.</param>
    /// <param name="vocabulary">The values the vocabulary knows.</param>
    /// <param name="named">Where the operations go.</param>
    private static void ScanBody(
        Type type,
        MethodBase method,
        IReadOnlyCollection<string> vocabulary,
        List<string> named)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return;
        }

        Module module = type.Module;
        Type[]? typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        List<string> literals = [];
        int offset = 0;

        while (offset < il.Length)
        {
            OpCode code = Read(il, ref offset);

            if (code == OpCodes.Ldstr)
            {
                literals.Add(module.ResolveString(BitConverter.ToInt32(il, offset)));
            }
            else if ((code == OpCodes.Call || code == OpCodes.Callvirt)
                && IsGuardCall(module, BitConverter.ToInt32(il, offset), typeArguments, methodArguments))
            {
                named.Add(OperationIn(literals, vocabulary, type, method));
                literals.Clear();
            }

            offset += OperandSize(code, il, offset);
        }
    }

    /// <summary>Picks the one vocabulary value out of a call site's literals.</summary>
    /// <param name="literals">The literals loaded since the previous call site in this body.</param>
    /// <param name="vocabulary">The values the vocabulary knows.</param>
    /// <param name="type">The type, for the message.</param>
    /// <param name="method">The method, for the message.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="InvalidOperationException">There was not exactly one.</exception>
    private static string OperationIn(
        List<string> literals,
        IReadOnlyCollection<string> vocabulary,
        Type type,
        MethodBase method)
    {
        List<string> known = [.. literals.Where(vocabulary.Contains)];

        return known.Count == 1
            ? known[0]
            : throw new InvalidOperationException(
                $"{type.Name}.{method.Name} calls VenueCallGuard.RunAsync with {known.Count} of its string "
                + "literals in VenueOperation, and exactly one is expected. A count of zero means the "
                + "gateway named an operation the closed vocabulary does not know — which starts a time "
                + "series no dashboard names and no reviewer priced. Literals at that call site: "
                + string.Join(", ", literals.Select(literal => "\"" + literal + "\"")));
    }

    /// <summary>Whether a method token resolves to <see cref="VenueCallGuard.RunAsync{T}"/>.</summary>
    /// <param name="module">The module the token belongs to.</param>
    /// <param name="token">The metadata token.</param>
    /// <param name="typeArguments">The declaring type's generic arguments, if any.</param>
    /// <param name="methodArguments">The method's generic arguments, if any.</param>
    /// <returns>Whether it is the guard's funnel.</returns>
    private static bool IsGuardCall(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments)
    {
        try
        {
            MethodBase? resolved = module.ResolveMethod(token, typeArguments, methodArguments);

            return resolved?.DeclaringType == typeof(VenueCallGuard)
                && resolved.Name == nameof(VenueCallGuard.RunAsync);
        }
        catch (ArgumentException)
        {
            // A token this module cannot resolve in isolation is never the guard's — the guard lives in the
            // assembly under test and resolves cleanly.
            return false;
        }
    }

    /// <summary>Reads one opcode and advances past it, leaving the offset on its operand.</summary>
    /// <param name="il">The method body.</param>
    /// <param name="offset">The read position.</param>
    /// <returns>The opcode.</returns>
    private static OpCode Read(byte[] il, ref int offset)
    {
        ushort value = il[offset];
        offset++;

        if (value == 0xFE)
        {
            value = (ushort)(0xFE00 | il[offset]);
            offset++;
        }

        return _opCodes.TryGetValue(value, out OpCode code)
            ? code
            : throw new InvalidOperationException(
                $"Unknown IL opcode 0x{value:X4} at offset {offset}. The scan cannot continue safely, and "
                + "guessing the operand width would silently mis-read every call site after it.");
    }

    /// <summary>How many bytes an opcode's operand occupies.</summary>
    /// <param name="code">The opcode.</param>
    /// <param name="il">The method body.</param>
    /// <param name="offset">The offset of the operand.</param>
    /// <returns>The operand width in bytes.</returns>
    private static int OperandSize(OpCode code, byte[] il, int offset) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
        _ => throw new InvalidOperationException(
            $"IL operand type {code.OperandType} has no width rule, so the scan cannot advance safely."),
    };

    /// <summary>Every opcode the runtime defines, indexed by its encoded value.</summary>
    /// <returns>The table.</returns>
    private static Dictionary<ushort, OpCode> BuildOpCodeTable() =>
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => (ushort)code.Value);
}
