using Fixture.Lib;

namespace Fixture.App;

// File 3 of 3 reading VendorActivityTypes.Deactivated — from a DIFFERENT project, so the
// const's usage edges cross the incremental-eviction boundary (the OSSUS "tests" role).
public sealed class VendorAudit
{
    public bool IsDeactivation(string type) => type == VendorActivityTypes.Deactivated;
}
