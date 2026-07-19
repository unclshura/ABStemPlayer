using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace AudioCore.Impl;

public sealed class Htdemucs6sSeparator : IStemSeparator
{
    private const int _sampleRate        = 44100;
    private const int _channels          = 2;
    private const double _segmentSeconds = 7.8;
    private const int _segmentSamples    = (int)(_sampleRate * _segmentSeconds);
    private const int _overlap           = _segmentSamples / 4;
    private const int _stride            = _segmentSamples - _overlap;

    private static readonly string[] _stemNames = Enum.GetNames<StemType>();

    public async Task<StemSet> SeparateAsync(
        StemSeparationRequest request,
        IProgressReporter<double> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(request.OutputDirectory);

        var existingStems = CheckExistingStems(request);
        if (existingStems != null)
            return existingStems;

        var probe = FfprobeProcess.ProbeAudio(request.SourceFilePath);

        int totalSamples = (int)(probe.Duration.TotalSeconds * _sampleRate);

        // Decode whole file to stereo float array via ffmpeg, resampled to 44.1kHz.
        var mix = LoadStereoFloatWave(request.SourceFilePath, totalSamples);

        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CPU();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Data", "htdemucs_6s.onnx");
        using var session = new InferenceSession(modelPath, opts);

        var outStems = new float[_stemNames.Length, _channels, totalSamples];
        var weight   = new float[totalSamples];
        var window   = MakeWindow(_segmentSamples, _overlap);

        var nChunks = Math.Max(1, (totalSamples + _stride - 1) / _stride);

        for (var i = 0; i < nChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            var start = i * _stride;
            var end   = Math.Min(start + _segmentSamples, totalSamples);
            var clen  = end - start;

            var chunk = new float[_channels, _segmentSamples];
            for (var ch = 0; ch < _channels; ch++)
                Array.Copy(mix, ch * totalSamples + start, chunk, ch * _segmentSamples, clen);

            var inputData = new float[_channels * _segmentSamples];
            for (var ch = 0; ch < _channels; ch++)
            {
                var baseIndex = ch * _segmentSamples;
                for (var s = 0; s < _segmentSamples; s++)
                    inputData[baseIndex + s] = chunk[ch, s];
            }

            using var inputOrtValue = OrtValue.CreateTensorValueFromMemory(
                inputData,
                new long[] { 1, _channels, _segmentSamples });

            var outputData = new float[_stemNames.Length * _channels * _segmentSamples];

            using var outputOrtValue = OrtValue.CreateTensorValueFromMemory(
                outputData,
                new long[] { 1, _stemNames.Length, _channels, _segmentSamples });

            using var io = session.CreateIoBinding();
            io.BindInput("mix", inputOrtValue);
            io.BindOutput("stems", outputOrtValue);

            session.RunWithBinding(new RunOptions(), io);

            var buf = outputData.AsSpan();

            var stemCnt  = _stemNames.Length;
            var chCnt    = _channels;
            var length   = _segmentSamples;

            var stemStride    = chCnt * length;
            var channelStride = length;

            for (var stem = 0; stem < stemCnt; stem++)
            {
                for (var ch = 0; ch < chCnt; ch++)
                {
                    var baseIndex = stem * stemStride + ch * channelStride;

                    for (var s = 0; s < clen; s++)
                    {
                        var w = window[s];
                        var v = buf[baseIndex + s];
                        outStems[stem, ch, start + s] += v * w;
                    }
                }
            }

            for (var s = 0; s < clen; s++)
                weight[start + s] += window[s];

            await progress.ReportProgress((double)(i + 1) / nChunks, ct);
        }

        for (var stem = 0; stem < _stemNames.Length; stem++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                for (var s = 0; s < totalSamples; s++)
                {
                    var w = weight[s];
                    if (w > 1e-8f)
                        outStems[stem, ch, s] /= w;
                }
            }
        }

        var result = new List<StemTrack>();
        for (var i = 0; i < _stemNames.Length; i++)
        {
            var name = $"{Path.GetFileNameWithoutExtension(request.SourceFilePath)}_{_stemNames[i]}.flac";
            var path = Path.Combine(request.OutputDirectory, name);

            WriteFlac(path, outStems, i, totalSamples);

            result.Add(new StemTrack
            {
                Type       = Enum.Parse<StemType>(_stemNames[i]),
                Name       = name,
                FilePath   = path,
                SampleRate = _sampleRate,
                Channels   = _channels,
                Duration   = TimeSpan.FromSeconds((double)totalSamples / _sampleRate)
            });
        }

        return new StemSet
        {
            OriginalFilePath = request.SourceFilePath,
            Stems = result
        };
    }

    private static float[,] LoadStereoFloatWave(string path, int totalSamples)
    {
        var result = new float[_channels, totalSamples];

        var cmd =
            "-hide_banner -loglevel error " +
            $"-i \"{path}\" " +
            "-map a:0 " +
            "-af aresample " +
            "-f f32le -ac 2 -ar 44100 pipe:1";

        using var ff = new FfmpegProcess(
            name: $"decode:{Path.GetFileName(path)}",
            commandLine: cmd,
            redirectOutput: true,
            redirectInput: false);

        ff.StartProcess();

        var buffer = new float[4096 * _channels];
        var pos    = 0;

        const float SCALE = 2f;

        while (true)
        {
            var readFloats = ff.ReadAsync(buffer.AsMemory(), CancellationToken.None).GetAwaiter().GetResult();
            if (readFloats <= 0)
                break;

            var framesRead = readFloats / _channels;
            for (var f = 0; f < framesRead && pos < totalSamples; f++, pos++)
            {
                var baseIndex = f * _channels;
                result[0, pos] = buffer[baseIndex + 0] * SCALE;
                result[1, pos] = buffer[baseIndex + 1] * SCALE;
            }

            if (pos >= totalSamples)
                break;
        }

        return result;
    }

    private static float[] MakeWindow(int n, int overlap)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = 1f;

        for (var i = 0; i < overlap; i++)
        {
            var fade = (float)i / overlap;
            w[i] = fade;
            w[n - 1 - i] = fade;
        }
        return w;
    }

    private static void WriteFlac(string path, float[,,] stems, int stemIndex, int totalSamples)
    {
        var cmd =
            "-y " +
            "-f f32le " +
            "-ar 44100 " +
            "-ac 2 " +
            "-i pipe:0 " +
            "-compression_level 12 " +
            $"\"{path}\"";

        using var ff = new FfmpegProcess(
            name: $"flac:{Path.GetFileName(path)}",
            commandLine: cmd,
            redirectOutput: true,
            redirectInput: true);

        ff.StartProcess();

        var stdin = ff.Stdin!;
        var frame = new byte[sizeof(float) * 2];

        for (var i = 0; i < totalSamples; i++)
        {
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), stems[stemIndex, 0, i]);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), stems[stemIndex, 1, i]);
            stdin.Write(frame, 0, frame.Length);
        }

        stdin.Flush();
        stdin.Close();

        ff.Proc!.WaitForExit();
    }

    private StemSet? CheckExistingStems(StemSeparationRequest request)
    {
        var filesToCheck = Enum.GetNames(typeof(StemType))
            .Select(stemType => (stemType, Path.Combine(request.OutputDirectory,
                $"{Path.GetFileNameWithoutExtension(request.SourceFilePath)}_{stemType}.flac")))
            .ToList();

        var stems = new List<StemTrack>();
        var set = new StemSet
        {
            OriginalFilePath = request.SourceFilePath,
            Stems            = stems
        };

        foreach (var f in filesToCheck.Where(f => File.Exists(f.Item2)))
        {
            using var reader = new AudioFileReader(f.Item2);

            stems.Add(new StemTrack
            {
                Type = Enum.Parse<StemType>(f.Item1),
                Name = Path.GetFileName(f.Item2),
                FilePath = f.Item2,
                SampleRate = reader.WaveFormat.SampleRate,
                Channels = reader.WaveFormat.Channels,
                Duration = reader.TotalTime
            });
        }

        return stems.Count > 0 ? set : null;
    }
}
