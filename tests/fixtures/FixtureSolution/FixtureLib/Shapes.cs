namespace Fixture.Lib;

// This fixture deliberately exercises every Slnmap relationship kind:
// Implements (ShapeBase : IShape), Inherits (Circle/Square : ShapeBase),
// Calls (Geometry.TotalArea -> IShape.Area), References, and Contains.

public interface IShape
{
    double Area();
}

public abstract class ShapeBase : IShape
{
    public abstract double Area();

    public string Describe() => $"{GetType().Name}: {Area():F2}";
}

public sealed class Circle : ShapeBase
{
    public Circle(double radius) => Radius = radius;

    public double Radius { get; }

    public override double Area() => Math.PI * Radius * Radius;
}

public sealed class Square : ShapeBase
{
    private readonly double _side;

    public Square(double side) => _side = side;

    public override double Area() => _side * _side;
}

public static class Geometry
{
    public static double TotalArea(IEnumerable<IShape> shapes)
    {
        double total = 0;
        foreach (IShape shape in shapes)
        {
            total += shape.Area();
        }

        return total;
    }
}
