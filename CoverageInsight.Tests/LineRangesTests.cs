using System;
using CoverageInsight.Models;
using Xunit;

namespace CoverageInsight.Tests;

/// <summary>
/// The whole AI CSV rests on this: it turns "which lines are dark" into the one datum a
/// reader can't get from a percentage. It's also pure, so there's no excuse.
/// </summary>
public class LineRangesTests
{
    [Fact]
    public void Empty_input_produces_empty_string()
        => Assert.Equal(string.Empty, LineRanges.Collapse(Array.Empty<int>()));

    [Fact]
    public void Single_line_has_no_dash()
        => Assert.Equal("7", LineRanges.Collapse(new[] { 7 }));

    [Fact]
    public void Consecutive_lines_collapse_into_one_range()
        => Assert.Equal("118-121", LineRanges.Collapse(new[] { 118, 119, 120, 121 }));

    [Fact]
    public void Gaps_split_ranges_and_are_joined_by_semicolons()
        => Assert.Equal("118-121;129-130;155",
            LineRanges.Collapse(new[] { 118, 119, 120, 121, 129, 130, 155 }));

    [Fact]
    public void Unordered_input_is_sorted_first()
        => Assert.Equal("1-3;9", LineRanges.Collapse(new[] { 9, 2, 1, 3 }));

    [Fact]
    public void Duplicates_are_ignored_rather_than_widening_a_range()
        => Assert.Equal("3-4", LineRanges.Collapse(new[] { 3, 3, 4, 4 }));

    [Fact]
    public void Two_adjacent_lines_still_render_as_a_range()
        => Assert.Equal("10-11", LineRanges.Collapse(new[] { 10, 11 }));
}
