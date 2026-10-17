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
    [InlineData("/sellers", "sellers")]
    [InlineData("/api/Sellers", "api/sellers")]
    [InlineData("/api/Sellers/", "api/sellers")]
    [InlineData("sellers", "sellers")]
    // ── Group 2: dynamic segments ────────────────────────────────────────────
    [InlineData("/api/Sellers/{sellerId}", "api/sellers/{x}")]
    [InlineData("/Users/{userId}/Permissions/{permId}", "users/{x}/permissions/{x}")]
    [InlineData("/api/Sellers/{id:int}", "api/sellers/{x}")]
    [InlineData("/{month:int}/{year:int}", "{x}/{x}")]
    // ── Group 3: query strings stripped ──────────────────────────────────────
    [InlineData("/sellers?status=active", "sellers")]
    [InlineData("/api/Sellers?page=1&pageSize=10", "api/sellers")]
    // ── Group 4: realistic production route shapes ──────────────────────────
    [InlineData("/api/Checkout/resume-token/{token}/detail", "api/checkout/resume-token/{x}/detail")]
    [InlineData("/api/Disputes/{disputeId:int}/invoices/{fileId:int}/download", "api/disputes/{x}/invoices/{x}/download")]
    [InlineData("/api/Files/public/store/{slug}/logo/{theme}", "api/files/public/store/{x}/logo/{x}")]
    // ── Group 5: edge cases ──────────────────────────────────────────────────
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("///sellers///notes///", "sellers/notes")]
    // ── Group 6: special characters ──────────────────────────────────────────
    [InlineData("/api/Files/{fileId}.pdf", "api/files/{x}.pdf")]
    // ── Group 7: hash fragments ──────────────────────────────────────────────
    [InlineData("/sellers#section", "sellers")]
    // ── Group 8: case normalization ──────────────────────────────────────────
    [InlineData("/api/SELLERS", "api/sellers")]
    [InlineData("/api/AccountProfiles/current", "api/accountprofiles/current")]
    // ── Group 10: combinations ───────────────────────────────────────────────
    [InlineData("/api/sellers/{id}?include=notes#top", "api/sellers/{x}")]
    [InlineData("/api/Sellers/{sellerId}/Tags/{tagId}", "api/sellers/{x}/tags/{x}")]
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
    [InlineData("/api/sellers", "/api/sellers", true)]
    // case-insensitivity is the framework's semantics, not a heuristic
    [InlineData("/api/Sellers", "/api/SELLERS", true)]
    // a {param} hole binds a concrete segment (the request-path direction)
    [InlineData("/api/sellers/{id}", "/api/sellers/42", true)]
    [InlineData("/api/sellers/{id:int}", "/api/sellers/42", true)]
    // and the reverse: a hole in the query matches a template's concrete segment
    [InlineData("/api/AccountProfiles/current", "/api/accountprofiles/{x}", true)]
    // holes match holes even under different names/constraints
    [InlineData("/api/sellers/{sellerId:int}", "/api/sellers/{id}", true)]
    // segment counts must agree
    [InlineData("/api/sellers/{id}", "/api/sellers", false)]
    [InlineData("/api/sellers", "/api/sellers/42", false)]
    // concrete segments must agree
    [InlineData("/api/sellers/{id}", "/api/disputes/42", false)]
    // trailing slash and duplicate slashes are normalization, not difference
    [InlineData("/api/sellers/", "//api//sellers", true)]
    // root
    [InlineData("/", "", true)]
    public void Matches_FrameworkSemantics(string template, string query, bool expected) =>
        Assert.Equal(
            expected,
            RouteTemplate.Matches(RouteTemplate.Normalize(template), RouteTemplate.Normalize(query)));
}
