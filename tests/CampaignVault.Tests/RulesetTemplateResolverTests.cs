using System;
using System.Collections.Generic;
using CampaignVault.Data.Templates;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetTemplateResolverTests
{
    // Minimal concrete template used across all tests in this file
    private record TestTemplate : RulesetTemplate
    {
        public string? FieldA { get; init; }
        public Dictionary<string, int>? NumericMap { get; init; }
    }

    private static TestTemplate Merge(TestTemplate child, TestTemplate parent) => child with
    {
        Description = child.Description ?? parent.Description,
        FieldA = child.FieldA ?? parent.FieldA,
        NumericMap = MergeMaps(child.NumericMap, parent.NumericMap)
    };

    private static Dictionary<string, int>? MergeMaps(
        Dictionary<string, int>? child,
        Dictionary<string, int>? parent)
    {
        if (parent == null) return child;
        if (child == null) return new Dictionary<string, int>(parent);
        var merged = new Dictionary<string, int>(child);
        foreach (var (k, v) in parent)
            merged.TryAdd(k, v);
        return merged;
    }

    private static RulesetTemplateResolver<TestTemplate> BuildResolver(
        params TestTemplate[] templates)
    {
        var dict = new Dictionary<string, TestTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates)
            dict[t.Name] = t;
        return new RulesetTemplateResolver<TestTemplate>(
            name => dict.TryGetValue(name, out var t) ? t : null,
            Merge);
    }

    [Fact]
    public void Resolve_NoInheritance_ReturnsSelf()
    {
        var template = new TestTemplate { Name = "a", FieldA = "hello" };
        var resolver = BuildResolver(template);

        var result = resolver.Resolve(template);

        Assert.Equal("hello", result.FieldA);
    }

    [Fact]
    public void Resolve_SingleParent_ChildFieldWins()
    {
        var parent = new TestTemplate { Name = "parent", FieldA = "from_parent" };
        var child = new TestTemplate { Name = "child", FieldA = "from_child", Inherits = ["parent"] };
        var resolver = BuildResolver(parent, child);

        var result = resolver.Resolve(child);

        Assert.Equal("from_child", result.FieldA);
    }

    [Fact]
    public void Resolve_SingleParent_ParentFillsNullChildField()
    {
        var parent = new TestTemplate { Name = "parent", FieldA = "from_parent", Description = "parent desc" };
        var child = new TestTemplate { Name = "child", Inherits = ["parent"] };
        var resolver = BuildResolver(parent, child);

        var result = resolver.Resolve(child);

        Assert.Equal("from_parent", result.FieldA);
        Assert.Equal("parent desc", result.Description);
    }

    [Fact]
    public void Resolve_NestedDict_ChildKeyWins_ParentFillsGaps()
    {
        var parent = new TestTemplate
        {
            Name = "parent",
            NumericMap = new() { { "1", 10 }, { "2", 20 }, { "5", 50 } }
        };
        var child = new TestTemplate
        {
            Name = "child",
            NumericMap = new() { { "1", 99 } }, // overrides level 1
            Inherits = ["parent"]
        };
        var resolver = BuildResolver(parent, child);

        var result = resolver.Resolve(child);

        Assert.Equal(99, result.NumericMap!["1"]);   // child wins
        Assert.Equal(20, result.NumericMap["2"]);    // parent fills gap
        Assert.Equal(50, result.NumericMap["5"]);    // parent fills gap
    }

    [Fact]
    public void Resolve_TransitiveChain_GrandparentValues_Inherited()
    {
        var grandparent = new TestTemplate { Name = "gp", FieldA = "from_gp", Description = "gp desc" };
        var parent = new TestTemplate { Name = "p", FieldA = "from_p", Inherits = ["gp"] };
        var child = new TestTemplate { Name = "c", Inherits = ["p"] };
        var resolver = BuildResolver(grandparent, parent, child);

        var result = resolver.Resolve(child);

        Assert.Equal("from_p", result.FieldA);       // parent wins over grandparent
        Assert.Equal("gp desc", result.Description); // grandparent fills gap all the way up
    }

    [Fact]
    public void Resolve_MissingBase_ThrowsKeyNotFoundException()
    {
        var child = new TestTemplate { Name = "orphan", Inherits = ["nonexistent"] };
        var resolver = BuildResolver(child);

        Assert.Throws<KeyNotFoundException>(() => resolver.Resolve(child));
    }

    [Fact]
    public void Resolve_Cycle_ThrowsInvalidOperationException()
    {
        // A → B → A
        var a = new TestTemplate { Name = "a", Inherits = ["b"] };
        var b = new TestTemplate { Name = "b", Inherits = ["a"] };
        var resolver = BuildResolver(a, b);

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(a));
    }
}
