using Fixture.Lib;

namespace Fixture.App;

// File 3 of 3 reading SellerActivityTypes.Deactivated — from a DIFFERENT project, so the
// const's usage edges cross the incremental-eviction boundary (the "tests" role).
public sealed class SellerAudit
{
    public bool IsDeactivation(string type) => type == SellerActivityTypes.Deactivated;
}
