using System.ComponentModel.DataAnnotations;

[assembly: Fixture.Lib.Attributes.UsageAssemblyProbe("fixture")]

// v0.14.0 fixture for get_attribute_usages (docs/EXPANSION-SPECS.md §10): one attribute target per
// shape, plus two same-named attribute types in different namespaces (the ambiguous short name).
namespace Fixture.Lib.Attributes
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class UsageAssemblyProbeAttribute : Attribute
    {
        public UsageAssemblyProbeAttribute(string label) => Label = label;

        public string Label { get; }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class UsageProbeAttribute : Attribute
    {
    }

    [UsageProbe]
    public sealed class DecoratedType
    {
        [UsageProbe]
        public int MultiA, MultiB;

        [UsageProbe]
        public string Name { get; set; } = string.Empty;

        // watch fixture: WatchCommandTests swaps this for [Required] — same length, both external —
        // so the edit changes ONLY an attribute usage, never a node or an edge.
        [Obsolete]
        public string WatchSwap { get; set; } = string.Empty;

        [UsageProbe]
        public void Method([UsageProbe] int parameter)
        {
            Func<int, int> lambda = [UsageProbe] (int x) => x + parameter;
            _ = lambda(1);
        }

        [Required]
        public string Validated { get; set; } = string.Empty;

        // QA finding 8: a destructor is not a graph node; its attribute must fall back to the
        // enclosing type, never be dropped.
        [UsageProbe]
        ~DecoratedType()
        {
        }
    }
}

namespace Fixture.Lib.Attributes.Other
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class UsageProbeAttribute : Attribute
    {
    }

    [UsageProbe]
    public sealed class DecoratedByTheOtherProbe
    {
    }
}
