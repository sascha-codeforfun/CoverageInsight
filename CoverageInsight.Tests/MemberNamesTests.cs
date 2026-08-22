using System;
using CoverageInsight.Filtering;
using Xunit;

namespace CoverageInsight.Tests;

public class MemberNamesTests
{
    [Theory]
    [InlineData("Worker.<>c", "Worker")]
    [InlineData("Worker.<Run>d__8", "Worker")]
    [InlineData("Worker.<>c__DisplayClass14_0", "Worker")]
    [InlineData("Worker", "Worker")]
    [InlineData("Outer.Inner", "Outer.Inner")]
    public void NormalizeType_drops_generated_segments(string input, string expected)
        => Assert.Equal(expected, MemberNames.NormalizeType(input));

    [Fact]
    public void NormalizeType_keeps_the_original_when_every_segment_is_generated()
        => Assert.Equal("<>c", MemberNames.NormalizeType("<>c"));

    // The generated forms nest, so precedence matters: a local function declared inside
    // an async method must resolve as a local function, not as the state machine.
    [Theory]
    [InlineData("Worker.<RunAsync>d__8", "MoveNext()", "RunAsync", "async")]
    [InlineData("Worker.<>c", "<Build>b__13_7(Item)", "Build", "lambda")]
    [InlineData("Worker.<>c__DisplayClass14_0", "<Read>b__2()", "Read", "lambda")]
    [InlineData("Parser", "<Read>g__Facts|12_1(String)", "Read", "local func Facts")]
    [InlineData("Engine.<Walk>d__3", "<Walk>g__Step|3_0()", "Walk", "local func Step")]
    [InlineData("Registry", "get_Items()", "Items", "accessor")]
    [InlineData("Registry", "set_Items(IList)", "Items", "accessor")]
    [InlineData("Program", "<Main>$(string[])", "Main", "")]
    public void Fold_resolves_the_declaring_method(string type, string method,
                                                   string expectedMethod, string expectedKind)
    {
        var (folded, generated) = MemberNames.Fold(type, method);
        Assert.Equal(expectedMethod, folded);
        Assert.Equal(expectedKind, generated);
    }

    /// <summary>
    /// Regression from a real report: a constructor's lambdas compile to &lt;.ctor&gt;b__n,
    /// which folded to ".ctor" while the constructor itself folded to the type name —
    /// the same member arriving as two rows.
    /// </summary>
    [Fact]
    public void A_constructor_and_its_lambdas_fold_to_the_type_name()
    {
        var ctor = MemberNames.Fold("MainViewModel", "MainViewModel()").Method;
        var lambda = MemberNames.Fold("MainViewModel.<>c", "<.ctor>b__36_0()").Method;

        Assert.Equal("MainViewModel", ctor);
        Assert.Equal("MainViewModel", lambda);
    }

    [Fact]
    public void A_static_constructor_folds_to_the_type_name_too()
        => Assert.Equal("Registry", MemberNames.Fold("Registry", ".cctor()").Method);

    /// <summary>
    /// Regression: the lambda pattern extracted ".ctor" and returned before the
    /// constructor mapping could run, so a constructor's lambdas still arrived as
    /// their own row. Every path that extracts a declaring name must map it.
    /// </summary>
    [Fact]
    public void A_lambda_inside_a_constructor_folds_to_the_type_name()
        => Assert.Equal("MainViewModel",
            MemberNames.Fold("MainViewModel.<>c", "<.ctor>b__36_0()").Method);

    /// <summary>
    /// Regression: type names were split on every dot, but ".ctor" and generic
    /// arguments carry dots inside angle brackets, so the split shredded them.
    /// </summary>
    [Theory]
    [InlineData("Cache.<.ctor>d__2", "Cache")]
    [InlineData("Map<System.Int32>", "Map<System.Int32>")]
    [InlineData("Store<System.String>.<>c__DisplayClass1_0", "Store<System.String>")]
    public void Type_names_split_only_outside_brackets(string input, string expected)
        => Assert.Equal(expected, MemberNames.NormalizeType(input));

    /// <summary>A generic method is a real name, not a generated one.</summary>
    [Fact]
    public void Generic_methods_are_not_treated_as_generated()
    {
        Assert.False(MemberNames.LooksGenerated("Set<T>"));
        Assert.Equal("Set<T>", MemberNames.Fold("Observable", "Set<T>(T, T, string)").Method);
    }

    [Fact]
    public void Ordinary_methods_are_left_alone_apart_from_the_signature()
    {
        var (folded, generated) = MemberNames.Fold("Calculator", "Total(Invoice)");
        Assert.Equal("Total", folded);
        Assert.Equal(string.Empty, generated);
    }

    /// <summary>
    /// Regression. The first implementation kept signatures on real methods but not on
    /// folded ones, so a method and its own lambda stayed separate rows — a half-done
    /// fold that looks like it worked. Both must reduce to the same key.
    /// </summary>
    [Fact]
    public void A_method_and_its_own_lambda_fold_to_the_same_name()
    {
        var real = MemberNames.Fold("Analyzer", "BuildReport(Item)").Method;
        var lambda = MemberNames.Fold("Analyzer.<>c", "<BuildReport>b__9_4(Item)").Method;

        Assert.Equal(real, lambda);
    }

    [Fact]
    public void A_method_and_its_own_async_body_fold_to_the_same_name()
    {
        var real = MemberNames.Fold("Analyzer", "RunAsync(String)").Method;
        var body = MemberNames.Fold("Analyzer.<RunAsync>d__8", "MoveNext()").Method;

        Assert.Equal(real, body);
    }

    [Fact]
    public void Nothing_generated_survives_folding()
    {
        var samples = new[]
        {
            ("Worker.<RunAsync>d__8", "MoveNext()"),
            ("Worker.<>c", "<Build>b__13_7(Item)"),
            ("Parser", "<Read>g__Facts|12_1(String)"),
            ("Program", "<Main>$(string[])")
        };

        foreach (var (type, method) in samples)
        {
            var folded = MemberNames.NormalizeType(type) + "." + MemberNames.Fold(type, method).Method;
            Assert.False(MemberNames.LooksGenerated(folded), folded);
            Assert.DoesNotContain("d__", folded);
            Assert.DoesNotContain("b__", folded);
            Assert.DoesNotContain("g__", folded);
        }
    }
}
