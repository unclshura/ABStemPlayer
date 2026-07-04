namespace AudioCore.Interfaces;

public interface IAudioReaderFactory
{
    IAudioReader Create(string filePath);
}
