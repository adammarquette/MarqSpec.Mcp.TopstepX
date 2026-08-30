namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// One method's contribution to a confluence score — its zones, or the reason it contributed nothing.
/// </summary>
/// <param name="Name">The method name, lowercase and stable.</param>
/// <param name="Family">The correlation family the method declared.</param>
/// <param name="Zones">The zones it produced. Empty when it contributed nothing.</param>
/// <param name="AbsentReason">
/// Why the method contributed nothing — refused, no data, or genuinely no levels — or
/// <see langword="null"/> when <paramref name="Zones"/> is the contribution.
/// </param>
public sealed record ConfluenceMethodInput(
    string Name,
    string Family,
    IReadOnlyList<KeyLevelZone> Zones,
    string? AbsentReason = null);

/// <summary>One requested method as the score reports it.</summary>
/// <param name="Method">The method name.</param>
/// <param name="Family">The correlation family.</param>
/// <param name="Weight">The weight the score used for this method.</param>
/// <param name="ZoneCount">How many zones it contributed. Zero when it contributed nothing.</param>
public sealed record ConfluenceConstituent(string Method, string Family, decimal Weight, int ZoneCount);

/// <summary>A requested method that contributed nothing.</summary>
/// <param name="Method">The method name.</param>
/// <param name="Reason">Why — refused, no data, or no levels. These must not collapse.</param>
public sealed record ConfluenceAbsence(string Method, string Reason);

/// <summary>
/// A confluence score and the inputs that produced it.
/// </summary>
/// <param name="Score">The strongest cluster's family-aware weight.</param>
/// <param name="Tolerance">The line-to-zone tolerance the score was computed against.</param>
/// <param name="Constituents">Every requested method, the weight used, and how many zones it gave.</param>
/// <param name="Absent">The requested methods that contributed nothing, and why.</param>
public sealed record ConfluenceResult(
    decimal Score,
    decimal Tolerance,
    IReadOnlyList<ConfluenceConstituent> Constituents,
    IReadOnlyList<ConfluenceAbsence> Absent);

/// <summary>
/// Weighted agreement across named level methods, family-aware.
/// </summary>
/// <remarks>
/// <para>
/// Pure: the same methods, weights and tolerance always produce the same score. Nothing here reads a
/// clock, a store or a configuration singleton — the weights and the tolerance are arguments, which is
/// what keeps two callers with different tolerances from being shown each other's number
/// (ADR-0006 applied to a derived score, gh#259).
/// </para>
/// <para>
/// <b>Families share a budget.</b> Grouping is by <see cref="ConfluenceMethodInput.Family"/>, not by a
/// list of five pivot names: a sixth variant that declares the family is discounted with the rest, and
/// one that forgets to is counted as independent evidence of the period it is arithmetic on.
/// </para>
/// </remarks>
public static class ConfluenceScoring
{
    /// <summary>The reason recorded when a method returned no zones and named no more specific cause.</summary>
    public const string NoLevelsReason = "no levels";

    /// <summary>
    /// Scores the agreement between the requested methods.
    /// </summary>
    /// <param name="methods">The requested methods, in request order.</param>
    /// <param name="weights">Per-method weights. A missing name weighs 1.</param>
    /// <param name="tolerance">
    /// The line-to-zone tolerance the zones were built with. Written on the result; not used to
    /// re-widen anything — agreement is overlap of the zones as handed in.
    /// </param>
    /// <param name="applyFamilyDiscount">
    /// When <see langword="true"/>, methods that share a family contribute the largest weight among
    /// the members that hit the cluster, not the sum. The pin that this is load-bearing is
    /// <c>RemovingTheFamilyDiscount_ReddensTheCaseThatExistsForIt</c>.
    /// </param>
    /// <returns>The score, the tolerance, the constituents and the absences.</returns>
    public static ConfluenceResult Score(
        IReadOnlyList<ConfluenceMethodInput> methods,
        IReadOnlyDictionary<string, decimal> weights,
        decimal tolerance,
        bool applyFamilyDiscount = true)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(weights);

        List<ConfluenceConstituent> constituents = [];
        List<ConfluenceAbsence> absent = [];
        List<(ConfluenceMethodInput Method, KeyLevelZone Zone)> hits = [];

        foreach (ConfluenceMethodInput method in methods)
        {
            ArgumentNullException.ThrowIfNull(method);
            decimal weight = WeightOf(weights, method.Name);
            int zoneCount = method.AbsentReason is null ? method.Zones.Count : 0;
            constituents.Add(new ConfluenceConstituent(method.Name, method.Family, weight, zoneCount));

            if (method.AbsentReason is { } reason)
            {
                absent.Add(new ConfluenceAbsence(method.Name, reason));
                continue;
            }

            if (method.Zones.Count == 0)
            {
                absent.Add(new ConfluenceAbsence(method.Name, NoLevelsReason));
                continue;
            }

            foreach (KeyLevelZone zone in method.Zones)
            {
                hits.Add((method, zone));
            }
        }

        decimal score = StrongestCluster(hits, weights, applyFamilyDiscount);
        return new ConfluenceResult(score, tolerance, constituents, absent);
    }

    private static decimal StrongestCluster(
        IReadOnlyList<(ConfluenceMethodInput Method, KeyLevelZone Zone)> hits,
        IReadOnlyDictionary<string, decimal> weights,
        bool applyFamilyDiscount)
    {
        if (hits.Count == 0)
        {
            return 0m;
        }

        int[] parent = new int[hits.Count];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (int i = 0; i < hits.Count; i++)
        {
            for (int j = i + 1; j < hits.Count; j++)
            {
                if (hits[i].Zone.Overlaps(hits[j].Zone))
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        Dictionary<int, List<ConfluenceMethodInput>> clusters = [];
        for (int i = 0; i < hits.Count; i++)
        {
            int root = Find(i);
            if (!clusters.TryGetValue(root, out List<ConfluenceMethodInput>? members))
            {
                members = [];
                clusters[root] = members;
            }

            if (members.TrueForAll(m => m.Name != hits[i].Method.Name))
            {
                members.Add(hits[i].Method);
            }
        }

        decimal strongest = 0m;
        foreach (List<ConfluenceMethodInput> members in clusters.Values)
        {
            decimal contribution = applyFamilyDiscount
                ? FamilyBudget(members, weights)
                : members.Sum(m => WeightOf(weights, m.Name));
            if (contribution > strongest)
            {
                strongest = contribution;
            }
        }

        return strongest;
    }

    /// <summary>
    /// One budget per family: the largest weight among the members that hit, not the sum.
    /// </summary>
    private static decimal FamilyBudget(
        IReadOnlyList<ConfluenceMethodInput> members,
        IReadOnlyDictionary<string, decimal> weights)
    {
        Dictionary<string, decimal> byFamily = new(StringComparer.Ordinal);
        foreach (ConfluenceMethodInput member in members)
        {
            decimal weight = WeightOf(weights, member.Name);
            if (!byFamily.TryGetValue(member.Family, out decimal current) || weight > current)
            {
                byFamily[member.Family] = weight;
            }
        }

        return byFamily.Values.Sum();
    }

    private static decimal WeightOf(IReadOnlyDictionary<string, decimal> weights, string name)
    {
        if (weights.TryGetValue(name, out decimal weight))
        {
            return weight;
        }

        foreach (KeyValuePair<string, decimal> pair in weights)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return 1m;
    }
}
