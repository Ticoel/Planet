using Godot;
using Planet.Simulation.Audio;

namespace Planet.Rendering;

/// <summary>
/// Joue les sons synthétisés de l'environnement (voir <see cref="Soundscape"/>) : à chaque image,
/// on calcule juste assez d'échantillons pour remplir le tampon du générateur audio de Godot.
/// </summary>
public sealed partial class SoundscapePlayer : Node
{
    private const int MixRate = 22050;
    private readonly Soundscape _soundscape = new(MixRate, 7);
    private AudioStreamPlayer _player;
    private AudioStreamGeneratorPlayback _playback;
    private float[] _left = new float[1024], _right = new float[1024];
    private Vector2[] _frames = new Vector2[1024];

    /// <summary>Ce que la caméra entend en ce moment (mis à jour par Main).</summary>
    public SoundscapeInput Input { get; set; }

    /// <summary>Volume général (0 à 1) ; touche de coupure du son dans Main.</summary>
    public float Volume { get; set; } = 0.8f;

    public override void _Ready()
    {
        var stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.2f };
        _player = new AudioStreamPlayer { Stream = stream, Name = "Ambience" };
        AddChild(_player);
        _player.Play();
        _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
    }

    public override void _Process(double delta)
    {
        if (_playback == null)
            return;
        int available = _playback.GetFramesAvailable();
        while (available > 0)
        {
            int count = System.Math.Min(available, _left.Length);
            if (count != _frames.Length)
            {
                // Dernier morceau plus court : tampons à la bonne taille (PushBuffer prend tout)
                _left = new float[count];
                _right = new float[count];
                _frames = new Vector2[count];
            }
            _soundscape.Render(Input, _left, _right);
            for (int i = 0; i < count; i++)
                _frames[i] = new Vector2(_left[i], _right[i]) * Volume;
            _playback.PushBuffer(_frames);
            available -= count;
            if (count < 1024)
            {
                _left = new float[1024];
                _right = new float[1024];
                _frames = new Vector2[1024];
            }
        }
    }
}
