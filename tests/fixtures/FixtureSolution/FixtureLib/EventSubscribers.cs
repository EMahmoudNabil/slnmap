namespace Fixture.Lib;

// Exercises issue #8: event subscription (+=/-=) and raising sites must produce usage edges to
// the EVENT node. Before the fix, HandleNameReference's symbol filter accepted
// IProperty/IMethod/INamedType/IField but not IEventSymbol, so every event reference — from a
// different class or the event's own raiser — silently produced nothing, and find_usages on any
// event answered "No usages found" (confirmed on eShopOnWeb's RefreshBroadcast.RefreshRequested).

public sealed class EventSubscriber
{
    private readonly EventFieldHolder _holder;

    public EventSubscriber(EventFieldHolder holder)
    {
        _holder = holder;
        // Subscription from ANOTHER class — the eShopOnWeb OnInitialized() shape.
        _holder.Changed += OnChanged;
    }

    public void Detach() => _holder.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgsPayload payload)
    {
    }
}
