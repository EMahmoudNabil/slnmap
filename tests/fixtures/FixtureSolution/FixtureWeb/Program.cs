using Fixture.Web;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// A registration in top-level statements: the walker already visits this invocation and
// attributes it to FixtureWeb.<top-level-statements-entry-point> (the Gap-3 finding).
app.MapGet("/health", VendorEndpoints.Ping);

VendorEndpoints.Map(app);

app.Run();
