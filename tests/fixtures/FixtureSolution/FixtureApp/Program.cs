using Fixture.Lib;

IShape[] shapes = [new Circle(1.5), new Square(2)];

Console.WriteLine($"Total area: {Geometry.TotalArea(shapes):F2}");
Console.WriteLine(new Circle(1).Describe());

// WebApplicationFactory-style accessibility hook: this explicit partial merges with the
// synthesized top-level Program class — one symbol, and it must stay one graph node.
public partial class Program { }
