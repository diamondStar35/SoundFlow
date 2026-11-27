namespace SoundFlow.Codecs.FFMpeg;

/// <summary>Connection and probing limits for an audio URL. TLS peer verification is always enabled.</summary>
public sealed record NetworkSettings
{
    /// <summary>Maximum native network I/O wait before an error. Defaults to ten seconds.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum bytes used to identify the source format; defaults to 64 KiB.</summary>
    public int ProbeBytes { get; init; } = 65536;
    /// <summary>Media duration to analyze, not a wall-clock deadline. Defaults to two seconds; zero uses FFmpeg's automatic limit.</summary>
    public TimeSpan AnalyzeDuration { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>HTTP User-Agent sent to the endpoint.</summary>
    public string UserAgent { get; init; } = "SoundFlow";
    /// <summary>Additional HTTP headers, copied when opening the source.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>Optional PEM root bundle for OpenSSL builds on Linux/Android. Windows and Apple use platform trust.</summary>
    public string? CertificateAuthorityFile { get; init; }
    /// <summary>Uses interleaved TCP for RTSP media by default; false requests UDP.</summary>
    public bool RtspOverTcp { get; init; } = true;

    internal void Validate()
    {
        if (ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
        if (ProbeBytes is < 2048 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(ProbeBytes));
        if (AnalyzeDuration < TimeSpan.Zero || AnalyzeDuration > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(AnalyzeDuration));
        if (string.IsNullOrWhiteSpace(UserAgent) || UserAgent.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("User-Agent must be a single nonempty line.");
        if (CertificateAuthorityFile != null && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()))
            throw new NotSupportedException("This build uses platform certificate trust, rather than an OpenSSL PEM bundle.");
        if (CertificateAuthorityFile != null && !File.Exists(CertificateAuthorityFile))
            throw new FileNotFoundException("Certificate authority bundle was not found.", CertificateAuthorityFile);
        _ = HeaderText();
    }

    internal string? HeaderText()
    {
        if (Headers.Count == 0)
            return null;
        return string.Concat(Headers.Select(pair =>
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                pair.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("HTTP headers must contain a valid name and a single-line value.");
            return pair.Key + ": " + pair.Value + "\r\n";
        }));
    }
}
