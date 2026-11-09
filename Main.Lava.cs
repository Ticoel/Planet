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
        eruption == null ? 0 : (eruption.Vei <= 1 ? 80.0 : 10.0) * eruption.IntensityAt(day);

    /// <param name="nearest">Éruption terrestre la plus proche (null s'il n'y en a pas).</param>
    private void UpdateLava(Eruption nearest, bool climate)
    {
        if (!climate || _terrain.Field == null)
        {
            _terrain.SetShaderParameter("lava_active", 0f);
            _lavaDay = double.NaN;
            return;
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
            _lavaBuild = Task.Run(() => new LavaFlow(field, vent));
        }
        if (_lavaBuild is { IsCompleted: true })
        {
            if (_lavaStep is { IsCompleted: false })
                _lavaStep.Wait();
            _lava = _lavaBuild.IsCompletedSuccessfully ? _lavaBuild.Result : null;
            _lavaBuild = null;
            _lavaPendingSeconds = 0;
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
    }
}
