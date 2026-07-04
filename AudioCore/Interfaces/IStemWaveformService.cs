namespace AudioCore.Interfaces;

public interface IStemWaveformService
{
    /// <summary>
    /// Computes N averaged amplitude values for the given stem.
    /// </summary>
    /// <param name="decoder">Configured stem decoder.</param>
    /// <param name="segments">Number of averages to produce.</param>
    /// <returns>Float array of length N.</returns>
    Task<float[]> ComputeWaveformAsync(StemTrack stem, IStemDecoder decoder, int segments = 200);
}

public interface IStemWaveformServiceFactory
{
    IStemWaveformService Create(StemTrack stem);
}