using AudioCore.Interfaces;
using AudioCore.Impl;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class Htdemucs6sSeparator_Tests
{
    private string _inputPath = null!;
    private string _outputDir = null!;

    [TestInitialize]
    public void Init()
    {
        var baseDir = AppContext.BaseDirectory;

        var modelPath = Path.Combine(baseDir, "Data", "htdemucs_6s.onnx");
        _inputPath = Path.Combine(baseDir, "Data", "test_input.mp3");

        Assert.IsTrue(File.Exists(modelPath), "Model file missing");
        Assert.IsTrue(File.Exists(_inputPath), "Test input audio missing");

        _outputDir = Path.Combine(baseDir, "TestOutput", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_outputDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_outputDir) && Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, recursive: true);
        }
        catch
        {
            // Swallow exceptions so cleanup never breaks the test runner
        }
    }

    [TestMethod]
    public async Task Htdemucs6sSeparator_Separates_6_Stems()
    {
        var separator = new Htdemucs6sSeparator();

        var request = new StemSeparationRequest
        {
            SourceFilePath = _inputPath,
            OutputDirectory = _outputDir
        };

        var progress = new TestProgressReporter();

        var stemSet = await separator.SeparateAsync(request, progress);

        Assert.HasCount(6, stemSet.Stems, "Expected 6 stems");

        foreach (var stem in stemSet.Stems)
        {
            Assert.IsTrue(File.Exists(stem.FilePath), $"Missing stem file: {stem.FilePath}");
            Assert.AreEqual(44100, stem.SampleRate);
            Assert.AreEqual(2, stem.Channels);
            Assert.IsGreaterThan(0.1, stem.Duration.TotalSeconds, "Stem duration too small");
        }

        Assert.IsGreaterThan(0, progress.Calls, "Progress reporter was never called");
    }

    private sealed class TestProgressReporter : IProgressReporter<double>
    {
        public int Calls { get; private set; }

        public Task ReportProgress(double progress, CancellationToken ct = default)
        {
            Calls++;
            Assert.IsTrue(progress >= 0.0 && progress <= 1.0);
            return Task.CompletedTask;
        }
    }
}
