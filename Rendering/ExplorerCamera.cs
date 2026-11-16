using System;
using Godot;
using Planet.Simulation.Terrain;

namespace Planet.Rendering;

/// <summary>Commandes d'un instant (chacune de −1 à 1).</summary>
public readonly record struct ExplorerInput(float Forward, float Strafe, float Vertical, float Turn, float Pitch, bool Run, bool Jump);

/// <summary>
/// Caméra unique, de l'orbite jusqu'au sol, à l'échelle réelle (avatar invisible). Position en double précision
/// dans le repère de la planète ; orientation par cap (0 = nord) et inclinaison, relatives au
/// sol local, si bien que l'horizon reste horizontal où que l'on soit sur la sphère.
/// <list type="bullet">
/// <item><b>Vol</b> : on avance à l'horizontale selon le cap, quelle que soit l'inclinaison du
/// regard (sinon, en regardant vers le bas, on plongeait vers le sol en avançant) ; on monte et
/// descend avec des touches à part. Vitesse proportionnelle à l'altitude : on traverse un
/// continent en quelques minutes, on frôle le sol lentement ;</item>
/// <item><b>Marche</b> : yeux à 1,7 m du sol, gravité, saut ;</item>
/// <item><b>Molette</b> : zoom (l'altitude est multipliée ou divisée), le regard passant de la
/// verticale vu de l'espace à l'horizon près du sol, comme dans un globe virtuel ;</item>
/// <item><b>Clic gauche + glisser</b> : on « attrape » le sol et on le fait glisser.</item>
/// </list>
/// </summary>
public sealed class ExplorerCamera
{
    public const double EyeHeight = 1.7;
    private const double WalkSpeed = 6.0;           // m/s (×3 en courant)
    // Vitesses de vol, proportionnelles à la hauteur au-dessus du sol (par seconde). Essais de
    // Nicolas : ×1 un peu lent à l'horizontale, ×3 beaucoup trop rapide, surtout à la verticale
    private const double MinFlySpeed = 8.0;         // m/s
    private const double FlySpeedPerMeter = 1.5;    // à l'horizontale
    private const double ClimbSpeedPerMeter = 0.8;  // à la verticale
    private const double MaxAngularSpeed = 0.3;     // rad/s : vu de très haut, on ne fait pas le tour en 2 s
    private const double RunFactor = 3.0;           // touche Maj en vol
    private const double ZoomFraction = 0.15;       // molette : pas = 15 % de la distance regardée
    private const double PanFactor = 1.4;           // glisser : mètres par pixel = hauteur × facteur / hauteur d'écran
    private const double TurnRate = 1.6;            // rad/s (flèches gauche/droite)
    private const double PitchRate = 1.0;           // rad/s
    private const double MaxAltitude = 5.0e7;       // m
    private const double Gravity = 9.81;
    private const double JumpSpeed = 5.0;
    private const double MouseSensitivity = 0.0025; // radians par pixel
    private const double MaxPitch = 1.53;

    public Double3 Position { get; private set; }
    /// <summary>Cap (radians, 0 = nord, positif vers l'est), déduit de la direction de marche.</summary>
    public double Yaw
    {
        get
        {
            var (_, north, east) = LocalFrame();
            return Math.Atan2(_heading.Dot(east), _heading.Dot(north));
        }
    }
    /// <summary>Inclinaison (radians, positif vers le haut).</summary>
    public double Pitch { get; private set; }
    public bool Walking { get; private set; }
    /// <summary>Multiplicateur de vitesse en vol (molette).</summary>
    public double SpeedFactor { get; private set; } = 1.0;

    /// <summary>Altitude de la surface sous la caméra (m ; la mer et les lacs comptent comme un sol).</summary>
    public double GroundHeight { get; private set; }
    /// <summary>Hauteur de la caméra au-dessus du sol (m).</summary>
    public double AltitudeAboveGround { get; private set; }
    /// <summary>Vitesse du dernier déplacement (m/s).</summary>
    public double Speed { get; private set; }

    private TerrainHeightField _field;

    /// <summary>
    /// Grottes (relief en volume) : espace libre à l'aplomb d'un point, ou null hors des grottes.
    /// Dans une grotte, le sol est celui de la galerie (on y marche, on y tombe par l'ouverture) et
    /// les parois arrêtent la caméra.
    /// </summary>
    public Func<Double3, CaveSpace?> CaveProbe { get; set; }
    /// <summary>Centre de la galerie la plus proche d'un point (pour sortir la caméra de la roche), ou null.</summary>
    public Func<Double3, Double3?> CaveRescue { get; set; }

    /// <summary>Roche, ou espace plus bas que les yeux (en marchant) : on ne peut pas y aller.</summary>
    private bool Blocked(CaveSpace space) =>
        space.Solid || (!double.IsInfinity(space.CeilingRadius)
                        && space.CeilingRadius - 0.3 < space.FloorRadius + (Walking ? EyeHeight : 0.6));

    /// <summary>Vrai quand la caméra est dans une grotte (le sol est celui de la galerie).</summary>
    public bool InCave { get; private set; }
    // Direction de marche, tangente au sol. Elle est transportée d'un point à l'autre quand la
    // caméra se déplace (au lieu d'un cap mesuré depuis le nord, indéfini aux pôles)
    private Double3 _heading = new(0, 1, 0);
    private double _verticalSpeed;
    private int _hint = -1;

    public void SetField(TerrainHeightField field)
    {
        _field = field;
        _hint = -1;
    }

    /// <summary>Place la caméra au-dessus d'un point, regard vers le nord (inclinaison selon l'altitude).</summary>
    public void PlaceAbove(Double3 direction, double altitude)
    {
        direction = direction.Normalized();
        double ground = SampleGround(direction);
        Position = direction * (TerrainHeightField.RadiusMeters + ground + altitude);
        var (_, north, _) = LocalFrame();
        _heading = north;
        Pitch = AutoPitch(altitude);
        Walking = false;
        InCave = false;
        _verticalSpeed = 0;
        UpdateGround();
    }

    /// <summary>Tourne le regard vers une direction (repère de la planète), avec une inclinaison donnée (radians).</summary>
    public void Face(Double3 target, double pitch)
    {
        Double3 up = Position.Normalized();
        Double3 toward = target - up * target.Dot(up);
        if (toward.LengthSquared() > 1e-12)
            _heading = toward.Normalized();
        Pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);
    }

    /// <summary>Tourne le regard pour viser exactement un point (repère de la planète, m) : il est au centre de l'écran.</summary>
    public void LookAt(Double3 point)
    {
        Double3 up = Position.Normalized();
        Double3 d = point - Position;
        Double3 horizontal = d - up * d.Dot(up);
        if (horizontal.LengthSquared() > 1e-6)
            _heading = horizontal.Normalized();
        Pitch = Math.Clamp(Math.Atan2(d.Dot(up), horizontal.Length()), -MaxPitch, MaxPitch);
    }

    // --- Transitions ----------------------------------------------------------------------------

    private bool _transition;
    private double _transitionTime, _transitionSeconds;
    private Double3 _fromPosition, _toPosition, _fromHeading, _toHeading;
    private double _fromPitch, _toPitch, _transitionPeak;
    private bool _toWalking;

    /// <summary>Vrai pendant un vol de transition vers une nouvelle vue.</summary>
    public bool InTransition => _transition;

    /// <summary>
    /// Va vers une nouvelle vue en volant au lieu d'y sauter : <paramref name="place"/> place la
    /// caméra (PlaceAbove, LookAt…), puis la caméra y vole depuis sa position actuelle en quelques
    /// secondes (montée, voyage, descente, comme un globe virtuel), le temps que le relief se
    /// charge. Une touche de déplacement interrompt le vol.
    /// </summary>
    public void Transition(Action place)
    {
        Double3 position = Position, heading = _heading;
        double pitch = Pitch;
        place();
        _toPosition = Position;
        _toHeading = _heading;
        _toPitch = Pitch;
        _toWalking = Walking;
        double arc = Math.Acos(Math.Clamp(position.Normalized().Dot(_toPosition.Normalized()), -1, 1)) * TerrainHeightField.RadiusMeters;
        if (arc < 30 && Math.Abs(position.Length() - _toPosition.Length()) < 30)
            return;                                         // presque au même endroit : pas de vol
        _fromPosition = position;
        _fromHeading = heading;
        _fromPitch = pitch;
        Position = position;
        _heading = heading;
        Pitch = pitch;
        Walking = false;
        InCave = false;
        _verticalSpeed = 0;
        // Sommet du vol : assez haut pour voir le chemin (le tiers de la distance), durée selon la distance
        _transitionPeak = Math.Min(arc * 0.35, 2.0e6);
        double span = Math.Max(arc, Math.Abs(position.Length() - _toPosition.Length()));
        _transitionSeconds = Math.Clamp(1.2 + 0.9 * Math.Log10(1 + span / 1000), 1.2, 4.5);
        _transitionTime = 0;
        _transition = true;
        UpdateGround();
    }

    private void UpdateTransition(double delta, ExplorerInput input)
    {
        if (input.Forward != 0 || input.Strafe != 0 || input.Vertical != 0)
        {
            _transition = false;                            // le joueur reprend la main
            return;
        }
        _transitionTime += delta;
        double t = Math.Min(1, _transitionTime / _transitionSeconds);
        double s = t * t * (3 - 2 * t);
        double radius = TerrainHeightField.RadiusMeters;
        // Direction : arc de grand cercle ; altitude : de l'une à l'autre, avec une bosse au milieu
        Double3 a = _fromPosition.Normalized(), b = _toPosition.Normalized();
        double omega = Math.Acos(Math.Clamp(a.Dot(b), -1, 1));
        Double3 direction = omega < 1e-9 ? b
            : (a * Math.Sin((1 - s) * omega) + b * Math.Sin(s * omega)) / Math.Sin(omega);
        double h0 = _fromPosition.Length() - radius, h1 = _toPosition.Length() - radius;
        // (altitude interpolée en échelle logarithmique : de l'orbite au sol, la descente ne se fait
        // pas toute dans la dernière fraction de seconde)
        double logHeight = Math.Exp(Math.Log(Math.Max(h0, 0) + 10) * (1 - s) + Math.Log(Math.Max(h1, 0) + 10) * s) - 10;
        double height = logHeight + Math.Max(0, _transitionPeak - Math.Max(h0, h1)) * Math.Sin(Math.PI * s);
        Position = direction.Normalized() * (radius + height);
        // Regard : du regard de départ à celui d'arrivée (en passant par le bas : on voit le sol défiler)
        Double3 heading = _fromHeading * (1 - s) + _toHeading * s;
        _heading = heading.LengthSquared() < 1e-6 ? _toHeading : heading.Normalized();
        TransportHeading();
        double bump = Math.Max(0, _transitionPeak - Math.Max(h0, h1)) > 1000 ? Math.Sin(Math.PI * s) : 0;
        Pitch = Math.Clamp(_fromPitch + (_toPitch - _fromPitch) * s - 0.6 * bump, -MaxPitch, MaxPitch);
        // Jamais sous le sol
        double minimum = radius + SampleGround(Position.Normalized()) + 2;
        if (Position.Length() < minimum)
            Position = Position.Normalized() * minimum;
        if (t >= 1)
        {
            Position = _toPosition;
            _heading = _toHeading;
            Pitch = _toPitch;
            Walking = _toWalking;
            _transition = false;
        }
        UpdateGround();
    }

    public void ToggleWalking()
    {
        Walking = !Walking;
        _verticalSpeed = 0;
        if (Walking)
            Pitch = Math.Clamp(Pitch, -0.8, 0.8);
    }

    public void Look(Vector2 mouseDelta)
    {
        Turn(mouseDelta.X * MouseSensitivity);
        Pitch = Math.Clamp(Pitch - mouseDelta.Y * MouseSensitivity, -MaxPitch, MaxPitch);
    }

    /// <summary>Tourne la direction de marche autour de la verticale (positif : vers la droite).</summary>
    private void Turn(double angle)
    {
        Double3 up = Position.Normalized();
        _heading = (_heading * Math.Cos(angle) + _heading.Cross(up) * Math.Sin(angle)).Normalized();
    }

    /// <summary>Remet la direction de marche dans le plan du sol après un déplacement.</summary>
    private void TransportHeading()
    {
        Double3 up = Position.Normalized();
        Double3 heading = _heading - up * _heading.Dot(up);
        if (heading.LengthSquared() < 1e-12)
        {
            var (_, north, _) = LocalFrame();
            heading = north;
        }
        _heading = heading.Normalized();
    }

    public void ChangeSpeed(int direction) =>
        SpeedFactor = Math.Clamp(SpeedFactor * Math.Pow(1.25, direction), 0.05, 50.0);

    /// <summary>
    /// Inclinaison du regard conseillée à une altitude : vers le bas (−85°) vu de l'espace, vers
    /// l'horizon (−12°) près du sol, interpolée sur l'échelle logarithmique des altitudes.
    /// </summary>
    public static double AutoPitch(double altitude)
    {
        // (regard presque horizontal jusqu'à ~10 km : avant, dès quelques kilomètres, la caméra
        // plongeait vers le sol et les reliefs, volcans compris, ne se voyaient plus de profil)
        double t = Math.Clamp((Math.Log10(Math.Max(altitude, 1.0)) - 4.0) / (6.3 - 4.0), 0.0, 1.0);
        t = t * t * (3 - 2 * t);
        return -0.21 + (-1.48 + 0.21) * t;
    }

    /// <summary>
    /// Molette : +1 avance dans l'axe du regard, −1 recule, sans changer l'orientation. Le pas est
    /// une fraction de la distance de ce qu'on regarde (le sol devant soi), pour avancer vite de
    /// loin et finement de près.
    /// </summary>
    public void Zoom(int direction)
    {
        // (dans une grotte, la molette ramenait la caméra au-dessus du relief)
        if (Walking || InCave)
            return;
        _transition = false;
        double height = Math.Max(AltitudeAboveGround, 2.0);
        // Distance du sol dans l'axe du regard (vers l'horizon : quelques fois l'altitude)
        double target = Pitch < -0.05 ? height / Math.Sin(-Pitch) : Math.Max(5 * height, 2000);
        double step = ZoomFraction * Math.Min(target, 4 * height + 2000) * direction;
        Double3 moved = Position + Forward() * step;
        // Jamais sous le sol ni trop loin
        Double3 up = moved.Normalized();
        double minimum = TerrainHeightField.RadiusMeters + SampleGround(up) + 2.0;
        Position = up * Math.Clamp(moved.Length(), minimum, TerrainHeightField.RadiusMeters + MaxAltitude);
        TransportHeading();
        UpdateGround();
    }

    /// <summary>Clic gauche + glisser : déplace la caméra comme si l'on faisait glisser le sol.</summary>
    public void Pan(Vector2 pixels, float viewportHeight)
    {
        if (Walking || InCave || viewportHeight <= 0)
            return;
        _transition = false;
        Double3 up = Position.Normalized();
        Double3 heading = _heading;
        Double3 right = heading.Cross(up);
        double metersPerPixel = Math.Max(AltitudeAboveGround, 2.0) * PanFactor / viewportHeight;
        double radius = Position.Length();
        Position = (Position + (heading * pixels.Y - right * pixels.X) * metersPerPixel).Normalized() * radius;
        TransportHeading();
        UpdateGround();
    }

    /// <summary>Repère local : haut (vertical), nord et est.</summary>
    public (Double3 up, Double3 north, Double3 east) LocalFrame()
    {
        Double3 up = Position.Normalized();
        Double3 north = Double3.UnitY - up * up.Dot(Double3.UnitY);
        // Aux pôles, le nord n'est pas défini : on prend une direction fixe
        north = north.LengthSquared() < 1e-10 ? new Double3(0, 0, -1) : north.Normalized();
        Double3 east = north.Cross(up);
        return (up, north, east);
    }

    public Double3 Forward()
    {
        Double3 up = Position.Normalized();
        return _heading * Math.Cos(Pitch) + up * Math.Sin(Pitch);
    }

    /// <summary>
    /// Orientation de la caméra Godot (elle regarde selon −Z). Construite directement à partir de
    /// la direction de marche, de la droite et de l'inclinaison, en double précision : stable même
    /// en regardant presque à la verticale (avec « regarder vers ce point, le haut étant la
    /// verticale », la caméra tournoyait quand le regard et la verticale se confondaient).
    /// </summary>
    public Basis Basis()
    {
        Double3 up = Position.Normalized();
        Double3 right = _heading.Cross(up).Normalized();
        Double3 forward = _heading * Math.Cos(Pitch) + up * Math.Sin(Pitch);
        Double3 cameraUp = right.Cross(forward).Normalized();
        return new Basis(right.ToVector3(), cameraUp.ToVector3(), (-forward).ToVector3());
    }

    /// <summary>Déplacement et rotation de la caméra pendant <paramref name="delta"/> secondes.</summary>
    public void Update(double delta, ExplorerInput input)
    {
        if (_field == null || delta <= 0)
            return;
        if (_transition)
        {
            UpdateTransition(delta, input);
            if (_transition)
                return;
        }
        Turn(input.Turn * TurnRate * delta);
        Pitch = Math.Clamp(Pitch + input.Pitch * PitchRate * delta, -MaxPitch, MaxPitch);

        Double3 up = Position.Normalized();
        Double3 start = Position;
        Double3 heading = _heading;
        Double3 right = heading.Cross(up);
        Double3 horizontal = heading * input.Forward + right * input.Strafe;
        if (horizontal.LengthSquared() > 1)
            horizontal = horizontal.Normalized();

        if (!Walking)
        {
            double boost = SpeedFactor * (input.Run ? RunFactor : 1);
            double height = Math.Max(AltitudeAboveGround, 0.0);
            double speed = Math.Min(Math.Max(MinFlySpeed, height * FlySpeedPerMeter), MaxAngularSpeed * Position.Length()) * boost;
            double climb = Math.Max(MinFlySpeed * 0.5, height * ClimbSpeedPerMeter) * boost;
            // On suit la courbure : le déplacement horizontal garde la distance au centre
            double radius = Position.Length() + input.Vertical * climb * delta;
            Position = (Position + horizontal * speed * delta).Normalized() * radius;
        }
        else
        {
            double radius = Position.Length();
            Position = (Position + horizontal * WalkSpeed * (input.Run ? 3 : 1) * delta).Normalized() * radius;
            if (input.Jump && AltitudeAboveGround <= EyeHeight + 0.05)
                _verticalSpeed = JumpSpeed;
            _verticalSpeed -= Gravity * delta;
            Position += Position.Normalized() * _verticalSpeed * delta;
        }

        // Dans une grotte : sol et plafond de la galerie ; les parois arrêtent
        var cave = CaveProbe?.Invoke(Position);
        if (cave.HasValue)
        {
            var space = cave.Value;
            if (Blocked(space) && InCave)
            {
                // Contre une paroi, ou passage plus bas que les yeux : on reste où l'on était ; si
                // l'on était déjà dans la roche (coincé), retour au centre de la galerie la plus
                // proche (avant : la caméra restait dans la roche et voyait à travers tout)
                var before = CaveProbe(start);
                if (before.HasValue && !Blocked(before.Value))
                    Position = start;
                else if (CaveRescue?.Invoke(Position) is Double3 rescue)
                    Position = rescue;
                _verticalSpeed = 0;
                TransportHeading();
                Speed = 0;
                return;
            }
            if (!Blocked(space))
            {
                double low = space.FloorRadius + (Walking ? EyeHeight : 0.6);
                double high = double.IsInfinity(space.CeilingRadius) ? double.MaxValue : Math.Max(low, space.CeilingRadius - 0.3);
                double r = Math.Clamp(Position.Length(), low, high);
                if ((r <= low && _verticalSpeed < 0) || (r >= high && _verticalSpeed > 0))
                    _verticalSpeed = 0;
                Position = Position.Normalized() * r;
                InCave = true;
                TransportHeading();
                Speed = Position.DistanceTo(start) / delta;
                GroundHeight = space.FloorRadius - TerrainHeightField.RadiusMeters;
                AltitudeAboveGround = Position.Length() - space.FloorRadius;
                return;
            }
        }
        InCave = false;

        // Jamais sous le sol (ni trop loin)
        Double3 direction = Position.Normalized();
        double ground = SampleGround(direction);
        double minimum = TerrainHeightField.RadiusMeters + ground + (Walking ? EyeHeight : 2.0);
        double distance = Math.Clamp(Position.Length(), minimum, TerrainHeightField.RadiusMeters + MaxAltitude);
        if (distance <= minimum && _verticalSpeed < 0)
            _verticalSpeed = 0;
        Position = direction * distance;
        TransportHeading();

        Speed = Position.DistanceTo(start) / delta;
        UpdateGround();
    }

    /// <summary>Altitude de la surface (sol, ou eau qui le recouvre : on marche sur l'eau).</summary>
    private double SampleGround(Double3 direction) =>
        _field == null ? 0 : Math.Max(0, _field.Sample(direction, 0.5, ref _hint).Surface);

    private void UpdateGround()
    {
        GroundHeight = SampleGround(Position.Normalized());
        AltitudeAboveGround = Position.Length() - TerrainHeightField.RadiusMeters - GroundHeight;
    }
}
