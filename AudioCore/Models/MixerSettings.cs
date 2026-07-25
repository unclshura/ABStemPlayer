using NAudio.Mixer;

namespace AudioCore.Models;

public sealed class StemMixSettings
{
    public bool Enabled { get; init; } = true;
    public float GainDb { get; init; } = 0f;
    public float Pan { get; init; } = 0f; // -1..+1

    public override string ToString() => $"{(Enabled ? "[x]": "[ ]")} {GainDb:N2} {Pan:N2}";
}

public sealed class MixerSettings
{
    public required IReadOnlyList<StemMixSettings> Stems { get; init; }

    public override string ToString() => $"Mixer: {Stems.Count}";

}

