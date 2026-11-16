using System;
using System.Threading.Tasks;
using Godot;
using Planet.Simulation;
using Planet.Simulation.Tectonics;
using Planet.Simulation.Terrain;

namespace Planet;

/// <summary>
/// Coulée de lave simulée (<see cref="LavaFlow"/>) autour de la bouche éruptive la plus proche de
/// la caméra : la grille est posée sur le relief en tâche de fond, puis avance d'autant de pas que
/// le temps simulé en demande (plafonné : à grande vitesse, la coulée avance en accéléré mais
/// reste lisible), et son image est envoyée au shader du terrain.
/// </summary>
public partial class Main
{
    /// <summary>Au-delà de cette distance de la caméra (km), pas de coulée simulée.</summary>
    private const double LavaMaxDistanceKm = 300;
    /// <summary>Pas de simulation au plus par image (~0,1 ms chacun).</summary>
    private const int LavaMaxStepsPerFrame = 120;

    private LavaFlow _lava;
    private TerrainHeightField _lavaField, _lavaBuildField;
    private Vector3 _lavaVent;
    private Task<LavaFlow> _lavaBuild;
    private Task _lavaStep;
    private double _lavaDay = double.NaN;
    private double _lavaPendingSeconds;
    private readonly byte[] _lavaPixels = new byte[LavaFlow.Size * LavaFlow.Size * 2];
    private Image _lavaImage;
    private ImageTexture _lavaTexture;

    /// <summary>Débit de lave (m³/s) : fort pour les volcans boucliers (type hawaïen), faible pour les explosifs.</summary>
    private static double EffusionRate(Eruption eruption, double day) =>
        eruption == null ? 0 : (eruption.Fissure ? 250.0 : eruption.Vei <= 1 ? 80.0 : 10.0) * eruption.IntensityAt(day);

    /// <param name="nearest">Éruption terrestre la plus proche (null s'il n'y en a pas).</param>
    private void UpdateLava(Eruption nearest, bool climate)
    {
        if (!climate || _terrain.Field == null)
        {
            _terrain.SetShaderParameter("lava_active", 0f);
            _lavaDay = double.NaN;
            return;
        }
        // Relief changé (nouvelle planète, retour de l'horloge géologique) : la coulée ne vaut plus
        if (!ReferenceEquals(_lavaField, _terrain.Field))
        {
            _lavaField = _terrain.Field;
            if (_lavaStep is { IsCompleted: false })
                _lavaStep.Wait();
            _lavaStep = null;
            _lava = null;
            _lavaVent = default;
            _lavaBuildField = null;
        }
        double day = _climate.TimeDays;
        Vector3 camera = _terrain.Explorer.Position.Normalized().ToVector3();
        bool near = nearest != null && nearest.Position.AngleTo(camera) * PlanetConstants.RadiusKm < LavaMaxDistanceKm;

        // Nouvelle bouche : grille posée sur le relief en tâche de fond (~65 000 points)
        if (near && nearest.Position != _lavaVent && _lavaBuild == null)
        {
            var field = _terrain.Field;
            var vent = Double3.From(nearest.Position).Normalized();
            _lavaVent = nearest.Position;
            _lavaBuildField = field;
            // (fissure : la lave sort tout le long, pas seulement au milieu)
            var (fa, fb) = nearest.Ends();
            bool fissure = nearest.Fissure;
            _lavaBuild = Task.Run(() =>
            {
                var flow = new LavaFlow(field, vent);
                if (fissure)
                    flow.SetFissure(Double3.From(fa), Double3.From(fb));
                return flow;
            });
        }
        if (_lavaBuild is { IsCompleted: true })
        {
            if (_lavaStep is { IsCompleted: false })
                _lavaStep.Wait();
            // (une grille posée sur un relief remplacé entre-temps est jetée, et refaite sur le
            // nouveau ; une grille qui n'a pas pu être posée n'est pas redemandée à chaque image)
            bool built = _lavaBuild.IsCompletedSuccessfully;
            if (!built)
                GD.PushWarning($"Lava flow failed: {_lavaBuild.Exception?.GetBaseException().Message}");
            _lava = built && ReferenceEquals(_lavaBuildField, _terrain.Field) ? _lavaBuild.Result : null;
            if (_lava == null && built)
                _lavaVent = default;
            _lavaBuild = null;
            _lavaStep = null;
            _lavaPendingSeconds = 0;
            // L'image de l'ancienne coulée ne doit pas s'afficher sur le nouveau volcan : rien
            // n'est montré avant le premier pas de la nouvelle
            _terrain.SetShaderParameter("lava_active", 0f);
            if (_lava != null)
            {
                _terrain.SetShaderParameter("lava_center", _lava.Vent.ToVector3());
                _terrain.SetShaderParameter("lava_axis_x", _lava.AxisX.ToVector3());
                _terrain.SetShaderParameter("lava_axis_y", _lava.AxisY.ToVector3());
                _terrain.SetShaderParameter("lava_half_extent", (float)LavaFlow.HalfExtentM);
            }
        }
        if (_lava == null)
        {
            _terrain.SetShaderParameter("lava_active", 0f);
            return;
        }

        // Temps simulé écoulé (pas de saut quand on revient dans l'horloge climatique)
        if (!double.IsNaN(_lavaDay) && day > _lavaDay)
            _lavaPendingSeconds += (day - _lavaDay) * 86400.0;
        _lavaDay = day;

        // L'image précédente est prête : on l'envoie, puis on lance les pas suivants
        if (_lavaStep == null || _lavaStep.IsCompleted)
        {
            if (_lavaStep != null)
                UploadLavaTexture();
            int steps = (int)Math.Min(LavaMaxStepsPerFrame, _lavaPendingSeconds / LavaFlow.StepSeconds);
            if (steps > 0)
            {
                // (l'excédent est oublié : à grande vitesse, la coulée avance en accéléré)
                _lavaPendingSeconds = steps < LavaMaxStepsPerFrame ? _lavaPendingSeconds - steps * LavaFlow.StepSeconds : 0;
                var lava = _lava;
                double rate = nearest != null && nearest.Position == _lavaVent ? EffusionRate(nearest, day) : 0;
                _lavaStep = Task.Run(() =>
                {
                    lava.Step(steps, rate);
                    lava.FillTexture(_lavaPixels);
                });
            }
            else
                _lavaStep = null;
        }
    }

    private readonly Vector4[] _fissureA = new Vector4[4], _fissureB = new Vector4[4];

    /// <summary>
    /// Fissures en éruption proches de la caméra (moins de 400 km) : leur ligne incandescente est
    /// dessinée par le shader du terrain (extrémités relatives à la caméra, intensité en w).
    /// </summary>
    private void UpdateFissureGlow(bool climate)
    {
        int count = 0;
        if (climate && _terrain.Field != null)
        {
            Double3 camera = _terrain.Explorer.Position;
            Vector3 cameraDirection = camera.Normalized().ToVector3();
            double day = _climate.TimeDays;
            foreach (var eruption in _activity.Eruptions)
            {
                if (!eruption.Fissure || count >= _fissureA.Length || eruption.Position.AngleTo(cameraDirection) * PlanetConstants.RadiusKm > 400)
                    continue;
                float intensity = eruption.IntensityAt(day);
                var (a, b) = eruption.Ends();
                float height = VentHeight(eruption.Position);
                if (intensity <= 0 || height < 0)
                    continue;
                Double3 pa = Double3.From(a).Normalized() * (TerrainHeightField.RadiusMeters + height) - camera;
                Double3 pb = Double3.From(b).Normalized() * (TerrainHeightField.RadiusMeters + height) - camera;
                _fissureA[count] = new Vector4((float)pa.X, (float)pa.Y, (float)pa.Z, intensity);
                _fissureB[count] = new Vector4((float)pb.X, (float)pb.Y, (float)pb.Z, 0f);
                count++;
            }
        }
        _terrain.SetShaderParameter("fissure_count", count);
        _terrain.SetShaderParameter("fissure_a", _fissureA);
        _terrain.SetShaderParameter("fissure_b", _fissureB);
    }

    private void UploadLavaTexture()
    {
        if (_lavaImage == null)
        {
            _lavaImage = Image.CreateFromData(LavaFlow.Size, LavaFlow.Size, false, Image.Format.Rg8, _lavaPixels);
            _lavaTexture = ImageTexture.CreateFromImage(_lavaImage);
            _terrain.SetShaderParameter("lava_map", _lavaTexture);
        }
        else
        {
            _lavaImage.SetData(LavaFlow.Size, LavaFlow.Size, false, Image.Format.Rg8, _lavaPixels);
            _lavaTexture.Update(_lavaImage);
        }
        _terrain.SetShaderParameter("lava_active", 1f);
        // (pas terminé : la grille est stable, son basalte peut être inscrit dans la croûte)
        UpdateLavaCrust();
    }
}
