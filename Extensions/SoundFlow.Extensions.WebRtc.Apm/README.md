<div align="center">
    <img src="https://raw.githubusercontent.com/LSXPrime/SoundFlow/refs/heads/master/logo.png" alt="Project Logo" width="256" height="256">

# SoundFlow - Audio Processing Module Extension (WebRTC)

**WebRTC Audio Processing Module (Google & PulseAudio) Integration for SoundFlow**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT) [![NuGet](https://img.shields.io/nuget/v/SoundFlow.Extensions.WebRtc.Apm.svg)](https://www.nuget.org/packages/SoundFlow.Extensions.WebRtc.Apm)
[![SoundFlow Main Repository](https://img.shields.io/badge/SoundFlow%20Core-Repo-blue)](https://github.com/LSXPrime/SoundFlow)

</div>

## Introduction

`SoundFlow.Extensions.WebRtc.Apm` is an official extension package for the [SoundFlow (.NET) audio engine](https://github.com/LSXPrime/SoundFlow). It integrates a native library based on the high-quality **WebRTC Audio Processing Module (APM)**, bringing advanced voice processing capabilities to your SoundFlow audio pipelines.

The WebRTC APM is widely used in real-time communication applications (like WebRTC itself) to enhance audio quality by addressing common issues such as echo, background noise, and varying audio levels.

## Features

This extension provides the following WebRTC APM features, configurable and applicable within your SoundFlow projects:

*   **Acoustic Echo Cancellation (AEC):** Effectively cancels echoes that occur when audio played through speakers is picked up by the microphone. Essential for speakerphone or conference call scenarios.
*   **Noise Suppression (NS):** Reduces unwanted steady-state background noise (e.g., fan noise, hum) from the audio signal, improving clarity.
*   **Automatic Gain Control (AGC):** Dynamically adjusts the microphone input volume to ensure a consistent and appropriate level, preventing audio that is too quiet or clipped.
*   **High Pass Filter (HPF):** Removes low-frequency components below ~80 Hz, helping to eliminate DC offset, rumble, and plosives.
*   **Pre-Amplifier:** Applies a configurable gain to the audio signal before other APM processing steps.

These features are primarily applied via a single, unified `WebRtcApmModifier` component designed for real-time audio graph processing. The package also includes a `NoiseSuppressor` component for convenient offline/batch processing of `SoundFlow.Providers.IDataProvider` sources.

## Getting Started

### Installation

This package requires SoundFlow (the core library). You can install it via NuGet:

**NuGet Package Manager:**

```bash
Install-Package SoundFlow.Extensions.WebRtc.Apm
```

**.NET CLI:**

```bash
dotnet add package SoundFlow.Extensions.WebRtc.Apm
```

### Usage

Reference an explicit backend such as SoundFlow.Backends.MiniAudio. The [noise-suppression sample](../../Samples/SoundFlow.Samples.NoiseSuppression/Program.cs) demonstrates file playback, live microphone processing and offline cleaning using the current device interface.

```csharp
using MiniAudio = SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Extensions.WebRtc.Apm.Modifiers;
using SoundFlow.Providers;
using SoundFlow.Structs;

using var engine = new MiniAudio.Engine();
using var output = engine.InitializePlaybackDevice(null, AudioFormat.DvdHq);
using var player = new SoundPlayer(engine, output.Format,
    new StreamDataProvider(engine, output.Format, File.OpenRead("recording.wav")));
using var processing = new WebRtcApmModifier(output, nsEnabled: true);
player.AddModifier(processing);
output.MasterMixer.AddComponent(player);
player.Play(); output.Start();
Console.ReadLine();
output.Stop();
```

AEC requires a genuine final playback reference. Inspect processing stages before connecting it: physical OpenAL exposes its software graph feed, not the final spatial/effects mix. A graph-only reference must not be presented as complete device output. Capture and playback clocks may differ. See [processing stages](../../Docs/Architecture.md) and the sample's explicit capture/device wiring.

## Sample

A sample application demonstrating the usage of the `NoiseSuppressor` component for offline audio file processing can be found here:

[SoundFlow.Samples.NoiseSuppression](https://github.com/LSXPrime/SoundFlow/blob/master/SoundFlow.Samples.NoiseSuppression/) *(Note: This link assumes the sample is in the main SoundFlow repository)*

## Origin and Licensing

This `SoundFlow.Extensions.WebRtc.Apm` package consists of C# code (wrapper, integration logic, modifiers, components) and relies on a separate native binary derived from the **WebRTC Audio Processing Module**.

*   The C# code within this `SoundFlow.Extensions.WebRtc.Apm` package is licensed under the **MIT License**.
*   The native `webrtc_audio_processing` library wrapped by this package is based on the efforts of the [PulseAudio project](https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing) to extract the APM from the full WebRTC source tree into a standalone, buildable library.
*   The underlying WebRTC Audio Processing Module code is typically licensed under the **BSD 3-Clause "New" or "Revised" License**.

**Users of this package must comply with the terms of BOTH the MIT License (for the C# wrapper) and the BSD 3-Clause License (for the native library).** The BSD license generally requires including the copyright notice and license text of the WebRTC code. Please consult the native library's specific distribution for the exact licensing requirements.

## Contributing

Contributions to the `SoundFlow.Extensions.WebRtc.Apm` package are welcome! Please open issues or submit pull requests to this repository following the general [SoundFlow Contributing Guidelines](https://github.com/LSXPrime/SoundFlow#contributing).

## Acknowledgments

We gratefully acknowledge the work of the original **WebRTC team** for developing the powerful Audio Processing Module, and the **PulseAudio project** for their valuable effort in extracting and maintaining a standalone version of the APM library, which makes this integration possible.

## License

The C# code in `SoundFlow.Extensions.WebRtc.Apm` is licensed under the [MIT License](../../LICENSE.md).
