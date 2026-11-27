"""Exercise the built native wrapper, including codecs, URL input, HLS and interrupted network I/O."""
import argparse
import ctypes as C
import http.server
import io
import json
import math
from pathlib import Path
import struct
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
READ = C.CFUNCTYPE(C.c_size_t, C.c_void_p, C.c_void_p, C.c_size_t)
WRITE = READ
SEEK = C.CFUNCTYPE(C.c_int64, C.c_void_p, C.c_int64, C.c_int)
INTERRUPT = C.CFUNCTYPE(C.c_int, C.c_void_p)


class Options(C.Structure):
    _fields_ = [("timeout", C.c_int64), ("probe", C.c_int64), ("analyze", C.c_int64),
                ("agent", C.c_char_p), ("headers", C.c_char_p), ("ca", C.c_char_p), ("rtsp", C.c_char_p)]


class Wrapper:
    def __init__(self, path):
        self.lib = C.CDLL(str(path.resolve()))
        declarations = {
            "sf_feature_name": (C.c_char_p, [C.c_int, C.c_int]),
            "sf_non_audio_codec_count": (C.c_int, []),
            "sf_ffmpeg_version": (C.c_char_p, []),
            "sf_decoder_create": (C.c_void_p, []),
            "sf_decoder_init": (C.c_int, [C.c_void_p, READ, SEEK, C.c_void_p, C.c_int, C.c_void_p, C.c_void_p, C.c_void_p]),
            "sf_decoder_init_url": (C.c_int, [C.c_void_p, C.c_char_p, C.POINTER(Options), INTERRUPT, C.c_void_p, C.c_int, C.c_void_p, C.c_void_p, C.c_void_p]),
            "sf_decoder_read_pcm_frames": (C.c_int, [C.c_void_p, C.c_void_p, C.c_int64, C.POINTER(C.c_int64)]),
            "sf_decoder_seek_to_pcm_frame": (C.c_int, [C.c_void_p, C.c_int64]),
            "sf_decoder_free": (None, [C.c_void_p]),
            "sf_encoder_create": (C.c_void_p, []),
            "sf_encoder_init_ex": (C.c_int, [C.c_void_p, C.c_char_p, WRITE, SEEK, C.c_void_p, C.c_int, C.c_uint, C.c_uint]),
            "sf_encoder_write_pcm_frames": (C.c_int, [C.c_void_p, C.c_void_p, C.c_int64, C.POINTER(C.c_int64)]),
            "sf_encoder_free": (None, [C.c_void_p])}
        for name, (result, arguments) in declarations.items():
            function = getattr(self.lib, name)
            function.restype, function.argtypes = result, arguments

    def capabilities(self):
        names = ["decoders", "encoders", "demuxers", "muxers", "input_protocols", "output_protocols"]
        result = {"version": self.lib.sf_ffmpeg_version().decode()}
        for category, name in enumerate(names):
            values = set()
            for index in range(10000):
                value = self.lib.sf_feature_name(category, index)
                if not value:
                    break
                values.update(value.decode().split(','))
            result[name] = sorted(values)
        assert self.lib.sf_non_audio_codec_count() == 0, "Video/subtitle codecs leaked into the audio runtime"
        return result

    def encode(self, kind):
        stream = io.BytesIO()
        @WRITE
        def write(_, pointer, size):
            return stream.write(C.string_at(pointer, size))
        @SEEK
        def seek(_, offset, whence):
            if whence == 0x10000:
                return len(stream.getvalue())
            return stream.seek(offset, whence & 0xffff)
        handle = self.lib.sf_encoder_create()
        try:
            result = self.lib.sf_encoder_init_ex(handle, kind.encode(), write, seek, None, 5, 2, 48000)
            assert result == 0, ("encoder init", kind, result)
            samples = (C.c_float * 9600)(*[.2 * math.sin((i // 2) * .08) for i in range(9600)])
            written = C.c_int64()
            result = self.lib.sf_encoder_write_pcm_frames(handle, samples, 4800, C.byref(written))
            assert result == 0 and written.value == 4800, ("encode", kind, result, written.value)
        finally:
            self.lib.sf_encoder_free(handle)
        return stream.getvalue()

    def decode(self, data=None, url=None, interrupted=None, timeout=2000000, allow_failure=False):
        stream = io.BytesIO(data or b'')
        @READ
        def read(_, pointer, count):
            chunk = stream.read(count)
            C.memmove(pointer, chunk, len(chunk))
            return len(chunk)
        @SEEK
        def seek(_, offset, whence):
            if whence == 0x10000:
                return len(stream.getvalue())
            return stream.seek(offset, whence & 0xffff)
        @INTERRUPT
        def interrupt(_):
            return int(interrupted is not None and interrupted.is_set())
        handle = self.lib.sf_decoder_create()
        channels, rate = C.c_uint(), C.c_uint()
        try:
            if url:
                options = Options(timeout, 65536, 1000000, b'SoundFlow native verification', None, None, b'tcp')
                result = self.lib.sf_decoder_init_url(handle, url.encode(), C.byref(options), interrupt, None,
                    5, None, C.byref(channels), C.byref(rate))
            else:
                result = self.lib.sf_decoder_init(handle, read, seek, None, 5, None, C.byref(channels), C.byref(rate))
            if allow_failure:
                assert result != 0, "Expected a network open failure"
                return
            assert result == 0, ("decoder init", result)
            assert channels.value in (1, 2) and rate.value > 0
            samples = (C.c_float * (2048 * channels.value))()
            decoded = []
            for _ in range(100):
                count = C.c_int64()
                result = self.lib.sf_decoder_read_pcm_frames(handle, samples, 2048, C.byref(count))
                assert result == 0, ("decode", result)
                if count.value == 0:
                    break
                decoded.extend(samples[:count.value * channels.value])
            assert len(decoded) >= 100, "Decoder returned no useful audio"
            assert all(math.isfinite(sample) for sample in decoded)
            assert max(abs(sample) for sample in decoded) > .01, "Decoded audio is silent"
            if data:
                assert self.lib.sf_decoder_seek_to_pcm_frame(handle, 0) == 0
                count = C.c_int64()
                assert self.lib.sf_decoder_read_pcm_frames(handle, samples, 2048, C.byref(count)) == 0
                assert list(samples[:min(len(decoded), count.value * channels.value)]) == decoded[:count.value * channels.value]
            return decoded
        finally:
            self.lib.sf_decoder_free(handle)


def ima_wave():
    block = struct.pack('<hBBhBB', 1000, 30, 0, -1000, 20, 0) + bytes.fromhex('1234567889abcdef')
    data = block * 80
    fmt = struct.pack('<HHIIHHHH', 17, 2, 44100, 78400, 16, 4, 2, 9)
    body = b'WAVEfmt ' + struct.pack('<I', 20) + fmt + b'fact' + struct.pack('<II', 4, 720) + b'data' + struct.pack('<I', len(data)) + data
    return b'RIFF' + struct.pack('<I', len(body)) + body


def verify(path, report):
    wrapper = Wrapper(path)
    capabilities = wrapper.capabilities()
    assert capabilities['version'] == json.loads((ROOT / 'Packaging/FFmpeg.json').read_text())['version']
    for group, required in {
        'decoders': ['aac', 'mp3float', 'pcm_s16le', 'pcm_mulaw', 'pcm_alaw', 'adpcm_ima_wav', 'adpcm_ms', 'flac', 'alac', 'opus', 'vorbis'],
        'encoders': ['aac', 'libmp3lame', 'libopus', 'libvorbis', 'flac', 'alac'],
        'demuxers': ['wav', 'mp3', 'aac', 'ogg', 'mov', 'matroska', 'hls', 'mpegts', 'rtsp'],
        'input_protocols': ['http', 'https', 'tcp', 'udp', 'rtp', 'rtmp', 'rtmps', 'tls']}.items():
        assert set(required) <= set(capabilities[group]), (group, set(required) - set(capabilities[group]))
    encoded = {}
    for kind in ['wav', 'aiff', 'flac', 'mp3', 'aac', 'm4a', 'alac', 'ogg', 'opus']:
        encoded[kind] = wrapper.encode(kind)
        pcm = wrapper.decode(data=encoded[kind])
        assert 7000 <= len(pcm) <= 14000, (kind, 'incorrect duration', len(pcm))
        print('PASS native encode/decode/seek:', kind, flush=True)
    wrapper.decode(data=ima_wave())
    print('PASS native IMA ADPCM WAV', flush=True)
    stop = threading.Event()
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass
        def do_GET(self):
            if self.path == '/stall':
                self.send_response(200)
                self.end_headers()
                stop.wait(6)
                return
            if self.path == '/audio.m3u8':
                data = b'#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:1,\n/segment.aac\n#EXT-X-ENDLIST\n'
                kind = 'application/vnd.apple.mpegurl'
            elif self.path == '/segment.aac':
                data, kind = encoded['aac'], 'audio/aac'
            else:
                data, kind = encoded['mp3'], 'audio/mpeg'
            self.send_response(200)
            self.send_header('Content-Type', kind)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = 'http://127.0.0.1:' + str(server.server_port)
    try:
        wrapper.decode(url=url + '/radio')
        print('PASS HTTP audio URL', flush=True)
        wrapper.decode(url=url + '/audio.m3u8')
        print('PASS audio HLS playlist', flush=True)
        cancelled = threading.Event()
        timer = threading.Timer(.25, cancelled.set)
        timer.start()
        start = time.monotonic()
        wrapper.decode(url=url + '/stall', interrupted=cancelled, timeout=5000000, allow_failure=True)
        assert time.monotonic() - start < 3, 'Cancellation did not interrupt blocked native I/O'
        timer.join()
        start = time.monotonic()
        wrapper.decode(url=url + '/stall', timeout=300000, allow_failure=True)
        assert time.monotonic() - start < 3, 'Network timeout was not honored'
        print('PASS network cancellation and timeout', flush=True)
    finally:
        stop.set()
        server.shutdown()
        server.server_close()
    if report:
        report.write_text(json.dumps(capabilities, indent=2) + '\n')
    print('PASS audio-only native profile; no video/subtitle codecs', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library', type=Path, required=True)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    verify(args.library, args.report)
