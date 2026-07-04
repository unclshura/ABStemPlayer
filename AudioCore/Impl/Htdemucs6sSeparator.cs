using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace AudioCore.Impl;

public sealed class Htdemucs6sSeparator : IStemSeparator
{
    private const int _sampleRate        = 44100;
    private const int _channels          = 2;
    private const double _segmentSeconds = 7.8;
    private const int _segmentSamples    = (int)(_sampleRate * _segmentSeconds); // 343,980
    private const int _overlap           = _segmentSamples / 4;                  // 85,995
    private const int _stride            = _segmentSamples - _overlap;           // 257,985

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

        // 1. Load audio
        var mix = LoadStereoFloatWave(request.SourceFilePath, out var sr);
        if (sr != _sampleRate)
            throw new InvalidOperationException($"Input must be {_sampleRate} Hz");

        var totalSamples = mix.GetLength(1);

        // 2. Prepare ONNX session 
        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CPU();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Data", "htdemucs_6s.onnx");
        using var session = new InferenceSession(modelPath, opts);

        // 3. Prepare buffers
        var outStems = new float[_stemNames.Length, _channels, totalSamples];
        var weight   = new float[totalSamples];
        var window   = MakeWindow(_segmentSamples, _overlap);

        var nChunks = Math.Max(1, (totalSamples + _stride - 1) / _stride);

        // 4. Sliding window inference
        for (var i = 0; i < nChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            var start = i * _stride;
            var end = Math.Min(start + _segmentSamples, totalSamples);
            var clen = end - start;

            // Extract chunk into [2, N]
            var chunk = new float[_channels, _segmentSamples];
            for (var ch = 0; ch < _channels; ch++)
            {
                Array.Copy(mix, ch * totalSamples + start, chunk, ch * _segmentSamples, clen);
            }

            // Build flat input buffer (1,2,N)
            var inputData = new float[_channels * _segmentSamples];
            for (var ch = 0; ch < _channels; ch++)
            {
                var baseIndex = ch * _segmentSamples;
                for (var s = 0; s < _segmentSamples; s++)
                    inputData[baseIndex + s] = chunk[ch, s];
            }

            // Create OrtValue for input
            using var inputOrtValue = OrtValue.CreateTensorValueFromMemory(
    inputData,
    new long[] { 1, _channels, _segmentSamples }
);

            // Prepare output buffer (CPU)
            var outputData = new float[_stemNames.Length * _channels * _segmentSamples];

            // Create OrtValue for output
            using var outputOrtValue = OrtValue.CreateTensorValueFromMemory(
    outputData,
    new long[] { 1, _stemNames.Length, _channels, _segmentSamples }
);

            // Bind using IOBinding
            using var io = session.CreateIoBinding();
            io.BindInput("mix", inputOrtValue);
            io.BindOutput("stems", outputOrtValue);

            // Execute on GPU → output goes directly to CPU buffer
            session.RunWithBinding(new RunOptions(), io);

            // Now outputData contains (1,6,2,N)
            var buf = outputData.AsSpan();

            // Extract output tensor shape (1, 6, 2, N)
            var stemCnt  = _stemNames.Length;   // 6
            var chCnt    = _channels;           // 2
            var length   = _segmentSamples;     // 343980

            // Compute strides for flattened buffer
            var stemStride    = chCnt * length; // 2 * N
            var channelStride = length;         // N

            // Overlap-add
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

        // 5. Normalize by weight
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

        // 6. Write stems
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

    private StemSet? CheckExistingStems(StemSeparationRequest request)
    {
        var filesToCheck = Enum.GetNames(typeof(StemType))
            .Select(stemType => (stemType, Path.Combine(request.OutputDirectory, $"{Path.GetFileNameWithoutExtension(request.SourceFilePath)}_{stemType}.flac")))
            .ToList();

        var stems = new List<StemTrack>();
        var set = new StemSet
        {
            OriginalFilePath = request.SourceFilePath,
            Stems = stems
        };

        foreach (var f in filesToCheck.Where(f => File.Exists(f.Item2)))
        {
            using var reader = new AudioFileReader(f.Item2);

            var stem =new StemTrack
            {
                Type       = Enum.Parse<StemType>(f.Item1),
                Name       = Path.GetFileName(f.Item2),
                FilePath   = f.Item2,
                SampleRate = reader.WaveFormat.SampleRate,
                Channels   = reader.WaveFormat.Channels,
                Duration   = reader.TotalTime
            };

            stems.Add(stem);
        }

        return stems.Count > 0 ? set : null;
    }

    // ------------------------------
    // Helpers
    // ------------------------------

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

    private static float[,] LoadStereoFloatWave(string path, out int sampleRate)
    {
        using var reader = new AudioFileReader(path);
        sampleRate = reader.WaveFormat.SampleRate;

        var samples = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * 4];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));

        var total = samples.Count / 2;
        var result = new float[2, total];

        for (var i = 0; i < total; i++)
        {
            result[0, i] = samples[2 * i];
            result[1, i] = samples[2 * i + 1];
        }

        return result;
    }

    //private static void WriteWave(string path, float[,,] stems, int stemIndex, int totalSamples)
    //{
    //    var format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);

    //    using var writer = new WaveFileWriter(path, format);

    //    for (int i = 0; i < totalSamples; i++)
    //    {
    //        writer.WriteSample(stems[stemIndex, 0, i]);
    //        writer.WriteSample(stems[stemIndex, 1, i]);
    //    }
    //}

    private static void WriteFlac(string path, float[,,] stems, int stemIndex, int totalSamples)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
            "-y " +
            "-f f32le " +                 // raw float32 little-endian
            "-ar 44100 " +                // sample rate
            "-ac 2 " +                    // channels
            "-i pipe:0 " +                // read from stdin
            "-compression_level 12 " +    // max FLAC compression
            $"\"{path}\"",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var ff = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg process");
        using var stdin = ff.StandardInput.BaseStream;

        // Write raw float32 PCM directly to FFmpeg
        var buffer = new byte[sizeof(float) * 2]; // stereo frame

        for (var i = 0; i < totalSamples; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), stems[stemIndex, 0, i]);
            BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), stems[stemIndex, 1, i]);
            stdin.Write(buffer, 0, buffer.Length);
        }

        stdin.Flush();
        stdin.Close();

        ff.WaitForExit();
    }



}
