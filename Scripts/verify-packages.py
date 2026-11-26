"""Inspect package boundaries and run a consumer using local SoundFlow packages and official .NET framework packs."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = {"android-arm", "android-x64", "linux-arm", "win-x86"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path)
    parser.add_argument("--rid", required=True)
    args = parser.parse_args()
    feed = args.packages.resolve()
    versions = {}
    for package in feed.glob("*.nupkg"):
        with zipfile.ZipFile(package) as archive:
            files = archive.namelist()
            nuspec = ET.fromstring(archive.read(next(name for name in files if name.endswith(".nuspec"))))
            metadata = next(node for node in nuspec if node.tag.endswith("metadata"))
            identity = next(node.text for node in metadata if node.tag.endswith("id"))
            version = next(node.text for node in metadata if node.tag.endswith("version"))
            versions[identity] = version
            rids = {name.split("/")[1] for name in files if name.startswith("runtimes/")}
            assert not rids.intersection(FORBIDDEN), (package, rids)
            if identity == "SoundFlow":
                assert not rids, "The core package must have no native runtime assets"
            if identity == "SoundFlow.Backends.OpenAL":
                assert not any("miniaudio" in name.lower() for name in files)
                dependencies = [node.attrib["id"] for node in nuspec.iter() if node.tag.endswith("dependency")]
                assert "SoundFlow.Codecs.FFMpeg" in dependencies and not any("MiniAudio" in name for name in dependencies)
            print(f"PASS {identity} {version}: {', '.join(sorted(rids)) or 'managed only'}")
    work = ROOT / "artifacts" / "package-consumers" / uuid.uuid4().hex
    work.mkdir(parents=True)
    # A private restore cache prevents an identically versioned upstream/global package from masking a broken local package.
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", {"key": "local", "value": str(feed)})
    ET.SubElement(sources, "add", {"key": "dotnet", "value": "https://api.nuget.org/v3/index.json"})
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", {"key": "local"})
    ET.SubElement(local, "package", {"pattern": "SoundFlow*"})
    dotnet = ET.SubElement(mapping, "packageSource", {"key": "dotnet"})
    ET.SubElement(dotnet, "package", {"pattern": "Microsoft.*"})
    ET.SubElement(dotnet, "package", {"pattern": "System.*"})
    ET.SubElement(dotnet, "package", {"pattern": "runtime.*"})
    ET.ElementTree(config).write(work / "NuGet.Config", encoding="utf-8", xml_declaration=True)
    project = ET.Element("Project", {"Sdk": "Microsoft.NET.Sdk"})
    props = ET.SubElement(project, "PropertyGroup")
    for key, value in {"OutputType": "Exe", "TargetFramework": "net8.0", "ImplicitUsings": "enable",
                       "Nullable": "enable", "RollForward": "Major", "RuntimeIdentifier": args.rid,
                       "SelfContained": "false", "RestorePackagesPath": str(work / "packages")}.items():
        ET.SubElement(props, key).text = value
    references = ET.SubElement(project, "ItemGroup")
    ET.SubElement(references, "PackageReference", {"Include": "SoundFlow.Backends.OpenAL", "Version": versions["SoundFlow.Backends.OpenAL"]})
    ET.ElementTree(project).write(work / "Consumer.csproj", encoding="utf-8", xml_declaration=True)
    (work / "Program.cs").write_text('''using SoundFlow.Backends.OpenAL;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Structs;

using var engine = new SoundFlow.Backends.OpenAL.Engine();
var mono = new AudioFormat { Format = SampleFormat.F32, Channels = 1, SampleRate = 48000 };
using var device = engine.CreateRenderDevice(mono with { Channels = 2 });
if (!device.TryGetFeature<SoundFlow.Effects.IEffects>(out var effects)) throw new Exception("No EFX");
using var slot = effects.CreateSlot(EfxReverbPresets.All["ROOM"]);
using var source = device.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Range(0, 4096).Select(i => .2f * MathF.Sin(i * .1f)).ToArray(), mono));
if (!source.TryGetFeature<SoundFlow.Effects.IEffectSends>(out var sends)) throw new Exception("No sends");
sends.SetSend(0, slot); source.Play();
var pcm = new float[4096]; device.Render(pcm);
if (!pcm.Any(s => Math.Abs(s) > .01)) throw new Exception("No rendered PCM");
if (Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
    .Any(p => Path.GetFileName(p).Contains("miniaudio", StringComparison.OrdinalIgnoreCase)))
    throw new Exception("MiniAudio leaked into the OpenAL consumer");
if (!device.TryGetFeature<IPreparedClips>(out var clips)) throw new Exception("No prepared clips");
var clip = new SoundFlow.Playback.Clip(Enumerable.Range(0, 2048).Select(i => .1f * MathF.Sin(i * .1f)).ToArray(), mono);
using var prepared = clips.Prepare(clip);
using var native = prepared.CreateSource(); native.Play(); device.Render(pcm);
using var pool = device.CreatePool(new() { Capacity = 1 });
using var first = pool.Play(clip, new() { Priority = 1 });
using var second = pool.Play(clip, new() { Priority = 2 });
if (first.Outcome?.Reason != PlaybackEndReason.Stolen) throw new Exception("Pool did not steal its lower-priority voice");
if (effects.SupportedEffects.Contains(typeof(ConvolutionSettings)))
{
    var response = new float[1024]; response[0] = .5f;
    using var convolution = effects.CreateSlot(new ConvolutionSettings { ImpulseResponse = new SoundFlow.Playback.Clip(response, mono) });
    if (!second.Source.TryGetFeature<IEffectSends>(out var wet)) throw new Exception("No native wet routing");
    wet.SetSend(0, convolution); device.Render(pcm);
}
Console.WriteLine("PASS: isolated NuGet consumer renders OpenAL/EFX, prepared clips and pooled playback without MiniAudio.");
''')
    subprocess.run(["dotnet", "restore", str(work / "Consumer.csproj"), "--configfile", str(work / "NuGet.Config"), "--nologo"], check=True)
    subprocess.run(["dotnet", "run", "--project", str(work / "Consumer.csproj"), "-c", "Release", "--no-restore"], check=True)
    print(f"Consumer artifacts: {work}")


if __name__ == "__main__":
    main()
