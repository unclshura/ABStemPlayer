namespace AudioCore.Interfaces;

public interface IAudioMixer
{
    MixedAudioBlock Mix(
        IReadOnlyList<TimeStretchedAudioBlock> stemBlocks,
        MixerSettings settings);
}
