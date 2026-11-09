using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Planet.Rendering;

/// <summary>
/// Écrit une vidéo AVI « Motion JPEG » : chaque image est un JPEG complet, rangé dans le
/// conteneur AVI de Microsoft (RIFF). Format simple, lu par VLC, mpv, les navigateurs via
/// conversion, et les logiciels de montage ; pas de dépendance externe.
/// <para>Structure : RIFF 'AVI ' { LIST 'hdrl' { avih, LIST 'strl' { strh, strf } },
/// LIST 'movi' { '00dc' image… }, idx1 }. Les tailles et le nombre d'images sont réécrits à la fin.</para>
/// </summary>
public sealed class AviWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;
    private readonly int _width, _height, _fps;
    private readonly List<(long offset, int size)> _index = new();
    private long _riffSizePosition, _moviSizePosition, _moviStart;
    private long _totalFramesPosition, _lengthPosition, _maxBytesPosition;
    private int _maxFrameBytes;
    private bool _closed;

    public int FrameCount => _index.Count;

    public AviWriter(Stream stream, int width, int height, int fps)
    {
        _stream = stream;
        _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        _width = width;
        _height = height;
        _fps = fps;
        WriteHeader();
    }

    private void FourCc(string code) => _writer.Write(Encoding.ASCII.GetBytes(code));

    private void WriteHeader()
    {
        FourCc("RIFF");
        _riffSizePosition = _stream.Position;
        _writer.Write(0);
        FourCc("AVI ");

        FourCc("LIST");
        long hdrlSize = _stream.Position;
        _writer.Write(0);
        FourCc("hdrl");

        // En-tête principal (avih, 56 octets)
        FourCc("avih");
        _writer.Write(56);
        _writer.Write(1_000_000 / _fps);       // microsecondes par image
        _maxBytesPosition = _stream.Position;
        _writer.Write(0);                      // débit maximal (réécrit)
        _writer.Write(0);                      // granularité
        _writer.Write(0x10);                   // AVIF_HASINDEX
        _totalFramesPosition = _stream.Position;
        _writer.Write(0);                      // nombre d'images (réécrit)
        _writer.Write(0);                      // images initiales
        _writer.Write(1);                      // nombre de flux
        _writer.Write(0);                      // taille de tampon conseillée
        _writer.Write(_width);
        _writer.Write(_height);
        _writer.Write(0); _writer.Write(0); _writer.Write(0); _writer.Write(0);

        FourCc("LIST");
        long strlSize = _stream.Position;
        _writer.Write(0);
        FourCc("strl");

        // En-tête du flux vidéo (strh, 56 octets)
        FourCc("strh");
        _writer.Write(56);
        FourCc("vids");
        FourCc("MJPG");
        _writer.Write(0);                      // drapeaux
        _writer.Write((short)0);               // priorité
        _writer.Write((short)0);               // langue
        _writer.Write(0);                      // images initiales
        _writer.Write(1);                      // échelle
        _writer.Write(_fps);                   // cadence = cadence / échelle
        _writer.Write(0);                      // début
        _lengthPosition = _stream.Position;
        _writer.Write(0);                      // durée en images (réécrite)
        _writer.Write(0);                      // tampon conseillé
        _writer.Write(-1);                     // qualité
        _writer.Write(0);                      // taille d'échantillon
        _writer.Write((short)0); _writer.Write((short)0);
        _writer.Write((short)_width); _writer.Write((short)_height);

        // Format (strf : BITMAPINFOHEADER, 40 octets)
        FourCc("strf");
        _writer.Write(40);
        _writer.Write(40);
        _writer.Write(_width);
        _writer.Write(_height);
        _writer.Write((short)1);
        _writer.Write((short)24);
        FourCc("MJPG");
        _writer.Write(_width * _height * 3);
        _writer.Write(0); _writer.Write(0); _writer.Write(0); _writer.Write(0);

        PatchSize(strlSize);
        PatchSize(hdrlSize);

        FourCc("LIST");
        _moviSizePosition = _stream.Position;
        _writer.Write(0);
        _moviStart = _stream.Position;
        FourCc("movi");
    }

    /// <summary>Ajoute une image (fichier JPEG complet).</summary>
    public void AddFrame(byte[] jpeg)
    {
        if (_closed)
            throw new InvalidOperationException("vidéo déjà terminée");
        _index.Add((_stream.Position - _moviStart, jpeg.Length));
        FourCc("00dc");
        _writer.Write(jpeg.Length);
        _writer.Write(jpeg);
        if ((jpeg.Length & 1) == 1)
            _writer.Write((byte)0);   // les blocs RIFF ont une taille paire
        _maxFrameBytes = Math.Max(_maxFrameBytes, jpeg.Length);
    }

    /// <summary>Termine le fichier : index, tailles, nombre d'images.</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        PatchSize(_moviSizePosition);

        FourCc("idx1");
        _writer.Write(_index.Count * 16);
        foreach (var (offset, size) in _index)
        {
            FourCc("00dc");
            _writer.Write(0x10);               // AVIIF_KEYFRAME : chaque JPEG est une image clé
            _writer.Write((int)offset);
            _writer.Write(size);
        }

        long end = _stream.Position;
        _stream.Position = _riffSizePosition;
        _writer.Write((int)(end - _riffSizePosition - 4));
        _stream.Position = _totalFramesPosition;
        _writer.Write(_index.Count);
        _stream.Position = _lengthPosition;
        _writer.Write(_index.Count);
        _stream.Position = _maxBytesPosition;
        _writer.Write(_maxFrameBytes * _fps);
        _stream.Position = end;
        _writer.Flush();
    }

    /// <summary>Écrit la taille d'un bloc dont l'en-tête de taille est à sizePosition.</summary>
    private void PatchSize(long sizePosition)
    {
        long end = _stream.Position;
        _stream.Position = sizePosition;
        _writer.Write((int)(end - sizePosition - 4));
        _stream.Position = end;
    }

    public void Dispose()
    {
        Close();
        _writer.Dispose();
    }
}
