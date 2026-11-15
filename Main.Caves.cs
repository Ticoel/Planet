using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Planet.Rendering;
using Planet.Simulation.Climate;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;

namespace Planet;

/// <summary>
/// Grottes en volume (étape 18) dans le jeu : grottes karstiques en amont des résurgences des
/// calcaires, tunnels de lave à mi-flanc des volcans. Tracées quand la caméra s'en approche
/// (<see cref="NearbyCaves"/>) et creusées dans le relief en volume autour de la caméra
/// (<see cref="VolumeChunkBuilder"/>) ; on y marche (la caméra suit le sol des galeries) ;
/// touche Y : survoler la grotte suivante.
/// </summary>
public partial class Main
{
    private readonly NearbyCaves _caves = new();
    // Maj + G : relief en volume autour de la caméra (pour comparer avec la surface seule)
    private bool _volumeRelief = true;
    private readonly HashSet<CaveSite> _visitedCaves = new();
    private TerrainHeightField _caveField;
    private Groundwater _caveGroundwater;

    private void SetupCaves()
    {
        _terrain.Explorer.CaveProbe = p => _caves.SpaceAt(p);
    }

    /// <summary>Emplacements des grottes du relief et de l'eau souterraine actuels.</summary>
    private List<CaveSite> CaveSites(TerrainHeightField field)
    {
        var sites = new List<CaveSite>();
        if (_groundwater != null)
            foreach (var spring in _groundwater.Springs)
                if (spring.Kind == SpringKind.Karst)
                {
                    int seed = StableSeed(Seed, spring.Cell, 11);
                    sites.Add(new CaveSite(CaveKind.Karst, Double3.From(spring.Position).Normalized(), seed));
                }
        int hint = -1;
        foreach (var volcano in field.Landforms)
        {
            if (volcano.Kind is not (LandformKind.ShieldVolcano or LandformKind.Stratovolcano))
                continue;
            int seed = StableSeed(Seed, (int)(BitConverter.DoubleToInt64Bits(volcano.BirthMyr) % int.MaxValue), 23);
            var random = new Random(seed);
            Double3 summit = Double3.From(volcano.Position).Normalized();
            var (ex, ey) = LavaFlow.TangentFrame(summit);
            double angle = random.NextDouble() * 2 * Math.PI;
            Double3 outward = ex * Math.Cos(angle) + ey * Math.Sin(angle);
            Double3 site = (summit + outward * (0.45 * volcano.RadiusM / TerrainHeightField.RadiusMeters)).Normalized();
            if (field.Sample(site, 1, ref hint).Height < 5)
                continue;                                       // flanc sous la mer
            sites.Add(new CaveSite(CaveKind.LavaTube, site, seed, outward));
        }
        return sites;
    }

    /// <summary>Graine reproductible (HashCode.Combine change à chaque lancement du programme :
    /// les grottes auraient changé de forme d'une partie à l'autre).</summary>
    private static int StableSeed(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)a * 2654435761u ^ (uint)b * 2246822519u ^ (uint)c * 3266489917u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (int)(h & 0x7fffffff);
        }
    }

    private void UpdateCaves()
    {
        var field = _terrain.Field;
        bool climate = _clock == ClockMode.Climate;
        _terrain.VolumeEnabled = climate && _volumeRelief;
        if (field == null)
        {
            _caves.Clear();
            _terrain.SetVolumeCaves(_caves.Ready, _caves.Version);
            return;
        }
        if (field != _caveField || _groundwater != _caveGroundwater)
        {
            _caveField = field;
            _caveGroundwater = _groundwater;
            _caves.SetSites(CaveSites(field), field);
        }
        var explorer = _terrain.Explorer;
        if (explorer.AltitudeAboveGround < 3000)
            _caves.Update(explorer.Position);
        _terrain.SetVolumeCaves(_caves.Ready, _caves.Version);
        // Lampe : allumée sous terre
        _terrain.SetShaderParameter("cave_lamp", explorer.InCave && _caves.Underground(explorer.Position) ? 1f : 0f);
    }

    /// <summary>Touche Y : emmène la caméra à l'entrée de la grotte suivante (de la plus proche à la plus lointaine).</summary>
    private void GoToNextCave() => _terrain.Explorer.Transition(PlaceAtNextCave);

    private void PlaceAtNextCave()
    {
        if (_terrain.Field == null)
            return;
        if (_caveField != _terrain.Field || _caveGroundwater != _groundwater)
            UpdateCaves();
        var all = _caves.Sites;
        if (all.Count == 0)
        {
            ShowNotice("Pas de grotte sur cette planète pour l'instant (il faut des calcaires sous la pluie, ou des volcans hors de l'eau)");
            return;
        }
        if (all.All(_visitedCaves.Contains))
            _visitedCaves.Clear();
        Double3 camera = _terrain.Explorer.Position.Normalized();
        var site = all.Where(s => !_visitedCaves.Contains(s)).OrderByDescending(s => s.Direction.Dot(camera)).First();
        _visitedCaves.Add(site);
        // En vol, à une cinquantaine de mètres de l'entrée et 30 m au-dessus, regard vers l'ouverture
        // (G pour marcher, puis on y entre à pied)
        var explorer = _terrain.Explorer;
        if (explorer.Walking)
            explorer.ToggleWalking();
        Double3 entrance = site.Direction;
        var (ex, ey) = LavaFlow.TangentFrame(entrance);
        explorer.PlaceAbove((entrance - (ex * 0.8 + ey * 0.6) * (50 / TerrainHeightField.RadiusMeters)).Normalized(), 30);
        explorer.LookAt(GroundPoint(entrance, 0));
        ShowNotice(site.Kind == CaveKind.Karst
            ? "Lieu : grotte karstique, en amont d'une résurgence (G pour marcher jusqu'à l'ouverture ; une lampe s'allume sous terre)"
            : "Lieu : tunnel de lave, entrée par une lucarne effondrée (G pour marcher jusqu'à l'ouverture ; une lampe s'allume sous terre)");
    }
}
