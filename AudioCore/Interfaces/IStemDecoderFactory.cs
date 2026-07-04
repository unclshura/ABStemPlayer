namespace AudioCore.Interfaces;

public interface IStemDecoderFactory
{
    IStemDecoder Create(StemTrack stem);
}
