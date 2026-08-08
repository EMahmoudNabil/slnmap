namespace Fixture.Lib;

// D in the E -> D -> F chain described in EvictionChain.cs: implements IEvictionContract (making
// it a one-hop dependent of that file) and separately declares UnrelatedWork(), which has nothing
// to do with IEvictionContract at all.
//
// v0.6.0 integration addition (reports/v060-regression-plan.md §1b): UnrelatedEvent gives this
// eviction-and-rewalk chain an Event-kind node too, alongside the existing Method-kind
// UnrelatedWork() -- exercising #6's source-scoped eviction fix against a node kind #5 added,
// not just the ones #6 was originally verified against.
public sealed class EvictionChainImplementor : IEvictionContract
{
    public void Contracted()
    {
    }

    // Unrelated to IEvictionContract. EvictionChainConsumer's edge into this member is the one
    // that must survive an eviction-and-rewalk of this file triggered by touching EvictionChain.cs.
    public int UnrelatedWork() => 42;

    // Also unrelated to IEvictionContract. See EvictionChainConsumer.cs for why this is a
    // field-style event (not a subscription target) -- #5 deliberately does not create an
    // incoming edge for event subscription (issue-5-investigation.md §4.6), so this node's own
    // survival through D's eviction-and-rewalk is what's under test here, not an E->event edge.
    public event System.Action? UnrelatedEvent;

    public void RaiseUnrelatedEvent() => UnrelatedEvent?.Invoke();
}
