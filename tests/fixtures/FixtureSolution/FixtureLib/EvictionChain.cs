namespace Fixture.Lib;

// Regression fixture for issue #6 (incremental re-analysis silently drops edges). The bug needs a
// 3-file chain E -> D -> F:
//   F (this file, EvictionChain.cs) declares IEvictionContract — the file that gets touched.
//   D (EvictionChainImplementor.cs) implements IEvictionContract, making it a one-hop dependent
//     of F, and separately declares UnrelatedWork(), a member that has nothing to do with F.
//   E (EvictionChainConsumer.cs) calls D's UnrelatedWork() — never anything declared in F.
// Touching F makes the planner evict-and-rewalk D (correctly, since D might reference a renamed
// or removed symbol from F). Before the fix, that eviction also dropped E's edge into D's
// UnrelatedWork(), because the old predicate required BOTH endpoints' files to survive; D's
// eviction alone was enough to kill an edge E never had anything to do with.
public interface IEvictionContract
{
    void Contracted();
}
