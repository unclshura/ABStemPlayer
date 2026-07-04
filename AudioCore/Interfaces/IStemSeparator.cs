namespace AudioCore.Interfaces;

public interface IStemSeparator
{
    Task<StemSet> SeparateAsync(
        StemSeparationRequest request,
        IProgressReporter<double> progress,
        CancellationToken ct = default);
}
