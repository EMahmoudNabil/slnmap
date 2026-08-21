using Fixture.Lib;

// Exercises issue #11: a typeof() inside an ASSEMBLY-LEVEL attribute lives outside every member
// declaration, so GetEnclosingMemberNode finds nothing and the reference was dropped entirely
// (the eShopOnWeb [assembly: HostingStartup(typeof(...))] finding from v0.6.0 QA). The designed
// attribution: the assembly IS the project, so the edge's source is the PROJECT node. The
// fully-qualified spelling below matches the original finding's shape (#4's qualified-name path).
[assembly: AssemblyMarker(typeof(Fixture.Lib.Circle))]

namespace Fixture.Lib;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AssemblyMarkerAttribute : Attribute
{
    public AssemblyMarkerAttribute(Type target) => Target = target;

    public Type Target { get; }
}
