using System;
using Godot;

namespace Planet.Simulation;

/// <summary>
/// Journal des messages de la simulation. Par défaut, la console de Godot ; les tests hors du
/// moteur le redirigent vers la sortie standard (les fonctions GD n'y sont pas disponibles).
/// </summary>
public static class SimulationLog
{
    public static Action<string> Info { get; set; } = message => GD.Print(message);
    public static Action<string> Warning { get; set; } = message => GD.PushWarning(message);
}
