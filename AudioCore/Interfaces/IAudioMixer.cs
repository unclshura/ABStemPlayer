namespace AudioCore.Interfaces;

public interface IAudioMixer
{
    MixedAudioBlock Mix(
        IReadOnlyList<AudioBlock> stemBlocks,
        MixerSettings settings);
}
