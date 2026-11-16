using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Planet.Simulation.Terrain;

/// <summary>
/// Grottes proches de la caméra (étape 18) : leurs galeries sont tracées en tâche de fond quand
/// la caméra s'en approche (à moins de 1,5 km), pour être creusées dans le relief en volume et
/// servir de sol à la caméra.
/// </summary>
public sealed class NearbyCaves
{
    private const double BuildDistanceM = 1500;

    private readonly Dictionary<CaveSite, Task<Cave>> _building = new();
    private readonly Dictionary<CaveSite, Cave> _ready = new();
    private TerrainHeightField _field;

    public IReadOnlyList<CaveSite> Sites { get; private set; } = Array.Empty<CaveSite>();
    /// <summary>Grottes tracées.</summary>
    public IReadOnlyList<Cave> Ready { get; private set; } = Array.Empty<Cave>();
    /// <summary>Change à chaque grotte ajoutée ou retirée.</summary>
    public int Version { get; private set; }

    /// <summary>Nouveaux emplacements, ou nouveau relief : les grottes seront retracées.</summary>
    public void SetSites(IReadOnlyList<CaveSite> sites, TerrainHeightField field)
    {
        if (field == _field && sites.SequenceEqual(Sites))
            return;
        if (field != _field)
        {
            _building.Clear();
            _ready.Clear();
        }
        _field = field;
        Sites = sites;
        foreach (var site in _ready.Keys.Where(s => !sites.Contains(s)).ToArray())
            _ready.Remove(site);
        Publish();
    }

    public void Clear() => SetSites(Array.Empty<CaveSite>(), null);

    /// <summary>Trace les grottes proches de la caméra (deux à la fois au plus).</summary>
    public void Update(Double3 camera)
    {
        if (_field == null)
            return;
        bool changed = false;
        foreach (var (site, task) in _building.ToArray())
        {
            if (!task.IsCompleted)
                continue;
            _building.Remove(site);
            if (task.IsCompletedSuccessfully && Sites.Contains(site))
            {
                _ready[site] = task.Result;
                changed = true;
            }
        }
        Double3 up = camera.Normalized();
        foreach (var site in Sites.OrderByDescending(s => s.Direction.Dot(up)))
        {
            if ((site.Direction - up).Length() * TerrainHeightField.RadiusMeters > BuildDistanceM || _building.Count >= 2)
                break;
            if (_ready.ContainsKey(site) || _building.ContainsKey(site))
                continue;
            var field = _field;
            _building[site] = Task.Run(() =>
            {
                Func<Double3, double> height = d => field.Sample(d, 0.5, ref ThreadHint.Value).Height;
                return site.Kind == CaveKind.Karst
                    ? Cave.Karst(site.Direction, site.Seed, height)
                    : Cave.LavaTube(site.Direction, site.Downhill, site.Seed, height);
            });
        }
        if (changed)
            Publish();
    }

    private void Publish()
    {
        Ready = _ready.Values.ToArray();
        Version++;
    }

    /// <summary>Espace libre de la grotte où se trouve ce point (null : hors des grottes).</summary>
    public CaveSpace? SpaceAt(Double3 position)
    {
        foreach (var cave in Ready)
        {
            if (cave.Origin.DistanceTo(position) > cave.BoundingRadius + 20)
                continue;
            var space = cave.SpaceAt(position);
            if (space.HasValue)
                return space;
        }
        return null;
    }

    /// <summary>Point de l'axe des galeries le plus proche (repère de la planète), ou null si aucune grotte n'est proche.</summary>
    public Double3? Rescue(Double3 position)
    {
        Double3? best = null;
        double bestDistance = double.MaxValue;
        foreach (var cave in Ready)
        {
            if (cave.Origin.DistanceTo(position) > cave.BoundingRadius + 20)
                continue;
            Double3 q = cave.ToLocal(position);
            foreach (var (a, _, b, _) in cave.Segments)
            {
                Double3 ab = b - a;
                double t = Math.Clamp((q - a).Dot(ab) / Math.Max(ab.LengthSquared(), 1e-9), 0, 1);
                Double3 point = a + ab * t;
                double d = point.DistanceTo(q);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = cave.ToWorld(point);
                }
            }
        }
        return best;
    }

    /// <summary>Ce point est-il sous terre, dans une grotte ?</summary>
    public bool Underground(Double3 position)
    {
        foreach (var cave in Ready)
        {
            if (cave.Origin.DistanceTo(position) > cave.BoundingRadius + 20)
                continue;
            Double3 q = cave.ToLocal(position);
            if (cave.Contains(q) && q.Z < cave.GroundZ(q.X, q.Y) - 1)
                return true;
        }
        return false;
    }
}
