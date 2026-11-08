// Tests de la simulation Planet, hors moteur Godot. Depuis le dossier du projet Godot :
//   dotnet run -c Release --project tests/Planet.Tests               tous les tests (~1 à 2 min)
//   dotnet run -c Release --project tests/Planet.Tests -- --quick    sans les simulations longues
//   dotnet run -c Release --project tests/Planet.Tests -- Hydrology  filtre sur le nom des tests
// Code de sortie 0 si tout réussit, 1 sinon.
using System;
using System.Globalization;
using System.Threading;
using Planet.Simulation;

// Hors moteur, GD.Print fait planter le programme : les journaux vont dans la console
SimulationLog.Info = message => Console.WriteLine($"     [log] {message}");
SimulationLog.Warning = message => Console.WriteLine($"     [warning] {message}");
// Nombres affichés de la même façon quelle que soit la langue du système
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

return Planet.Tests.TestRunner.Run(args);
