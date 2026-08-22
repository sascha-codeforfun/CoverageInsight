using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoverageInsight.Filtering;

/// <summary>
/// Maps compiler-generated members back to the source method that declared them.
///
/// An async method, an iterator, a lambda or a local function is compiled into
/// separate members on separate nested types, so a single source method's uncovered
/// lines arrive spread across a dozen rows — none individually large enough to rank.
/// Folding them restores the method as the unit a person actually edits.
///
/// The generated forms nest, so order matters: a local function must be tested before
/// a lambda, and a lambda before a state machine.
/// </summary>
public static class MemberNames
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // <Foo>g__Bar|0_1  — local function Bar declared in Foo
    private static readonly Regex LocalFunction = new(@"<(?<name>[^>]+)>g__(?<local>[^|]+)\|", Opts);

    // <Foo>b__13_7  — lambda declared in Foo
    private static readonly Regex Lambda = new(@"<(?<name>[^>]+)>b__", Opts);

    // <Foo>d__12  — async or iterator body of Foo
    private static readonly Regex StateMachine = new(@"<(?<name>[^>]+)>d__\d+", Opts);

    // get_Foo / set_Foo, after the signature has been stripped
    private static readonly Regex Accessor = new(@"^(?<kind>get|set)_(?<name>.+)$", Opts);

    // <Main>$ and similar single-name wrappers
    private static readonly Regex Wrapper = new(@"^<(?<name>[^>]+)>\$?$", Opts);

    /// <summary>
    /// Drops generated nested segments so members land under the type a person wrote:
    /// "Worker.&lt;&gt;c" and "Worker.&lt;Run&gt;d__8" both become "Worker".
    /// </summary>
    public static string NormalizeType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || typeName.IndexOf('<') < 0)
            return typeName;

        var kept = SplitTopLevel(typeName)
            .Where(segment => !segment.StartsWith("<", StringComparison.Ordinal))
            .ToArray();
        return kept.Length == 0 ? typeName : string.Join(".", kept);
    }

    /// <summary>
    /// Returns the declaring source method for a possibly-generated member, plus a short
    /// label for what was folded ("lambda", "async", "local func Parse", "accessor"),
    /// which is what tells a reader where inside the method the dark lines sit.
    ///
    /// Parameter lists are dropped. A generated member's IL name carries only the
    /// declaring method's name, so keeping signatures elsewhere would leave the real
    /// method and its own lambdas as separate rows — the exact half-done fold this is
    /// meant to prevent. The cost is that overloads merge into one row, and an IL name
    /// couldn't have told them apart anyway.
    /// </summary>
    public static (string Method, string Generated) Fold(string? typeName, string methodName)
    {
        // Coalesced once, up front: doing it later marks typeName as maybe-null for the
        // rest of the method and every use after it needs re-checking.
        var type = typeName ?? string.Empty;
        var generated = string.Empty;
        var name = StripSignature(methodName);

        // 1. the method name itself carries the declaring name
        var match = LocalFunction.Match(name);
        if (match.Success)
            return Resolve(match.Groups["name"].Value, type,
                           "local func " + match.Groups["local"].Value.Trim());

        match = Lambda.Match(name);
        if (match.Success)
            return Resolve(match.Groups["name"].Value, type, "lambda");

        match = StateMachine.Match(name);
        if (match.Success)
            return Resolve(match.Groups["name"].Value, type, "async");

        // 2. otherwise the nested type does — e.g. type <Run>d__8, method MoveNext()
        foreach (var segment in SplitTopLevel(type))
        {
            var stateMachine = StateMachine.Match(segment);
            if (stateMachine.Success)
                return Resolve(stateMachine.Groups["name"].Value, type, "async");

            var lambda = Lambda.Match(segment);
            if (lambda.Success)
                return Resolve(lambda.Groups["name"].Value, type, "lambda");

            if (segment.StartsWith("<>c", StringComparison.Ordinal))
                generated = "lambda";
        }

        // 3. a bare constructor
        if (name is ".ctor" or ".cctor")
            return (SimpleTypeName(type), generated);

        // 4. property and event accessors fold into the member they belong to
        var accessor = Accessor.Match(name);
        if (accessor.Success)
            return (accessor.Groups["name"].Value, "accessor");

        var wrapper = Wrapper.Match(name);
        if (wrapper.Success)
            return (wrapper.Groups["name"].Value, generated);

        return (name, generated);
    }

    /// <summary>
    /// Whether a name still carries compiler-generated markers. Deliberately not a plain
    /// angle-bracket test: a generic method like Set&lt;T&gt; is a real, readable name.
    /// </summary>
    public static bool LooksGenerated(string value)
        => !string.IsNullOrEmpty(value)
           && (value.Contains("d__") || value.Contains("b__") || value.Contains("g__")
               || value.Contains("<>c") || value.Contains(">$"));

    /// <summary>
    /// Splits on dots that sit outside angle brackets. Both "&lt;.ctor&gt;d__2" and a generic
    /// argument like "List&lt;System.Int32&gt;" carry dots inside the brackets, and a plain
    /// Split('.') shreds them into fragments that match nothing.
    /// </summary>
    private static List<string> SplitTopLevel(string value)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '<' or '[': depth++; break;
                case '>' or ']': depth--; break;
                case '.' when depth == 0:
                    segments.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }

        segments.Add(value[start..]);
        return segments;
    }

    /// <summary>
    /// Applies the constructor mapping to a name pulled out of a generated member.
    /// A lambda inside a constructor compiles to &lt;.ctor&gt;b__n, so the declaring name
    /// comes back as ".ctor" — it has to become the type's name here, on every path that
    /// extracts one, or the constructor and its own lambdas stay separate rows.
    /// </summary>
    private static (string, string) Resolve(string declaring, string typeName, string generated)
        => declaring is ".ctor" or ".cctor"
            ? (SimpleTypeName(typeName), generated)
            : (declaring, generated);

    /// <summary>Last segment of the normalized type, which is how a constructor is written.</summary>
    private static string SimpleTypeName(string typeName)
    {
        var normalized = NormalizeType(typeName);
        var dot = normalized.LastIndexOf('.');
        return dot < 0 ? normalized : normalized[(dot + 1)..];
    }

    private static string StripSignature(string methodName)
    {
        var paren = methodName.IndexOf('(');
        return paren < 0 ? methodName : methodName[..paren];
    }
}
