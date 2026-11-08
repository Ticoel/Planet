using Godot;

namespace Planet.Simulation.Tectonics;

/// <summary>
/// Une plaque tectonique rigide. Sur une sphère, tout mouvement rigide est une rotation autour
/// d'un axe passant par le centre : le pôle d'Euler. La vitesse d'un point p de la plaque est
/// donc v = ω × p, avec ω = pôle × vitesse angulaire.
/// </summary>
public sealed class Plate
{
    public int Id { get; }

    /// <summary>Cellule d'où la plaque a grandi lors de la génération.</summary>
    public int SeedCell { get; }

    /// <summary>Axe de rotation (vecteur unitaire).</summary>
    public Vector3 EulerPole { get; set; }

    /// <summary>Vitesse de rotation autour du pôle, en radians par million d'années.</summary>
    public float AngularSpeed { get; set; }

    /// <summary>Nombre de cellules appartenant à la plaque.</summary>
    public int CellCount { get; internal set; }

    public Plate(int id, int seedCell)
    {
        Id = id;
        SeedCell = seedCell;
    }

    /// <summary>Rotation vers laquelle le mouvement de la plaque évolue progressivement (rad/Ma).</summary>
    public Vector3 TargetAngularVelocity { get; set; }

    /// <summary>Instant (Ma) où la plaque choisira un nouveau cap.</summary>
    public double NextMotionChangeMyr { get; set; }

    public bool IsAlive => CellCount > 0;

    /// <summary>Vecteur rotation ω (rad/Ma).</summary>
    public Vector3 AngularVelocity => EulerPole * AngularSpeed;

    /// <summary>Fixe le mouvement à partir d'un vecteur rotation ω (rad/Ma).</summary>
    public void SetAngularVelocity(Vector3 angularVelocity)
    {
        float speed = angularVelocity.Length();
        AngularSpeed = speed;
        if (speed > 1e-9f)
            EulerPole = angularVelocity / speed;
    }

    /// <summary>Vitesse linéaire d'un point de la sphère unité porté par la plaque,
    /// en rayons par million d'années.</summary>
    public Vector3 VelocityAt(Vector3 position) => AngularVelocity.Cross(position);
}
