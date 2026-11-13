using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Planet.Rendering;
using Planet.Simulation;
using Planet.Simulation.Terrain;
using Planet.Ui;

namespace Planet;

/// <summary>
/// Croûte modulaire (étape 16a) dans le jeu.
/// <list type="bullet">
/// <item><b>Géologie figée</b> : on laisse tourner la tectonique jusqu'à l'âge voulu, puis on
/// passe dans l'horloge climatique ; ce passage est définitif (confirmation demandée) : le relief
/// de base ne changera plus, et la croûte peut alors être modifiée sans que la tectonique efface
/// les modifications.</item>
/// <item><b>Mouvements de terrain en temps réel</b> autour de la caméra : on examine les pentes
/// proches (stabilité selon la pluie, le gel et les séismes), et celles qui cèdent partent en
/// glissement, éboulement ou avalanche rocheuse (<see cref="Landslide"/>, simulé en tâche de
/// fond). Chaque événement inscrit son calque dans la croûte (<see cref="CrustEdits"/>) : les
/// morceaux de terrain touchés sont recalculés pendant que la masse descend, et la cicatrice
/// reste. Touche B : provoquer un mouvement de terrain sur la pente la plus raide en vue.</item>
/// <item><b>Lave figée</b> : le basalte des coulées simulées s'ajoute aussi à la croûte.</item>
/// </list>
/// </summary>
public partial class Main
{
    private const double CrustScanSeconds = 1.5;
    private const double CrustScanRadiusM = 2500;
    /// <summary>Au-dessus de cette hauteur (m), on ne verrait rien : pas de mouvement simulé.</summary>
    private const double CrustMaxCameraAltitude = 12000;
    private const int MaxActiveSlides = 4;
    private const double SlidePublishSeconds = 0.25;
    private const double ClimateConfirmSeconds = 6;

    /// <summary>Instabilité des pentes par rapport à la Terre (réglage du joueur ; 0 : rien ne bouge).</summary>
    public double SlopeInstability { get; set; } = 1.0;

    private readonly CrustEdits _crust = new();
    private bool _geologyLocked;
    private double _climateConfirmUntil = double.NegativeInfinity;
    private double _realSeconds;

    private sealed class ActiveSlide
    {
        public ActiveSlide(Landslide slide)
        {
            Slide = slide;
            Center = NextCenter = slide.Source;
            Moving = NextMoving = slide.ReleasedVolume;
        }

        public Landslide Slide;
        public Task Step;
        public double PublishTimer;
        public (float[] delta, float[] debris)? Layer;
        public List<DebrisParticle> Particles = new();
        public List<DebrisParticle> Back = new();
        public double DistanceKm;
        // Centre et volume de ce qui bouge : lus au dernier pas terminé (Next* : écrits par la tâche)
        public Double3 Center, NextCenter;
        public double Moving, NextMoving;
    }

    private readonly List<ActiveSlide> _slides = new();
    private Task<Landslide> _slideBuild;
    private bool _slideBuildForced;
    private double _crustScanTimer;
    private double _shakingIntensity;    // intensité ressentie récente (Mercalli), décroît
    private readonly Random _crustRandom = new(17);
    private int _slideSeed = 1;
    private int _slideCount, _rockfallCount;
    private double _slideVolume;
    private float _slideRumble;
    private DebrisRenderer _debris;
    private readonly List<DebrisParticle> _allParticles = new();
    private string _lastSlide;

    // Lave figée inscrite dans la croûte
    private CrustPatch _lavaPatch;
    private LavaFlow _lavaPatchFlow;
    private double _lavaPublishTimer;

    // --- Géologie figée --------------------------------------------------------------------------

    /// <summary>
    /// Demande de passer dans l'horloge climatique. Le premier passage fige la géologie pour de
    /// bon : il faut le confirmer (même demande dans les secondes qui suivent). Vrai si l'on est
    /// (ou passe) dans l'horloge climatique.
    /// </summary>
    private bool RequestClimate()
    {
        if (_clock == ClockMode.Climate)
            return true;
        if (!_geologyLocked && _realSeconds > _climateConfirmUntil)
        {
            _climateConfirmUntil = _realSeconds + ClimateConfirmSeconds;
            ShowNotice($"Passer au climat fige la géologie à {AgeLabel(_tectonics.TimeMyr)}, sans retour possible : " +
                       "la croûte devient modulaire (glissements, éboulements). Confirmez (C, ou le même choix) dans les 6 secondes.");
            return false;
        }
        _climateConfirmUntil = double.NegativeInfinity;
        bool first = !_geologyLocked;
        _geologyLocked = true;
        SetClock(ClockMode.Climate);
        if (first)
            ShowNotice($"Géologie figée à {AgeLabel(_tectonics.TimeMyr)} : la croûte est modulaire (B : provoquer un mouvement de terrain)");
        return true;
    }

    /// <summary>Retour à l'horloge géologique : impossible une fois la géologie figée.</summary>
    private bool RequestGeology()
    {
        if (_clock == ClockMode.Geological)
            return true;
        if (_geologyLocked)
        {
            ShowNotice("La géologie est figée : la croûte est modulaire et ses modifications resteraient sans objet. " +
                       "R : nouvelle planète (la tectonique repart de zéro).");
            return false;
        }
        SetClock(ClockMode.Geological);
        return true;
    }

    /// <summary>Nouvelle planète : la croûte modulaire repart de zéro, la géologie n'est plus figée.</summary>
    private void ResetCrust()
    {
        foreach (var slide in _slides)
            slide.Step?.Wait();
        _slides.Clear();
        _slideBuild?.Wait();
        _slideBuild = null;
        _crust.Clear();
        _geologyLocked = false;
        _climateConfirmUntil = double.NegativeInfinity;
        _slideCount = _rockfallCount = 0;
        _slideVolume = 0;
        _lavaPatch = null;
        _lavaPatchFlow = null;
        _shakingIntensity = 0;
        _debris?.Clear();
    }

    // --- Mouvements de terrain ---------------------------------------------------------------------

    private void SetupCrust()
    {
        _debris = new DebrisRenderer { Name = "Debris" };
        AddChild(_debris);
    }

    /// <summary>Relief détaillé (calques compris) utilisable depuis plusieurs threads à la fois.</summary>
    private static Func<Double3, double> ThreadSafeHeight(TerrainHeightField field, double minWavelength)
    {
        var hints = new ThreadLocal<int[]>(() => new[] { -1 });
        return d => field.Height(d, minWavelength, ref hints.Value[0]);
    }

    /// <summary>Conditions du moment sous la caméra : sol détrempé, gel et dégel, secousses.</summary>
    private SlopeConditions CrustConditions(out string cause)
    {
        cause = "";
        int cell = _grid.NearestCell(_terrain.Explorer.Position.Normalized().ToVector3(), Math.Max(_weatherHint, 0));
        double soil = Math.Clamp(_climate.SoilWater[cell] / 150.0, 0, 1);
        double rain = _climate.Statistics.RecentPrecipitation[cell];   // mm/jour
        double saturation = Math.Clamp(0.7 * soil + 0.7 * Smooth(4, 50, rain), 0, 1);
        // Température au sol de la caméra (gradient de 6,5 °C/km depuis l'altitude de la cellule)
        double ground = _terrain.Explorer.GroundHeight;
        double temperature = _climate.SurfaceTemperature[cell] - 0.0065 * (ground - _tectonics.CellElevation[cell] * 1000.0);
        double freezeThaw = Math.Max(0, 1 - Math.Abs(temperature) / 6.0);
        double k = SlopeStability.SeismicCoefficient(_shakingIntensity);
        if (k > 0.02) cause = "séisme";
        else if (saturation > 0.75) cause = "pluies";
        else if (freezeThaw > 0.5) cause = "gel et dégel";
        return new SlopeConditions(saturation, k, freezeThaw);
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Taille d'un mouvement : surtout des petits, parfois un grand (loi de puissance).</summary>
    private double SlideScale(bool strong)
    {
        double u = _crustRandom.NextDouble();
        return Math.Clamp(0.5 + (strong ? 2.0 : 1.3) * u * u * u, 0.4, 2.5);
    }

    /// <summary>Séismes ressentis : les pentes sont secouées pendant quelques secondes.</summary>
    private void ShakeSlopes()
    {
        Vector3 camera = _terrain.Explorer.Position.Normalized().ToVector3();
        foreach (var quake in _activity.RecentEarthquakes)
        {
            double km = quake.Position.AngleTo(camera) * PlanetConstants.RadiusKm;
            float felt = Simulation.Tectonics.PlanetActivity.FeltIntensity(quake.Magnitude, km);
            if (felt > _shakingIntensity)
            {
                _shakingIntensity = felt;
                _crustScanTimer = 0;   // examen tout de suite : les pentes cèdent pendant la secousse
            }
        }
    }

    /// <summary>
    /// Touche B : un mouvement de terrain sur la pente la plus raide autour du point visé
    /// (centre de l'écran), comme si un séisme la secouait.
    /// </summary>
    private void TriggerLandslide()
    {
        if (_clock != ClockMode.Climate || _terrain.Field == null)
        {
            ShowNotice("Les mouvements de terrain demandent l'horloge climatique (géologie figée : touche C)");
            return;
        }
        if (_terrain.Explorer.AltitudeAboveGround > CrustMaxCameraAltitude)
        {
            ShowNotice("Approchez-vous du sol pour voir un mouvement de terrain (moins de 12 km de haut)");
            return;
        }
        if (_slideBuild != null || _slides.Count >= MaxActiveSlides)
        {
            ShowNotice("Un mouvement de terrain se prépare déjà");
            return;
        }
        Double3 target = ViewTarget();
        var field = _terrain.Field;
        var height = ThreadSafeHeight(field, 2.0);
        int seed = _slideSeed++;
        float day = (float)_climate.TimeDays;
        double scale = SlideScale(true);
        _slideBuildForced = true;
        _slideBuild = Task.Run(() =>
        {
            // La pente la plus raide autour du point visé
            var random = new Random(seed);
            var (ax, ay) = LavaFlow.TangentFrame(target);
            Double3 best = target;
            double bestSlope = -1;
            for (int s = 0; s < 120; s++)
            {
                double rho = 600 * Math.Sqrt(random.NextDouble()), phi = random.NextDouble() * 2 * Math.PI;
                Double3 d = (target + ax * (rho * Math.Cos(phi) / TerrainHeightField.RadiusMeters)
                                    + ay * (rho * Math.Sin(phi) / TerrainHeightField.RadiusMeters)).Normalized();
                var (slope, _, h) = SlopeStability.LocalSlope(height, d, 6);
                if (h > 0.5 && slope > bestSlope)
                {
                    bestSlope = slope;
                    best = d;
                }
            }
            if (bestSlope < 15 * Math.PI / 180)
                return null;
            SlideKind kind = bestSlope >= SlopeStability.CliffSlope ? SlideKind.Rockfall
                : scale > 1.8 && bestSlope > 30 * Math.PI / 180 ? SlideKind.RockAvalanche : SlideKind.Landslide;
            return new Landslide(height, best, kind, kind == SlideKind.Rockfall ? scale * 1.5 : scale, seed, day);
        });
    }

    /// <summary>Point du sol au centre de l'écran (ou sous la caméra si l'on regarde le ciel).</summary>
    private Double3 ViewTarget()
    {
        var explorer = _terrain.Explorer;
        Double3 up = explorer.Position.Normalized();
        Vector2 center = GetViewport().GetVisibleRect().Size / 2;
        Double3 ray = Double3.From(_terrain.Camera.ProjectRayNormal(center)).Normalized();
        // Rayon contre la sphère au niveau du sol de la caméra
        double radius = TerrainHeightField.RadiusMeters + explorer.GroundHeight;
        Double3 origin = explorer.Position;
        double b = ray.Dot(origin);
        double disc = b * b - origin.LengthSquared() + radius * radius;
        if (disc > 0)
        {
            double t = -b - Math.Sqrt(disc);
            if (t > 0 && t < 30000)
                return (origin + ray * t).Normalized();
        }
        // Sinon, à quelques centaines de mètres devant
        return (up + explorer.Forward() * (500 / TerrainHeightField.RadiusMeters)).Normalized();
    }

    /// <summary>Croûte modulaire : examen des pentes, mouvements en cours, lave figée, affichage.</summary>
    private void UpdateCrust(double delta)
    {
        _realSeconds += delta;
        var field = _terrain.Field;
        if (field == null)
            return;
        if (!ReferenceEquals(field.Edits, _crust))
            field.Edits = _crust;
        bool climate = _clock == ClockMode.Climate && _climate.HasTemperature;
        _terrain.SetCrustDay((float)_climate.TimeDays);
        Double3 camera = _terrain.Explorer.Position;
        Vector3 cameraDirection = camera.Normalized().ToVector3();
        _shakingIntensity = Math.Max(0, _shakingIntensity - delta * 0.4);

        if (climate)
        {
            if (_running)
                ShakeSlopes();
            ScanSlopes(delta, field);
        }
        CollectSlideBuild();

        // Vitesse des mouvements : le temps réel (un peu accéléré si l'horloge va vite) ; en pause, tout s'arrête
        double rate = !_running || !climate ? 0 : Math.Clamp(ClimateSpeeds[_climateSpeed] * 86400.0, 1.0, 3.0);
        _allParticles.Clear();
        _slideRumble = 0;
        for (int index = _slides.Count - 1; index >= 0; index--)
        {
            var active = _slides[index];
            var slide = active.Slide;
            // Grondement : selon le volume en mouvement et la distance (valeurs du dernier pas
            // terminé : le glissement ne se lit pas pendant que la tâche de fond le fait avancer)
            double km = active.Center.ToVector3().AngleTo(cameraDirection) * PlanetConstants.RadiusKm;
            active.DistanceKm = km;
            float loud = (float)Math.Min(1.0, Math.Cbrt(active.Moving / 2e4)) * Math.Clamp(1f - (float)km / 6f, 0f, 1f);
            _slideRumble = Math.Max(_slideRumble, loud);
            if (km < 2 && active.Moving > 5e4 && _terrain.Explorer.AltitudeAboveGround < 3000)
            {
                _shakeStrength = Math.Max(_shakeStrength, 0.04 * Math.Min(1.0, active.Moving / 5e5));
                _shakeSeconds = Math.Max(_shakeSeconds, 0.5);
            }
            if (active.Step != null && !active.Step.IsCompleted)
            {
                _allParticles.AddRange(active.Particles);
                continue;
            }
            if (active.Step != null)
            {
                if (active.Step.IsFaulted)
                    GD.PushError($"Landslide failed: {active.Step.Exception?.GetBaseException().Message}");
                (active.Particles, active.Back) = (active.Back, active.Particles);
                active.Center = active.NextCenter;
                active.Moving = active.NextMoving;
                if (active.Layer is { } layer)
                {
                    _crust.Publish(slide.Patch, layer.delta, layer.debris);
                    _terrain.InvalidateRegion(slide.Patch.Center, slide.Patch.ReachM);
                    active.Layer = null;
                }
                active.Step = null;
                if (slide.Finished)
                {
                    _slides.RemoveAt(index);
                    continue;
                }
            }
            _allParticles.AddRange(active.Particles);
            double dt = delta * rate;
            active.PublishTimer -= delta;
            bool publish = active.PublishTimer <= 0;
            if (publish)
                active.PublishTimer = SlidePublishSeconds;
            if (dt > 0 || publish)
            {
                var back = active.Back;
                active.Step = Task.Run(() =>
                {
                    if (dt > 0)
                        slide.Step(dt);
                    slide.GetParticles(back);
                    active.NextCenter = slide.MovingCenter();
                    active.NextMoving = slide.MovingVolume;
                    if (publish || slide.Finished)
                        active.Layer = slide.BuildLayer();
                });
            }
        }

        UpdateLavaCrust();

        // Blocs et poussière
        int cell = _grid.NearestCell(cameraDirection, Math.Max(_weatherHint, 0));
        Vector3 wind = climate ? _climate.Winds.Wind[cell] : Vector3.Zero;
        Color light = _sky.Sun.LightColor * Mathf.Clamp(_sky.Sun.LightEnergy, 0.15f, 1.2f);
        _debris.UpdateDebris(delta, camera, _allParticles, wind, light, new Color(0.62f, 0.55f, 0.47f));
    }

    /// <summary>Examen des pentes autour de la caméra : celle qui cède part en tâche de fond.</summary>
    private void ScanSlopes(double delta, TerrainHeightField field)
    {
        _crustScanTimer -= delta;
        if (!_running || _crustScanTimer > 0 || SlopeInstability <= 0)
            return;
        _crustScanTimer = CrustScanSeconds;
        if (_terrain.Explorer.AltitudeAboveGround > CrustMaxCameraAltitude || _slideBuild != null || _slides.Count >= MaxActiveSlides)
            return;
        var conditions = CrustConditions(out _);
        // Les pentes cèdent d'autant plus souvent que le réglage est fort (et toujours un peu)
        double instability = SlopeInstability;
        conditions = conditions with
        {
            Saturation = Math.Min(1, conditions.Saturation * (0.6 + 0.4 * instability)),
            SeismicK = conditions.SeismicK * Math.Min(2, instability),
        };
        // Zone examinée : vers où regarde la caméra
        var explorer = _terrain.Explorer;
        double ahead = Math.Min(1500, 300 + explorer.AltitudeAboveGround);
        Double3 center = (explorer.Position.Normalized() + explorer.Forward() * (ahead / TerrainHeightField.RadiusMeters)).Normalized();
        var height = ThreadSafeHeight(field, 2.0);
        int seed = _slideSeed++;
        float day = (float)_climate.TimeDays;
        // (éboulements spontanés : une paroi examinée a quelques chances sur mille de lâcher un bloc)
        double rockfallChance = 0.004 * instability;
        bool strong = conditions.SeismicK > 0.1;
        double scale = SlideScale(strong);
        _slideBuildForced = false;
        _slideBuild = Task.Run(() =>
        {
            var random = new Random(seed);
            var candidate = SlopeStability.FindWeakest(height, center, CrustScanRadiusM, 140, conditions, random, rockfallChance);
            if (candidate is not { } c)
                return null;
            return new Landslide(height, c.Direction, c.Kind, c.Kind == SlideKind.Rockfall ? scale * 1.3 : scale, seed, day);
        });
    }

    private void CollectSlideBuild()
    {
        if (_slideBuild == null || !_slideBuild.IsCompleted)
            return;
        var build = _slideBuild;
        _slideBuild = null;
        if (build.IsFaulted)
        {
            GD.PushError($"Landslide build failed: {build.Exception?.GetBaseException().Message}");
            return;
        }
        var slide = build.Result;
        if (slide == null)
        {
            if (_slideBuildForced)
                ShowNotice("Pas de pente assez raide par ici pour un mouvement de terrain");
            return;
        }
        // Le relief a pu être remplacé entre-temps (nouvelle planète) : on jette
        if (_clock != ClockMode.Climate)
            return;
        _crust.Add(slide.Patch);
        _slides.Add(new ActiveSlide(slide));
        _slideCount++;
        if (slide.Kind == SlideKind.Rockfall)
            _rockfallCount++;
        _slideVolume += slide.ReleasedVolume;
        CrustConditions(out string cause);
        double km = slide.Source.ToVector3().AngleTo(_terrain.Explorer.Position.Normalized().ToVector3()) * PlanetConstants.RadiusKm;
        string what = slide.Kind switch
        {
            SlideKind.Rockfall => "Éboulement",
            SlideKind.RockAvalanche => "Avalanche rocheuse",
            _ => "Glissement de terrain",
        };
        _lastSlide = $"{what} de {VolumeLabel(slide.ReleasedVolume)} à {Distance(km * 1000)}" +
                     (_slideBuildForced ? " (provoqué)" : cause.Length > 0 ? $" ({cause})" : "");
        ShowNotice(_lastSlide);
    }

    private static string VolumeLabel(double m3) => m3 switch
    {
        >= 1e6 => $"{French(m3 / 1e6)} millions de m³",
        >= 1e4 => $"{m3 / 1000:N0} 000 m³",
        _ => $"{m3:N0} m³",
    };

    /// <summary>
    /// Lave figée de la coulée simulée : son basalte est inscrit dans la croûte toutes les
    /// quelques secondes (la grille de la coulée et le calque ont le même repère).
    /// </summary>
    private void UpdateLavaCrust()
    {
        if (_lava == null || (_lavaStep != null && !_lavaStep.IsCompleted))
            return;
        _lavaPublishTimer -= GetProcessDeltaTime();
        if (_lavaPublishTimer > 0)
            return;
        _lavaPublishTimer = 3.0;
        float[] basalt = _lava.BasaltLayer();
        if (basalt == null)
            return;
        if (!ReferenceEquals(_lavaPatchFlow, _lava))
        {
            _lavaPatchFlow = _lava;
            _lavaPatch = new CrustPatch(_lava.Vent, LavaFlow.Size, LavaFlow.CellM, (float)_climate.TimeDays, "Lava", _lava.AxisX, _lava.AxisY);
            _crust.Add(_lavaPatch);
        }
        var debris = new float[basalt.Length];
        for (int k = 0; k < basalt.Length; k++)
            debris[k] = basalt[k] > 0.05f ? 2f : 0f;   // 2 : lave figée (voir le shader du terrain)
        _crust.Publish(_lavaPatch, basalt, debris);
        _terrain.InvalidateRegion(_lavaPatch.Center, _lavaPatch.ReachM);
    }

    // --- Interface ---------------------------------------------------------------------------------

    private void RegisterCrust()
    {
        var registry = _ui.Registry;
        registry.Add(new SimParameter
        {
            Id = "crust.instability", Group = "Croûte", Label = "Instabilité des pentes", Unit = "×", Min = 0, Max = 5, Step = 0.1, Reference = 1, Format = "0.0",
            Help = "Fréquence des glissements de terrain et des éboulements autour de la caméra (0 : rien ne bouge). " +
                   "Agit dans l'horloge climatique, géologie figée. Touche B : provoquer un mouvement de terrain.",
            Get = () => SlopeInstability, Set = v => SlopeInstability = v,
        });
        registry.Add(new ProbeField
        {
            Label = "Croûte",
            Read = c =>
            {
                if (!_geologyLocked)
                    return null;
                // Calques de la croûte qui touchent la cellule
                Vector3 p = _grid.Positions[c];
                int count = 0;
                foreach (var patch in _crust.Patches)
                    if (patch.Center.ToVector3().AngleTo(p) * PlanetConstants.RadiusKm < 120)
                        count++;
                return count == 0 ? "intacte (géologie figée)" : $"{count} modification{(count > 1 ? "s" : "")} (glissements, éboulements, lave)";
            },
        });
        registry.Add(new TimeSeries
        {
            Label = "Mouvements de terrain", Format = "0", Color = (0.8f, 0.6f, 0.4f), Shown = false,
            Sample = () => _geologyLocked ? _slideCount : null,
        });
    }

    /// <summary>Ligne des détails (touche I) : la croûte modulaire.</summary>
    private string CrustLine()
    {
        if (!_geologyLocked)
            return "Croûte : la géologie avance (C : la figer et passer au climat ; la croûte devient alors modulaire)";
        string moving = _slides.Count > 0 ? $"   en cours : {_slides.Count}" : "";
        string last = _lastSlide != null ? $"   dernier : {_lastSlide}" : "";
        return $"Croûte modulaire : {_crust.Count} calques, {_slideCount} mouvements de terrain dont {_rockfallCount} éboulements " +
               $"({VolumeLabel(_slideVolume)}){moving}{last}   morceaux à refaire : {_terrain.StaleRegionChunkCount}";
    }
}
