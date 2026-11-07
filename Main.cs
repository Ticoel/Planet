using System.Diagnostics;
using Godot;
using Planet.Rendering;
using Planet.Simulation;

namespace Planet;

/// <summary>
/// Scène principale de l'étape 1 : génère la grille, colore chaque cellule au hasard
/// et permet de tourner autour de la planète.
/// Souris : clic gauche + glisser pour tourner, molette pour zoomer.
/// </summary>
public partial class Main : Node3D
{
    [Export(PropertyHint.Range, "0,8")]
    public int Subdivisions { get; set; } = 5;

    [Export]
    public int Seed { get; set; } = 1;

    private const float RotationSpeed = 0.005f;
    private const float MinDistance = 1.2f;
    private const float MaxDistance = 10f;

    private Node3D _cameraPivot;
    private Camera3D _camera;
    private float _yaw;
    private float _pitch;
    private float _distance = 3f;

    public override void _Ready()
    {
        var stopwatch = Stopwatch.StartNew();
        var grid = PlanetGrid.Create(Subdivisions);
        long gridMs = stopwatch.ElapsedMilliseconds;

        // Vérification : le nombre de cellules doit suivre la formule 10 × 4^n + 2
        int expected = PlanetGrid.ExpectedCellCount(Subdivisions);
        if (grid.CellCount != expected)
            GD.PushError($"Unexpected cell count: {grid.CellCount}, expected {expected}.");

        // Couleur aléatoire par cellule, pour voir la grille
        var rng = new RandomNumberGenerator { Seed = (ulong)Seed };
        var colors = new Color[grid.CellCount];
        for (int cell = 0; cell < grid.CellCount; cell++)
            colors[cell] = Color.FromHsv(rng.Randf(), 0.55f, 0.9f);

        stopwatch.Restart();
        var planet = new MeshInstance3D
        {
            Name = "Planet",
            Mesh = PlanetMeshBuilder.Build(grid, colors),
            MaterialOverride = PlanetMeshBuilder.CreateCellMaterial(),
        };
        AddChild(planet);
        long meshMs = stopwatch.ElapsedMilliseconds;

        GD.Print($"Grid: {grid.CellCount} cells, {grid.TriangleCount} triangles " +
                 $"(subdivisions {Subdivisions}) in {gridMs} ms, mesh in {meshMs} ms");

        SetupEnvironment();
        SetupCamera();
    }

    private void SetupEnvironment()
    {
        // Soleil provisoire ; le vrai ensoleillement viendra à l'étape 3
        var sun = new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-30f, 40f, 0f) };
        AddChild(sun);

        // Fond noir et lumière ambiante faible pour distinguer la face nocturne
        var environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.25f,
        };
        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = environment });
    }

    private void SetupCamera()
    {
        _cameraPivot = new Node3D { Name = "CameraPivot" };
        AddChild(_cameraPivot);
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, _distance) };
        _cameraPivot.AddChild(_camera);
        _camera.MakeCurrent();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Left) != 0:
                _yaw -= motion.Relative.X * RotationSpeed;
                // On bloque l'inclinaison juste avant les pôles pour ne pas retourner la caméra
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * RotationSpeed, -1.5f, 1.5f);
                _cameraPivot.Rotation = new Vector3(_pitch, _yaw, 0f);
                break;

            case InputEventMouseButton { Pressed: true } button:
                if (button.ButtonIndex == MouseButton.WheelUp)
                    _distance = Mathf.Max(MinDistance, _distance * 0.9f);
                else if (button.ButtonIndex == MouseButton.WheelDown)
                    _distance = Mathf.Min(MaxDistance, _distance * 1.1f);
                _camera.Position = new Vector3(0f, 0f, _distance);
                break;
        }
    }
}
