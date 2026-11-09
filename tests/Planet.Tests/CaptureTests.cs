using System;
using System.IO;
using System.Text;
using Planet.Rendering;

namespace Planet.Tests;

/// <summary>Vidéos de la simulation : conteneur AVI « Motion JPEG ».</summary>
public static class CaptureTests
{
    // Image JPEG de 16 × 16 pixels (orange)
    private const string TinyJpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAQABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDHooor4w/ST//Z";

    [Test]
    public static void AviFileHasConsistentStructure()
    {
        byte[] jpeg = Convert.FromBase64String(TinyJpeg);
        var memory = new MemoryStream();
        using (var avi = new AviWriter(memory, 16, 16, 30))
        {
            for (int i = 0; i < 45; i++)
                avi.AddFrame(jpeg);
            Assert.Equal(45, avi.FrameCount, "images");
        }
        byte[] file = memory.ToArray();
        string Text(int offset) => Encoding.ASCII.GetString(file, offset, 4);
        int Int(int offset) => BitConverter.ToInt32(file, offset);
        Assert.Equal("RIFF", Text(0), "RIFF");
        Assert.Equal("AVI ", Text(8), "type AVI");
        Assert.Equal(file.Length - 8, Int(4), "taille RIFF");
        // avih : microsecondes par image puis nombre d'images
        int avih = Encoding.ASCII.GetString(file).IndexOf("avih", StringComparison.Ordinal);
        Assert.Equal(1_000_000 / 30, Int(avih + 8), "durée d'une image (µs)");
        Assert.Equal(45, Int(avih + 24), "nombre d'images");
        int idx = Encoding.ASCII.GetString(file).IndexOf("idx1", StringComparison.Ordinal);
        Assert.Equal(45 * 16, Int(idx + 4), "taille de l'index");
        Assert.True(Encoding.ASCII.GetString(file).Contains("MJPG"), "codec Motion JPEG");
        string path = Path.Combine(Path.GetTempPath(), "planet_test.avi");
        File.WriteAllBytes(path, file);
        Console.WriteLine($"     {file.Length} octets, écrit dans {path}");
    }
}
