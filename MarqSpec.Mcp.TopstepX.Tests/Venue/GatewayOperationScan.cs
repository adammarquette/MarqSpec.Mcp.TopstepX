using System.Reflection;
using System.Reflection.Emit;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Reads the compiled body of a gateway and answers <i>which string the <c>operation</c> argument of every
/// <see cref="VenueCallGuard.RunAsync{T}"/> call site is</i> (gh#559).
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
/// <b>It identifies the first argument, rather than searching near the call.</b> The first version of this
/// scan collected the literals loaded since the previous guard call and required exactly one of them to be a
/// vocabulary value, on the reasoning that the other literals would be prose. <b>That was a heuristic wearing
/// a proof's wording, and one ordinary logging line defeated it both ways</b>: a
/// <c>_logger.LogDebug("Issuing {Operation}", VenueOperation.GetAccounts)</c> before an invented
/// <c>RunAsync("list_accounts", …)</c> supplied the vocabulary value the rule was looking for and the gate
/// went <i>green on the invented literal</i>; the same log line above a <i>correct</i> call made the count two
/// and the gate went <i>red on correct code</i>. A gate that cries wolf is deleted, which is the slower
/// version of the same failure (PR #575 review).
/// </para>
/// <para>
/// <b>So the argument is located by stack depth</b>, which is a property of the IL rather than of how the
/// surrounding statements happen to be written. Each instruction's effect on the evaluation stack is read
/// from <see cref="OpCode.StackBehaviourPop"/> and <see cref="OpCode.StackBehaviourPush"/>, with the
/// variable-width cases (<c>call</c>, <c>callvirt</c>, <c>newobj</c>) taken from the resolved callee's
/// parameter count and return type. Every slot is remembered as either the literal that produced it or
/// nothing, so at a guard call the three arguments and the receiver are the top four slots and the operation
/// is the one below the lambda and the prose. No full evaluator is needed, because nothing here has to know
/// what any other instruction <i>computes</i> — only how many slots it moves.
/// </para>
/// <para>
/// <b>The walk is linear, so a slot that two paths can fill is treated as unknown.</b> This is the second
/// correction the review forced and the second false green of the same family: <c>name ?? VenueOperation
/// .GetAccounts</c> lowers to <c>dup; brtrue L; pop; ldstr "get_accounts"; L:</c> with <i>no</i>
/// <c>br</c> in it, so nothing emptied the stack and the walk carried the fallback literal into the operation
/// slot — answering <c>get_accounts</c> for a call site that names <c>"list_accounts"</c> on its other path.
/// The rule now is that reaching any offset something can branch to (see <see cref="BranchTargets"/>) keeps
/// the slot <i>count</i>, which the runtime guarantees, and forgets the slot <i>contents</i>, which only one
/// followed path could have supplied. A ternary, an interpolated string and a <c>switch</c> expression in the
/// operation position are the same mechanism and refuse for the same reason.
/// </para>
/// <para>
/// <b>What it decides, exactly.</b> For every <see cref="VenueCallGuard.RunAsync{T}"/> call site in the type,
/// it either answers with the single string literal standing in the operation slot, or refuses. So the set it
/// returns <i>is</i> the set of operation strings named by the call sites it could read, and — because a call
/// site it could not read is a failure rather than an omission — comparing that set to the vocabulary decides
/// both directions: a name the vocabulary does not know, and a vocabulary value nothing names. It decides
/// <b>nothing</b> about an operation that is not one literal in the argument itself: a value forwarded from a
/// parameter or a field, or one that can arrive from more than one path, is reported as unreadable rather
/// than answered, because the scan cannot see through either.
/// </para>
/// <para>
/// <b>It fails when it finds nothing, and when it loses its place.</b> An unreadable opcode, an unresolvable
/// callee, a guard call with fewer than four slots beneath it, or a receiver slot holding a literal — which
/// would mean the depth tracking had drifted — each throw rather than being skipped, and the caller asserts
/// the result is non-empty before comparing it to anything.
/// </para>
/// </remarks>
internal static class GatewayOperationScan
{
    /// <summary>Slots the guard call takes: the receiver, then <c>operation</c>, <c>call</c>, <c>what</c>.</summary>
    private const int GuardCallSlots = 4;

    /// <summary>How far below the top of the stack the <c>operation</c> argument sits.</summary>
    private const int OperationFromTop = 3;

    private static readonly Dictionary<ushort, OpCode> _opCodes = BuildOpCodeTable();

    /// <summary>The <c>operation</c> literal at every <see cref="VenueCallGuard"/> call site in a type.</summary>
    /// <param name="gateway">The gateway type to read.</param>
    /// <returns>One entry per call site, in the order the bodies were walked.</returns>
    /// <exception cref="InvalidOperationException">
    /// A call site's operation is not a string literal, or the body could not be read with confidence.
    /// </exception>
    internal static IReadOnlyList<string> OperationsNamedBy(Type gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        List<string> named = [];

        foreach (Type type in WithNestedTypes(gateway))
        {
            foreach (MethodBase method in Bodies(type))
            {
                ScanBody(type, method, named);
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

    /// <summary>Walks one body, tracking the stack, and appends the operation of every guard call in it.</summary>
    /// <param name="type">The type the body belongs to, for messages.</param>
    /// <param name="method">The method to walk.</param>
    /// <param name="named">Where the operations go.</param>
    private static void ScanBody(Type type, MethodBase method, List<string> named)
    {
        MethodBody body = method.GetMethodBody()!;
        byte[]? il = body.GetILAsByteArray();
        if (il is null)
        {
            return;
        }

        Module module = type.Module;
        Type[]? typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        // Where the runtime hands a block a stack this walk did not build: a protected region starts empty,
        // and a catch or filter starts with the exception object on it.
        Dictionary<int, int> blockStarts = BlockStarts(body);

        // Every offset some instruction can jump to. Reaching one means two paths meet here and this walk
        // followed only one of them, so what the slots HOLD is no longer knowable -- see Blur.
        HashSet<int> joins = BranchTargets(il);

        // One entry per stack slot: the literal that produced it, or null for everything else. Only the
        // COUNT has to be right for the arguments to line up; the values are what makes an answer possible.
        List<string?> stack = [];

        // Whether control flow has met or left mid-expression -- with slots still on the stack -- since the
        // stack was last empty. A ternary or an interpolated string in an ARGUMENT does that, and no linear
        // walk can know the depth on the other side of it, so this is what tells a depth mismatch below
        // whether the walk lost its place or the argument list contained a branch.
        bool branchedMidExpression = false;
        int offset = 0;

        while (offset < il.Length)
        {
            if (blockStarts.TryGetValue(offset, out int handed))
            {
                stack.Clear();
                for (int slot = 0; slot < handed; slot++)
                {
                    stack.Add(null);
                }
            }
            else if (joins.Contains(offset))
            {
                branchedMidExpression |= stack.Count > 0;
                Blur(stack);
            }

            OpCode code = Read(il, ref offset);
            int operand = offset;

            if (code == OpCodes.Ldstr)
            {
                Move(stack, pop: 0, push: 1, module.ResolveString(BitConverter.ToInt32(il, operand)));
            }
            else if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
            {
                MethodBase callee = Resolve(
                    module, BitConverter.ToInt32(il, operand), typeArguments, methodArguments, type, method);

                bool constructing = code == OpCodes.Newobj;
                int arguments = callee.GetParameters().Length;
                int pop = constructing ? arguments : arguments + (callee.IsStatic ? 0 : 1);
                int push = constructing || callee is MethodInfo { ReturnType.FullName: not "System.Void" }
                    ? 1
                    : 0;

                if (!constructing && IsGuard(callee))
                {
                    named.Add(OperationAt(stack, type, method, branchedMidExpression));
                }

                Move(stack, pop, push, literal: null);
            }
            else if (Empties(code))
            {
                // After a return, a throw, or an unconditional transfer, the next instruction linearly is the
                // start of another block, and the stack this walk was carrying belongs to neither.
                branchedMidExpression |= stack.Count > 0;
                stack.Clear();
            }
            else
            {
                Move(stack, PopCount(code, type, method), PushCount(code, type, method), literal: null);
            }

            // An empty stack is a statement boundary, and whatever the control flow did inside the last one
            // cannot reach across it.
            if (stack.Count == 0)
            {
                branchedMidExpression = false;
            }

            offset += OperandSize(code, il, offset);
        }
    }

    /// <summary>Reads the operation argument off the stack at a guard call.</summary>
    /// <param name="stack">The tracked stack, with the receiver and three arguments on top.</param>
    /// <param name="type">The type, for the message.</param>
    /// <param name="method">The method, for the message.</param>
    /// <param name="branchedMidExpression">
    /// Whether control flow met or left mid-expression since the stack was last empty — which is what a
    /// ternary or an interpolated string inside the argument list looks like from here.
    /// </param>
    /// <returns>The operation literal.</returns>
    /// <exception cref="InvalidOperationException">
    /// The operation is not a literal, or the tracked depth is not what a guard call must look like.
    /// </exception>
    private static string OperationAt(
        List<string?> stack,
        Type type,
        MethodBase method,
        bool branchedMidExpression)
    {
        string where = type.Name + "." + method.Name;

        if (stack.Count < GuardCallSlots)
        {
            throw new InvalidOperationException(
                $"{where} calls VenueCallGuard.RunAsync with {stack.Count} tracked stack slot(s) beneath it "
                + $"where {GuardCallSlots} are required (the receiver and three arguments). "
                + (branchedMidExpression
                    ? "CONTROL FLOW BRANCHED INSIDE THIS ARGUMENT LIST -- a ternary or an interpolated "
                    + "string in one of the arguments -- and no linear walk can know the depth on the other "
                    + "side of it, so the scan refuses rather than reading whichever slot it happens to be "
                    + "pointing at. THIS IS NOT ITSELF A DEFECT IN THE OPERATION NAME: name the operation as "
                    + "one `VenueOperation` constant in the argument itself, computing anything else into a "
                    + "local first."
                    : "The scan has lost its place in this body rather than found a defect in it."));
        }

        // An integrity check on the depth tracking itself: the receiver is `ldfld _calls`, so its slot can
        // never hold a literal. If it does, the count drifted and every answer from this body is suspect.
        if (stack[^GuardCallSlots] is string receiver)
        {
            throw new InvalidOperationException(
                $"{where} calls VenueCallGuard.RunAsync with the string literal \"{receiver}\" tracked in the "
                + "RECEIVER slot, which cannot happen. The scan's stack tracking has drifted in this body, so "
                + "its answers cannot be trusted -- fix the walk rather than the gateway.");
        }

        return stack[^OperationFromTop]
            ?? throw new InvalidOperationException(
                $"{where} calls VenueCallGuard.RunAsync with an operation this scan cannot read as a single "
                + "string literal, so it cannot say which operation the call site names. THIS IS NOT ITSELF "
                + "A DEFECT IN THE OPERATION NAME -- the two causes are: (a) the operation is FORWARDED from "
                + "a parameter or a field, which is what a private guarding helper does; or (b) it ARRIVES "
                + "FROM MORE THAN ONE PATH -- `a ?? VenueOperation.X`, a ternary, an interpolated string, a "
                + "`switch` expression -- where the literal this walk happened to follow is only one of the "
                + "values that can reach the call. Both have to fail: a helper hides every call site behind "
                + "it, and a join hides every branch but one, and answering from either is how "
                + "`name ?? VenueOperation.GetAccounts` reported `get_accounts` for a site that also names "
                + "`\"list_accounts\"` (PR #575 review). Name the operation as one `VenueOperation` constant "
                + "in the argument itself, or teach this scan to follow the shape you need.");
    }

    /// <summary>Applies one instruction's effect to the tracked stack.</summary>
    /// <param name="stack">The tracked stack.</param>
    /// <param name="pop">How many slots it consumes.</param>
    /// <param name="push">How many slots it produces.</param>
    /// <param name="literal">The literal a pushed slot carries, if any.</param>
    private static void Move(List<string?> stack, int pop, int push, string? literal)
    {
        // A pop deeper than the tracked stack means this block was entered on a path the walk did not
        // follow. Clamping rather than throwing keeps the walk going; the receiver-slot check above is what
        // refuses to ANSWER from a body whose depth is wrong.
        stack.RemoveRange(Math.Max(0, stack.Count - pop), Math.Min(pop, stack.Count));

        for (int slot = 0; slot < push; slot++)
        {
            stack.Add(literal);
        }
    }

    /// <summary>Forgets what the tracked slots hold, keeping how many there are.</summary>
    /// <param name="stack">The tracked stack.</param>
    /// <remarks>
    /// <b>At a join, the depth is still knowable and the contents are not.</b> Both paths into a label leave
    /// the same number of slots — the runtime requires it — but this walk followed one of them, so whatever
    /// literal it is carrying is only one of the values that can arrive here. Blurring turns that into "not a
    /// literal", which makes a guard call beneath a join <i>refuse</i> rather than answer.
    /// <para>
    /// <b>This is the fix for <c>name ?? VenueOperation.GetAccounts</c></b>, which C# lowers to
    /// <c>dup; brtrue L; pop; ldstr "get_accounts"; L:</c> — with no <c>br</c> anywhere, so nothing emptied
    /// the stack and a linear walk carried the <i>fallback</i> literal into the operation slot and answered
    /// <c>get_accounts</c> for a call site that names <c>"list_accounts"</c> on its other path. Blurring at
    /// <c>L</c> is what turns that back into a refusal (PR #575 review). The same rule covers a ternary, an
    /// interpolated string and a <c>switch</c> expression in the operation position, which are joins by the
    /// same mechanism.
    /// </para>
    /// </remarks>
    private static void Blur(List<string?> stack)
    {
        for (int slot = 0; slot < stack.Count; slot++)
        {
            stack[slot] = null;
        }
    }

    /// <summary>Every offset in a body that some instruction can branch to.</summary>
    /// <param name="il">The method body.</param>
    /// <returns>The join points.</returns>
    /// <remarks>
    /// A separate pass because a target may be behind the walk as well as ahead of it, and the second pass
    /// has to know about both before it reaches either.
    /// </remarks>
    private static HashSet<int> BranchTargets(byte[] il)
    {
        HashSet<int> targets = [];
        int offset = 0;

        while (offset < il.Length)
        {
            OpCode code = Read(il, ref offset);
            int operand = offset;
            int size = OperandSize(code, il, operand);
            int next = operand + size;

            switch (code.OperandType)
            {
                case OperandType.ShortInlineBrTarget:
                    targets.Add(next + (sbyte)il[operand]);
                    break;

                case OperandType.InlineBrTarget:
                    targets.Add(next + BitConverter.ToInt32(il, operand));
                    break;

                case OperandType.InlineSwitch:
                    int cases = BitConverter.ToInt32(il, operand);
                    for (int index = 0; index < cases; index++)
                    {
                        targets.Add(next + BitConverter.ToInt32(il, operand + 4 + (index * 4)));
                    }

                    break;

                default:
                    break;
            }

            offset = next;
        }

        return targets;
    }

    /// <summary>Whether an instruction leaves the following one at the start of a new block.</summary>
    /// <param name="code">The opcode.</param>
    /// <returns>Whether the tracked stack should be dropped.</returns>
    private static bool Empties(OpCode code) =>
        code == OpCodes.Ret
        || code == OpCodes.Throw
        || code == OpCodes.Rethrow
        || code == OpCodes.Br
        || code == OpCodes.Br_S
        || code == OpCodes.Leave
        || code == OpCodes.Leave_S
        || code == OpCodes.Endfinally
        || code == OpCodes.Endfilter;

    /// <summary>Where the runtime, rather than this walk, decides what a block starts with.</summary>
    /// <param name="body">The method body.</param>
    /// <returns>Offset to the number of slots handed to the block starting there.</returns>
    private static Dictionary<int, int> BlockStarts(MethodBody body)
    {
        Dictionary<int, int> starts = [];

        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            starts[clause.TryOffset] = 0;

            // A catch handler, a filter block AND a filter's handler are each entered with the exception
            // object already on the stack; a finally or fault handler is entered with an empty one.
            bool carriesTheException =
                clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter;

            starts[clause.HandlerOffset] = carriesTheException ? 1 : 0;

            if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
            {
                starts[clause.FilterOffset] = 1;
            }
        }

        return starts;
    }

    /// <summary>Whether a resolved callee is the guard's funnel.</summary>
    /// <param name="callee">The resolved method.</param>
    /// <returns>Whether it is <see cref="VenueCallGuard.RunAsync{T}"/>.</returns>
    private static bool IsGuard(MethodBase callee) =>
        callee.DeclaringType == typeof(VenueCallGuard)
        && callee.Name == nameof(VenueCallGuard.RunAsync);

    /// <summary>Resolves a method token, or refuses.</summary>
    /// <param name="module">The module the token belongs to.</param>
    /// <param name="token">The metadata token.</param>
    /// <param name="typeArguments">The declaring type's generic arguments, if any.</param>
    /// <param name="methodArguments">The method's generic arguments, if any.</param>
    /// <param name="type">The type being walked, for the message.</param>
    /// <param name="method">The method being walked, for the message.</param>
    /// <returns>The callee.</returns>
    /// <exception cref="InvalidOperationException">The token could not be resolved.</exception>
    /// <remarks>
    /// <b>A callee that will not resolve is a failure, not a skip.</b> Its parameter count is what keeps the
    /// stack depth honest, so guessing it would silently misplace every argument after it.
    /// </remarks>
    private static MethodBase Resolve(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments,
        Type type,
        MethodBase method)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments)
                ?? throw new InvalidOperationException("resolved to nothing");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not resolve the callee at token 0x{token:X8} in {type.Name}.{method.Name}, so its "
                + "argument count is unknown and the stack depth after it would be a guess. The scan refuses "
                + "rather than continuing with a depth it cannot justify.",
                ex);
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

    /// <summary>How many stack slots an instruction consumes.</summary>
    /// <param name="code">The opcode.</param>
    /// <param name="type">The type being walked, for the message.</param>
    /// <param name="method">The method being walked, for the message.</param>
    /// <returns>The count.</returns>
    private static int PopCount(OpCode code, Type type, MethodBase method) => code.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            or StackBehaviour.Popref_popi_pop1 => 3,
        _ => throw new InvalidOperationException(
            $"Opcode {code.Name} in {type.Name}.{method.Name} pops a variable number of slots "
            + $"({code.StackBehaviourPop}) and this walk has no rule for it, so the depth after it would be a "
            + "guess."),
    };

    /// <summary>How many stack slots an instruction produces.</summary>
    /// <param name="code">The opcode.</param>
    /// <param name="type">The type being walked, for the message.</param>
    /// <param name="method">The method being walked, for the message.</param>
    /// <returns>The count.</returns>
    private static int PushCount(OpCode code, Type type, MethodBase method) => code.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => throw new InvalidOperationException(
            $"Opcode {code.Name} in {type.Name}.{method.Name} pushes a variable number of slots "
            + $"({code.StackBehaviourPush}) and this walk has no rule for it, so the depth after it would be "
            + "a guess."),
    };

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
