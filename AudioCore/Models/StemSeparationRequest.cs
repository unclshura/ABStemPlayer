namespace AudioCore.Models;

public class StemSeparationRequest
{
    public string SourceFilePath  { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
}
