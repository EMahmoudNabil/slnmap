namespace Fixture.Lib;

// Exercises Issue #5: events as graph nodes. NodeKind.Event (value 13) is already reserved in
// the enum but nothing currently maps to it -- mirroring the exact pre-fix state of
// NodeKind.Field before Gap 2 (see Fields.cs, which deliberately does NOT model its
// EventHolder.Changed field-style event). This fixture is additive and distinct from
// Fields.cs's EventHolder -- it exists to drive the Issue #5 fix itself, not to re-cover Gap 2.

public sealed class EventReferenceTargetA
{
}

public sealed class EventReferenceTargetB
{
}

public sealed class EventFieldHolder
{
    // Field-style event: EventFieldDeclarationSyntax. Same VariableDeclaratorSyntax shape as a
    // plain field, but GetDeclaredSymbol on the declarator returns an IEventSymbol, not an
    // IFieldSymbol -- the reason DocumentWalker's field-declarator case (guarded to
    // FieldDeclarationSyntax) does not pick this up today.
    public event EventHandler<EventArgsPayload>? Changed;

    public void Raise(EventArgsPayload payload) => Changed?.Invoke(this, payload);
}

public sealed class EventArgsPayload
{
}

public sealed class MultiDeclaratorEventFields
{
    // One EventFieldDeclarationSyntax, two VariableDeclaratorSyntax -- mirrors
    // MultiDeclaratorFields in Fields.cs. If the fix follows the Field precedent (matching on
    // the declarator, not the declaration), this must yield two distinct Event nodes.
    public event EventHandler? First, Second;

    public void RaiseBoth()
    {
        First?.Invoke(this, EventArgs.Empty);
        Second?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PropertyStyleEventHolder
{
    private EventHandler? _backing;

    // Property-style event: EventDeclarationSyntax with explicit add/remove accessors -- a
    // different syntax node from EventFieldDeclarationSyntax entirely (no VariableDeclaratorSyntax
    // involved), but GetDeclaredSymbol on the EventDeclarationSyntax itself also returns an
    // IEventSymbol. The typeof() reference inside `add` exercises whether a reference made from
    // inside an accessor body attributes to the event node itself (GetEnclosingMemberNode) rather
    // than falling back to the containing type, the same concern Gap 2 had for field initializers.
    public event EventHandler Notify
    {
        add
        {
            typeof(EventReferenceTargetA).ToString();
            _backing += value;
        }
        remove
        {
            typeof(EventReferenceTargetB).ToString();
            _backing -= value;
        }
    }

    public void Raise() => _backing?.Invoke(this, EventArgs.Empty);
}
