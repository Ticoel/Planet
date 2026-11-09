using System;
using Godot;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Vecteur 3D en double précision. À l'échelle réelle (6 371 km de rayon), un float (Vector3)
/// ne distingue plus deux points distants de moins d'un demi-mètre : toutes les positions
/// absolues du terrain sont donc en double ; seules les positions relatives à la caméra ou au
/// centre d'un morceau de terrain passent en float pour l'affichage.
/// </summary>
public readonly struct Double3 : IEquatable<Double3>
{
    public readonly double X, Y, Z;

    public Double3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Double3 Zero = new(0, 0, 0);
    public static readonly Double3 UnitY = new(0, 1, 0);

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Double3 operator -(Double3 a) => new(-a.X, -a.Y, -a.Z);
    public static Double3 operator *(Double3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Double3 operator *(double s, Double3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Double3 operator /(Double3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Double3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Double3 Cross(Double3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double LengthSquared() => X * X + Y * Y + Z * Z;
    public double Length() => Math.Sqrt(LengthSquared());

    public Double3 Normalized()
    {
        double length = Length();
        return length > 0 ? this / length : this;
    }

    public double DistanceTo(Double3 b) => (this - b).Length();

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public static Double3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    public bool Equals(Double3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object obj) => obj is Double3 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X:G9}, {Y:G9}, {Z:G9})";
}
