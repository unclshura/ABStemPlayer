namespace AudioCore.Interfaces;

public interface IProgressReporter<T>
{
    Task ReportProgress(T progress, CancellationToken ct = default);
}
