using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.13.0 follow-up to the HTTP-wrapper-resolution work
/// (reports/ts-http-wrapper-resolution-report.md): a resolved frontend call-site template can now
/// be a well-formed absolute URL (`API_ROOT = 'https://host/api'` is a common real-world shape —
/// confirmed against `gothinkster/react-redux-realworld-example-app`'s actual `agent.js`), and
/// <see cref="RouteTemplate.Normalize"/> previously never stripped the scheme+host, so an
/// absolute-URL call site could never skeleton-match a relative backend endpoint at all (verified
/// directly: <c>Normalize("https://host/api/x")</c> produced <c>"https:/host/api/x"</c> — the
/// leading "//" just collapsed to one slash, the host was never removed). This file exercises
/// <see cref="RouteTemplate.TrySplitAbsoluteUrl"/> and <see cref="RouteTemplate.Normalize"/>'s
/// updated behavior directly; <see cref="CrossStackLinkerTests"/> covers the linker-level
/// consequences (matching, the new <see cref="CallSiteLinkOutcome.AmbiguousHost"/> outcome, and
/// the "host carried visibly" contract).
/// </summary>
public sealed class RouteTemplateAbsoluteUrlTests
{
    [Theory]
    [InlineData("https://api.example.com/vendors", "vendors")]
    [InlineData("https://api.example.com/api/Vendors/{id}", "api/vendors/{x}")]
    [InlineData("http://localhost:3000/api/vendors", "api/vendors")]
    [InlineData("https://api.example.com", "")]
    [InlineData("https://api.example.com/", "")]
    [InlineData("https://api.example.com?tab=details", "")]
    [InlineData("https://api.example.com/vendors?tab=details#section", "vendors")]
    public void Normalize_AbsoluteUrl_StripsSchemeAndHostBeforeNormalizing(string input, string expected) =>
        Assert.Equal(expected, RouteTemplate.Normalize(input));

    [Fact]
    public void Normalize_RelativePathWithEmbeddedUrlInQueryString_IsUnaffected()
    {
        // The regression this fix must NOT introduce: an ordinary relative call site (starts with
        // '/', never a scheme) that happens to carry an absolute URL inside its OWN query string —
        // e.g. fetch('/redirect?to=' + encodeURIComponent(url)) — must normalize exactly as it
        // always has. Detection is anchored at position 0 (TrySplitAbsoluteUrl's own doc comment)
        // specifically so a "://" appearing anywhere OTHER than the very start never diverts this.
        string withEmbeddedUrl = RouteTemplate.Normalize("/redirect?to=http://evil.com");
        string withoutQuery = RouteTemplate.Normalize("/redirect");

        Assert.Equal(withoutQuery, withEmbeddedUrl);
    }

    [Fact]
    public void TrySplitAbsoluteUrl_OrdinaryRelativePath_IsNotAbsolute()
    {
        var outcome = RouteTemplate.TrySplitAbsoluteUrl("/api/vendors", out string? host, out string pathAndRest);

        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.NotAbsolute, outcome);
        Assert.Null(host);
        Assert.Equal("/api/vendors", pathAndRest);
    }

    [Fact]
    public void TrySplitAbsoluteUrl_CleanAbsoluteUrl_SplitsHostFromPath()
    {
        var outcome = RouteTemplate.TrySplitAbsoluteUrl("https://api.example.com/vendors/42", out string? host, out string pathAndRest);

        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.Clean, outcome);
        Assert.Equal("api.example.com", host);
        Assert.Equal("/vendors/42", pathAndRest);
    }

    [Fact]
    public void TrySplitAbsoluteUrl_HostWithPort_IsCarriedWhole()
    {
        var outcome = RouteTemplate.TrySplitAbsoluteUrl("http://localhost:3000/vendors", out string? host, out string pathAndRest);

        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.Clean, outcome);
        Assert.Equal("localhost:3000", host);
        Assert.Equal("/vendors", pathAndRest);
    }

    [Theory]
    [InlineData("https:///vendors")] // empty host
    [InlineData("https://")] // empty host, no path at all
    public void TrySplitAbsoluteUrl_EmptyHost_IsAmbiguous_NeverGuessed(string input)
    {
        var outcome = RouteTemplate.TrySplitAbsoluteUrl(input, out string? host, out string pathAndRest);

        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.Ambiguous, outcome);
        Assert.Null(host);
        // Falls back to echoing the original string verbatim -- a caller that ignores the return
        // value still gets a sane (pre-this-feature) string, never a crash or silently blank value.
        Assert.Equal(input, pathAndRest);
    }

    [Fact]
    public void TrySplitAbsoluteUrl_NullOrEmpty_IsNotAbsolute()
    {
        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.NotAbsolute, RouteTemplate.TrySplitAbsoluteUrl(null, out _, out _));
        Assert.Equal(RouteTemplate.AbsoluteUrlSplitResult.NotAbsolute, RouteTemplate.TrySplitAbsoluteUrl("", out _, out _));
    }
}
