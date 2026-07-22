// Second top-level-statements entry point: two executables in one solution reproduce the
// entry-point FQN collision (both render "<top-level-statements-entry-point>") that the analyzer
// must disambiguate per assembly. Deliberately uses only Labels (Text.cs), not Shapes.cs, so the
// two apps have disjoint dependencies and incremental tests can edit either dependency alone.
Console.WriteLine(Fixture.Lib.Labels.For(4));
