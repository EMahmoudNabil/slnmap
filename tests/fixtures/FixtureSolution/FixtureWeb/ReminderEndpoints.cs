namespace Fixture.Web;

/// <summary>
/// A leaf endpoint group using the convention infrastructure — the Tier-2 shapes the v1 design
/// supports (each registration pins one): the GetType().Name prefix fold, the reversed-argument
/// forwarder, the omitted pattern (overload default), string.Empty, an :int route constraint kept
/// verbatim, a multi-line chained registration, and a nested group.
/// </summary>
// Deliberately NOT sealed: the field convention (OSSUS's 54 groups) relies on the
// no-derived-types-in-compilation scan, not the IsSealed shortcut — this pins that path.
public class Reminders : EndpointGroupBase
{
    public override void Map(WebApplication app)
    {
        var group = app.MapGroup(this);

        // Omitted pattern: the forwarder's default "" → GET /api/Reminders.
        group.MapGet(GetAll);

        // string.Empty is a static readonly field, not a compile-time constant — special-cased.
        group.MapPost(Create, string.Empty);

        // Reversed argument order with a route constraint, kept as authored in the identity.
        group.MapGet(GetById, "{id:int}");

        // Multi-line registration ending in a builder chain: the Map* call is the innermost node.
        group.MapPut(Update,
            "{id:int}/details").WithName("UpdateReminderDetails");

        // Nested group: the prefix-trace loop recurses through the convention-folded parent.
        var archive = group.MapGroup("/archive");
        archive.MapDelete(Purge, "{id:int}");
    }

    public static string GetAll() => "all";

    public static string Create() => "created";

    public static string GetById(int id) => $"reminder {id}";

    public static string Update(int id) => $"updated {id}";

    public static string Purge(int id) => $"purged {id}";
}
