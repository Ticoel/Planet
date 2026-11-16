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
    // Morceaux fixes (pas d'allocation à chaque image : PushBuffer prend un tableau entier)
    private const int Chunk = 512;
    private readonly float[] _left = new float[Chunk], _right = new float[Chunk];
    private readonly Vector2[] _frames = new Vector2[Chunk];

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
        // Juste ce que la carte son consommera d'ici deux images (au plus) : remplir tout le tampon
        // d'un coup coûtait cher après chaque saccade, et sans fin avec un pilote audio factice
        int budget = (int)(System.Math.Min(delta, 0.1) * 2 * MixRate) + Chunk;
        int available = _playback.GetFramesAvailable();
        while (available >= Chunk && budget > 0)
        {
            _soundscape.Render(Input, _left, _right);
            for (int i = 0; i < Chunk; i++)
                _frames[i] = new Vector2(_left[i], _right[i]) * Volume;
            _playback.PushBuffer(_frames);
            available -= Chunk;
            budget -= Chunk;
        }
    }
}
