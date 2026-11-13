using System;
using Planet.Simulation;
using Planet.Simulation.Climate;
using Planet.Simulation.Terrain;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Taille de la planète : la Terre (rayon de 6 371 km), ou une petite planète « éprouvette »
/// de 2 000 km de rayon, à peu près la taille de la Lune. Sa surface fait le dixième de celle de
/// la Terre : moins de plaques (5 au lieu de 14), un point chaud au lieu de 12, une poignée de
/// volcans et de sources chaudes, une ou deux chaînes de montagnes. Tout reste simulé de la même
/// façon (tectonique, climat, eau, croûte modulaire, chimie), sur une grille quatre fois plus
/// légère (2 562 cellules d'environ 140 km au lieu de 10 242 de 220 km) : tout va plus vite, et
/// l'on fait le tour de ce qui s'y passe en quelques minutes.
/// <para>Simplification : la petite planète garde la gravité, l'atmosphère, la rotation et
/// l'orbite de la Terre (ce sont ces réglages qui font son climat) ; seule la taille change.</para>
/// </summary>
public partial class Main
{
    public const float SmallPlanetRadiusKm = 2000f;
    private const int SmallPlanetSubdivisions = 4, SmallPlanetPlates = 5;
    private const int EarthSubdivisions = 5, EarthPlates = 14;

    /// <summary>Démarrer sur une petite planète « éprouvette » (case de l'interface, groupe « Planète »).</summary>
    [Godot.Export] public bool StartSmall { get; set; }

    public bool SmallPlanet => PlanetConstants.RadiusKm < PlanetConstants.EarthRadiusKm - 1;

    /// <summary>Altitude de la vue d'ensemble, à l'échelle de la planète.</summary>
    private double OrbitView => OrbitAltitude * PlanetConstants.RadiusKm / PlanetConstants.EarthRadiusKm;

    /// <summary>Rayon et finesse de la grille choisis avant de créer la grille (au démarrage).</summary>
    private void ApplyStartSize()
    {
        if (!StartSmall)
            return;
        PlanetConstants.SetRadius(SmallPlanetRadiusKm);
        Subdivisions = SmallPlanetSubdivisions;
        PlateCount = SmallPlanetPlates;
    }

    /// <summary>
    /// Passe d'une planète comme la Terre à une petite planète « éprouvette », ou l'inverse : une
    /// nouvelle planète se forme (grille, climat, relief, croûte et chimie recréés).
    /// </summary>
    private void SetPlanetSize(bool small)
    {
        if (small == SmallPlanet)
            return;
        PlanetConstants.SetRadius(small ? SmallPlanetRadiusKm : PlanetConstants.EarthRadiusKm);
        Subdivisions = small ? SmallPlanetSubdivisions : EarthSubdivisions;
        PlateCount = small ? SmallPlanetPlates : EarthPlates;

        // Tout ce qui dépend de la grille est recréé
        _estimateTask?.Wait();
        _estimateTask = null;
        _terrainTask?.Wait();
        _terrainTask = null;
        _cloudTask?.Wait();
        _cloudTask = null;
        _grid = PlanetGrid.Create(Subdivisions);
        _climate.Dispose();
        _climate = new ClimateSimulation(_grid, _orbit, UseGpu, ClimateStartDay);
        _climateSurfaceSource = null;
        _canopy = null;
        _volcanism = null;
        _hoverCell = -1;
        _probeCell = -1;
        _weatherHint = -1;
        _terrain.SetGrid(_grid);
        _terrain.RefreshPlanetRadius();
        GeneratePlanet();
        _terrain.Explorer.PlaceAbove(new Double3(0, 0, 1), OrbitView);
        ShowNotice(small
            ? $"Petite planète « éprouvette » : rayon de {SmallPlanetRadiusKm:N0} km, {PlateCount} plaques, {_grid.CellCount:N0} cellules"
            : $"Planète comme la Terre : rayon de {PlanetConstants.EarthRadiusKm:N0} km, {PlateCount} plaques, {_grid.CellCount:N0} cellules");
    }

    private void RegisterPlanetSize()
    {
        _ui.Registry.Add(new SimToggle
        {
            Group = "Planète", Label = "Petite planète (éprouvette)",
            Help = $"Planète de {SmallPlanetRadiusKm:N0} km de rayon (la Terre : 6 371 km) : 5 plaques, un point chaud, quelques volcans et " +
                   "sources chaudes, tout va plus vite. Changer de taille crée une nouvelle planète.",
            Get = () => SmallPlanet, Set = SetPlanetSize,
        });
    }
}
