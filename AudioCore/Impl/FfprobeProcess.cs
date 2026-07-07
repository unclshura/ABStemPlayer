using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioCore.Impl;

public class AudioProbe
{
    public int      SampleRate   { get; init; }
    public int      Channels     { get; init; }
    public long     TotalSamples { get; init; }
    public TimeSpan Duration     { get; init; }
}

public static class FfprobeProcess
{
    private sealed class FfprobeJson
    {
        public FfprobeFormat? Format { get; set; }
        public FfprobeStream[]? Streams { get; set; }
    }

    private sealed class FfprobeFormat
    {
        public string? Duration { get; set; }
    }

    private sealed class FfprobeStream
    {
        [JsonConverter(typeof(IntFlexibleConverter))]
        public int Sample_Rate { get; set; }
        [JsonConverter(typeof(IntFlexibleConverter))]
        public int Channels { get; set; }
    }

    private sealed class IntFlexibleConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetInt32(),
                JsonTokenType.String => int.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new JsonException($"Invalid token for int: {reader.TokenType}")
            };
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    public static AudioProbe ProbeAudio(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffprobe",
            Arguments              =
                "-v error " +
                "-select_streams a:0 " +
                "-show_entries format=duration " +
                "-show_entries stream=sample_rate,channels " +
                "-print_format json " +
                $"\"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe failed");
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var probe = JsonSerializer.Deserialize<FfprobeJson>(json, opts)
                    ?? throw new InvalidOperationException("Invalid ffprobe JSON");

        if (probe.Format?.Duration is null)
            throw new InvalidOperationException("ffprobe missing duration");

        if (probe.Streams is null || probe.Streams.Length == 0)
            throw new InvalidOperationException("ffprobe missing audio stream");

        var stream = probe.Streams[0];

        var durationSeconds = double.Parse(probe.Format.Duration,
            System.Globalization.CultureInfo.InvariantCulture);

        var sampleRate = stream.Sample_Rate;
        var channels   = stream.Channels;

        var totalSamples = (long)(durationSeconds * sampleRate);

        return new AudioProbe
        {
            SampleRate = sampleRate,
            Channels = channels,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            TotalSamples = totalSamples
        };
    }
}