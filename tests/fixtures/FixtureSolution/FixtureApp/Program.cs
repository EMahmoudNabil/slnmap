using Fixture.Lib;

IShape[] shapes = [new Circle(1.5), new Square(2)];

Console.WriteLine($"Total area: {Geometry.TotalArea(shapes):F2}");
Console.WriteLine(new Circle(1).Describe());
