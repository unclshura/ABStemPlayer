namespace AudioCore.Models;

public sealed class StemMixSettings
{
    public bool Enabled { get; init; } = true;
    public float GainDb { get; init; } = 0f;
    public float Pan { get; init; } = 0f; // -1..+1
}

public sealed class MixerSettings
{
    public required IReadOnlyList<StemMixSettings> Stems { get; init; }
}

