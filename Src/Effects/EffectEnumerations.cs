namespace SoundFlow.Effects;

/// <summary>Modulation waveform for chorus and flanger effects.</summary>
public enum ModulationWaveform
{
    /// <summary>Sinusoidal modulation.</summary>
    Sinusoid,
    /// <summary>Triangle modulation.</summary>
    Triangle
}

/// <summary>Direction of a frequency shift on an output channel.</summary>
public enum FrequencyShiftDirection
{
    /// <summary>Shift down.</summary>
    Down,
    /// <summary>Shift up.</summary>
    Up,
    /// <summary>Disable the shift on this channel.</summary>
    Off
}

/// <summary>Waveform controlling the vocal morpher's transition between phonemes.</summary>
public enum VocalMorpherWaveform
{
    /// <summary>Sinusoidal modulation.</summary>
    Sinusoid,
    /// <summary>Triangle modulation.</summary>
    Triangle,
    /// <summary>Sawtooth modulation.</summary>
    Sawtooth
}

/// <summary>Waveform used by the ring modulator.</summary>
public enum RingModulatorWaveform
{
    /// <summary>Sinusoidal carrier.</summary>
    Sinusoid,
    /// <summary>Sawtooth carrier.</summary>
    Sawtooth,
    /// <summary>Square carrier.</summary>
    Square
}

/// <summary>EFX vocal-morpher phonemes in their original numeric order.</summary>
public enum VocalPhoneme
{
    /// <summary>A vowel.</summary>
    A,
    /// <summary>E vowel.</summary>
    E,
    /// <summary>I vowel.</summary>
    I,
    /// <summary>O vowel.</summary>
    O,
    /// <summary>U vowel.</summary>
    U,
    /// <summary>AA phoneme.</summary>
    AA,
    /// <summary>AE phoneme.</summary>
    AE,
    /// <summary>AH phoneme.</summary>
    AH,
    /// <summary>AO phoneme.</summary>
    AO,
    /// <summary>EH phoneme.</summary>
    EH,
    /// <summary>ER phoneme.</summary>
    ER,
    /// <summary>IH phoneme.</summary>
    IH,
    /// <summary>IY phoneme.</summary>
    IY,
    /// <summary>UH phoneme.</summary>
    UH,
    /// <summary>UW phoneme.</summary>
    UW,
    /// <summary>B consonant.</summary>
    B,
    /// <summary>D consonant.</summary>
    D,
    /// <summary>F consonant.</summary>
    F,
    /// <summary>G consonant.</summary>
    G,
    /// <summary>J consonant.</summary>
    J,
    /// <summary>K consonant.</summary>
    K,
    /// <summary>L consonant.</summary>
    L,
    /// <summary>M consonant.</summary>
    M,
    /// <summary>N consonant.</summary>
    N,
    /// <summary>P consonant.</summary>
    P,
    /// <summary>R consonant.</summary>
    R,
    /// <summary>S consonant.</summary>
    S,
    /// <summary>T consonant.</summary>
    T,
    /// <summary>V consonant.</summary>
    V,
    /// <summary>Z consonant.</summary>
    Z
}
