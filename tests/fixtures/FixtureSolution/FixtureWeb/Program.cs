using Fixture.Web;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// A registration in top-level statements: the walker already visits this invocation and
// attributes it to FixtureWeb.<top-level-statements-entry-point> (the Gap-3 finding).
app.MapGet("/health", VendorEndpoints.Ping);

// The cross-extractor duplicate registration lives in StatusController.cs (StatusCompat) — in
// the SAME file as the controller, deliberately: a duplicate route split across files loses its
// second HandledBy edge under incremental eviction (edge ownership follows the node's first-seen
// file — a known, documented limitation; ASP.NET itself rejects such duplicates at request time).
StatusCompat.Map(app);

VendorEndpoints.Map(app);

app.Run();
