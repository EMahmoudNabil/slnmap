namespace Fixture.Lib;

// E in the E -> D -> F chain described in EvictionChain.cs: calls EvictionChainImplementor's OWN
// unrelated member, never anything declared in IEvictionContract (EvictionChain.cs) directly.
public static class EvictionChainConsumer
{
    public static int UseUnrelatedWork() => new EvictionChainImplementor().UnrelatedWork();

    // v0.6.0 integration addition (reports/v060-regression-plan.md §1b): a FULLY-QUALIFIED
    // (no `using` shortcut) reference into D -- the #4-shaped edge this eviction chain didn't
    // previously exercise. Deliberately not calling UnrelatedWork() through this reference (that
    // Calls edge already exists above); this typeof() is the only thing that creates it.
    public static System.Type UseFullyQualifiedReferenceToD() => typeof(Fixture.Lib.EvictionChainImplementor);

    // NOTE on the event half of §1b's plan: an event *subscription* (`+=`) does not create an
    // edge under #5's actual, as-implemented design (issue-5-investigation.md §4.6, mirroring
    // Field's own precedent of not tracking incoming field references either) -- so there is no
    // "E -> D's event" edge kind to add here. EvictionChainImplementor.UnrelatedEvent (added
    // alongside this file) instead exercises the Event-kind node's own survival through D's
    // eviction-and-rewalk, which is the applicable version of this check given that scope
    // decision. See reports/v060-integration-report.md for why this adapts the plan.
}
