using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fixture.Web;

/// <summary>A Razor Page fixture (v0.12.2, foreign-patterns-trial finding #2) — routes by file
/// location, not attributes; disclosed via RazorPageFacts, never modeled as an Endpoint.</summary>
public sealed class SamplePageModel : PageModel
{
    public void OnGet()
    {
    }

    public IActionResult OnPost()
    {
        return Page();
    }
}
