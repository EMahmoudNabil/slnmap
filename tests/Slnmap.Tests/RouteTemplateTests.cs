using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The route-normalization golden suite — ported from the old prototype's PathSkeletonTests
/// (reports/frontend-feasibility-spike.md §4: "port them verbatim as the round-trip contract"),
/// minus the frontend-only rules that do not exist in slnmap v1 (no ${...} template variables, no
/// unresolvable-URL sentinels, no BFF/api prefix stripping — slnmap templates are stored composed
/// and matched whole). Normalization is a query-time concern of find_endpoint; node identity stays
/// as authored.
/// </summary>
public sealed class RouteTemplateTests
{
    // ── Group 1: basic cases ─────────────────────────────────────────────────
    [Theory]
    [InlineData("/vendors", "vendors")]
    [InlineData("/api/Vendors", "api/vendors")]
    [InlineData("/api/Vendors/", "api/vendors")]
    [InlineData("vendors", "vendors")]
    // ── Group 2: dynamic segments ────────────────────────────────────────────
    [InlineData("/api/Vendors/{vendorId}", "api/vendors/{x}")]
    [InlineData("/Users/{userId}/Permissions/{permId}", "users/{x}/permissions/{x}")]
    [InlineData("/api/Vendors/{id:int}", "api/vendors/{x}")]
    [InlineData("/{month:int}/{year:int}", "{x}/{x}")]
    // ── Group 3: query strings stripped ──────────────────────────────────────
    [InlineData("/vendors?status=active", "vendors")]
    [InlineData("/api/Vendors?page=1&pageSize=10", "api/vendors")]
    // ── Group 4: real field examples (OSSUS census shapes) ───────────────────
    [InlineData("/api/Authentication/reset-token/{token}/detail", "api/authentication/reset-token/{x}/detail")]
    [InlineData("/api/Incidents/{incidentId:int}/evidence/{fileId:int}/download", "api/incidents/{x}/evidence/{x}/download")]
    [InlineData("/api/Files/public/organization/{slug}/logo/{theme}", "api/files/public/organization/{x}/logo/{x}")]
    // ── Group 5: edge cases ──────────────────────────────────────────────────
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("///vendors///notes///", "vendors/notes")]
    // ── Group 6: special characters ──────────────────────────────────────────
    [InlineData("/api/Files/{fileId}.pdf", "api/files/{x}.pdf")]
    // ── Group 7: hash fragments ──────────────────────────────────────────────
    [InlineData("/vendors#section", "vendors")]
    // ── Group 8: case normalization ──────────────────────────────────────────
    [InlineData("/api/VENDORS", "api/vendors")]
    [InlineData("/api/UserProfiles/current", "api/userprofiles/current")]
    // ── Group 10: combinations ───────────────────────────────────────────────
    [InlineData("/api/vendors/{id}?include=notes#top", "api/vendors/{x}")]
    [InlineData("/api/Vendors/{vendorId}/Tags/{tagId}", "api/vendors/{x}/tags/{x}")]
    public void Normalize_GoldenSuite(string input, string expected) =>
        Assert.Equal(expected, RouteTemplate.Normalize(input));

    [Fact]
    public void Normalize_NullOrWhitespace_IsEmpty()
    {
        Assert.Equal("", RouteTemplate.Normalize("   "));
        Assert.Equal("", RouteTemplate.Normalize(null!));
    }

    // ── Matching: the framework's own semantics ──────────────────────────────

    [Theory]
    // exact
    [InlineData("/api/vendors", "/api/vendors", true)]
    // case-insensitivity is the framework's semantics, not a heuristic
    [InlineData("/api/Vendors", "/api/VENDORS", true)]
    // a {param} hole binds a concrete segment (the request-path direction)
    [InlineData("/api/vendors/{id}", "/api/vendors/42", true)]
    [InlineData("/api/vendors/{id:int}", "/api/vendors/42", true)]
    // and the reverse: a hole in the query matches a template's concrete segment
    [InlineData("/api/UserProfiles/current", "/api/userprofiles/{x}", true)]
    // holes match holes even under different names/constraints
    [InlineData("/api/vendors/{vendorId:int}", "/api/vendors/{id}", true)]
    // segment counts must agree
    [InlineData("/api/vendors/{id}", "/api/vendors", false)]
    [InlineData("/api/vendors", "/api/vendors/42", false)]
    // concrete segments must agree
    [InlineData("/api/vendors/{id}", "/api/incidents/42", false)]
    // trailing slash and duplicate slashes are normalization, not difference
    [InlineData("/api/vendors/", "//api//vendors", true)]
    // root
    [InlineData("/", "", true)]
    public void Matches_FrameworkSemantics(string template, string query, bool expected) =>
        Assert.Equal(
            expected,
            RouteTemplate.Matches(RouteTemplate.Normalize(template), RouteTemplate.Normalize(query)));
}
