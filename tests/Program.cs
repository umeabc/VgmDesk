using VgmDesk;

static void Expect(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}
static (uint Data, ushort Bits, bool Loop) Wave(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    uint data = 0; ushort bits = 0; bool loop = false;
    reader.BaseStream.Position = 12;
    while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
    {
        var id = new string(reader.ReadChars(4)); var size = reader.ReadUInt32();
        var next = reader.BaseStream.Position + size + (size & 1);
        if (id == "data") data = size;
        if (id == "smpl") loop = true;
        if (id == "fmt ") { reader.BaseStream.Position += 14; bits = reader.ReadUInt16(); }
        reader.BaseStream.Position = next;
    }
    return (data, bits, loop);
}
var engine = new Engine(args[0]);
var fixtures = args[1];
var loop = Path.Combine(fixtures, "循环 音频.lwav");
var fsb = Path.Combine(fixtures, "双音轨.fsb");
var output = Path.Combine(Path.GetFullPath(fixtures), "VgmDesk-test-" + Guid.NewGuid().ToString("N"), "中文 输出");
using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var token = ct.Token;
try
{
    var ver = await engine.VersionAsync(token);
    Expect(ver.Version == "r2168" && ver.Extensions.Contains("fsb"), "r2168 -V JSON with nonzero exit");
    var meta = await engine.InspectAsync(loop, 1, token);
    Expect(meta.SampleRate == 16000 && meta.Channels == 1 && meta.Samples == 64000 && meta.LoopStart == 8000 && meta.LoopEnd == 24000, "metadata and loop marker positions on Unicode path");
    Expect(!File.Exists(loop + ".wav"), "metadata-only never creates WAV");
    var first = await engine.InspectAsync(fsb, 1, token);
    var second = await engine.InspectAsync(fsb, 2, token);
    Expect(first.Total == 2 && first.Name == "first" && second.Name == "second" && second.Samples == 32000, "FSB5 subsongs");
    var a = await engine.ExportAsync(fsb, 1, 2, output, new(LoopMode.Once), token);
    var b = await engine.ExportAsync(fsb, 2, 2, output, new(LoopMode.Once), token);
    Expect(Path.GetFileName(a) == "双音轨_001.wav" && Wave(a).Data == 32000 && Path.GetFileName(b) == "双音轨_002.wav" && Wave(b).Data == 64000, "batch subsong WAV output and Chinese filenames");
    var dup = await engine.ExportAsync(fsb, 1, 2, output, new(LoopMode.Once), token);
    Expect(Path.GetFileName(dup) == "双音轨_001 (2).wav" && File.Exists(a), "never overwrite");
    var keep = await engine.ExportAsync(loop, 1, 1, output, new(LoopMode.Preserve, SampleFormat: 2), token);
    Expect(Wave(keep) == (192000u, (ushort)24, true), "24-bit WAV with preserved smpl loop");
    var fade = await engine.ExportAsync(loop, 1, 1, output, new(LoopMode.Fade, Loops: 2, Fade: .5, Delay: .25), token);
    Expect(Wave(fade).Data == 104000, "loop count, delay and fade");
    var tail = await engine.ExportAsync(loop, 1, 1, output, new(LoopMode.Tail), token);
    Expect(Wave(tail).Data == 160000, "two loops then original tail");
    var preview = await engine.PreviewAsync(loop, 1, output, token);
    Expect(Wave(preview) == (128000u, (ushort)16, false), "preview is PCM16, no loop repetitions");
    var broken = Path.Combine(fixtures, "broken.fsb");
    try { await engine.ExportAsync(broken, 1, 1, output, new(LoopMode.Once), token); throw new Exception("corrupt file accepted"); }
    catch (IOException) { Expect(!File.Exists(Path.Combine(output, "broken.wav")), "bad input has no final output"); }
    try { await engine.ExportAsync(loop, 1, 1, output, new(LoopMode.Once), new(true)); throw new Exception("cancel ignored"); }
    catch (OperationCanceledException) { Expect(!Directory.EnumerateFiles(output, "*.part").Any(), "cancelled and failed jobs leave no partial files"); }
}
finally
{
    var parent = Path.GetDirectoryName(output)!;
    if (Directory.Exists(parent)) Directory.Delete(parent, true);
}
