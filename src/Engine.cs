using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VgmDesk;

public record EngineInfo(string Version, HashSet<string> Extensions);
public record AudioInfo(int SampleRate, int Channels, long Samples, long Bitrate, string Encoding,
    string Layout, string Format, int Total, string Name, long LoopStart, long LoopEnd)
{
    public double Duration => (double)Samples / SampleRate;
    public bool HasLoop => LoopEnd > LoopStart && LoopStart >= 0;
}

public enum LoopMode { Once, Fade, Tail, Preserve }
public record ExportOptions(LoopMode Mode, double Loops = 2, double Fade = 10, double Delay = 0, int SampleFormat = 1)
{
    public IEnumerable<string> Flags()
    {
        static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        if (Mode is LoopMode.Once) yield return "-i";
        else if (Mode is LoopMode.Preserve) yield return "-L";
        else
        {
            yield return "-l"; yield return F(Loops);
            if (Mode is LoopMode.Tail) yield return "-F";
            else { yield return "-f"; yield return F(Fade); yield return "-d"; yield return F(Delay); }
        }
        yield return "-W"; yield return SampleFormat.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class Engine(string exe)
{
    public string Exe { get; } = Path.GetFullPath(exe);

    private async Task<(int Exit, string Out, string Err)> RunAsync(IEnumerable<string> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Exe) {
            WorkingDirectory = Path.GetDirectoryName(Exe)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 vgmstream CLI。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancel = token.Register(() => {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        await process.WaitForExitAsync().ConfigureAwait(false);
        var result = (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<(int Exit, string Out, string Err)> QueryAsync(string[] args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try { return await RunAsync(args, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("vgmstream 分析超时（25 秒）。"); }
    }

    private static IOException CliError(int code, string output, string error)
    {
        var detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (detail.Length > 900) detail = detail[..900] + "…";
        return new IOException($"vgmstream 退出码 {code}" + (detail.Length == 0 ? "。" : $"：{detail}"));
    }

    private static JsonDocument ParseJson(string output)
    {
        // Some decoders log diagnostics before the one-line metadata object.
        foreach (var line in output.Split('\n').Reverse())
        {
            var value = line.Trim();
            if (!value.StartsWith('{')) continue;
            try { return JsonDocument.Parse(value); } catch (JsonException) { }
        }
        try { return JsonDocument.Parse(output); }
        catch (JsonException e) { throw new IOException("无法读取 CLI 的 JSON。请使用支持 -V 与 -m -I 的新版 vgmstream-cli.exe。", e); }
    }

    public async Task<EngineInfo> VersionAsync(CancellationToken token)
    {
        var (code, output, error) = await QueryAsync(["-V"], token).ConfigureAwait(false);
        // Upstream r2168 prints valid JSON, then exits 1 for -V.
        if (string.IsNullOrWhiteSpace(output)) throw CliError(code, output, error);
        using var doc = ParseJson(output);
        var root = doc.RootElement;
        if (Text(root, "version") is not { Length: > 0 } version ||
            !root.TryGetProperty("extensions", out var sets) || sets.ValueKind != JsonValueKind.Object)
            throw new IOException("此程序没有返回 vgmstream 的版本和格式列表。");
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { "vgm", "common" })
            if (sets.TryGetProperty(kind, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } ext) extensions.Add(ext);
        if (extensions.Count == 0) throw new IOException("vgmstream 没有报告可识别的扩展名。");
        return new(version, extensions);
    }

    private static JsonElement Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;
    private static long Number(JsonElement obj, string name)
    {
        var v = Property(obj, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
    private static string Text(JsonElement obj, string name)
    {
        var v = Property(obj, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    public async Task<AudioInfo> InspectAsync(string input, int subsong, CancellationToken token)
    {
        var (code, output, error) = await QueryAsync(["-m", "-I", "-s", subsong.ToString(CultureInfo.InvariantCulture), Path.GetFullPath(input)], token).ConfigureAwait(false);
        if (code != 0) throw CliError(code, output, error);
        using var doc = ParseJson(output);
        var info = doc.RootElement;
        var stream = Property(info, "streamInfo");
        var loop = Property(info, "loopingInfo");
        var rate = Number(info, "sampleRate"); var channels = Number(info, "channels"); var samples = Number(info, "numberOfSamples");
        if (rate is <= 0 or > int.MaxValue || channels is <= 0 or > int.MaxValue || samples <= 0)
            throw new IOException("CLI 未返回有效的采样率、声道数或样本数。");
        return new((int)rate, (int)channels, samples, Number(info, "bitrate"), Text(info, "encoding"),
            Text(info, "layout"), Text(info, "metadataSource"), (int)Math.Clamp(Number(stream, "total"), 1, int.MaxValue),
            Text(stream, "name"), Number(loop, "start"), Number(loop, "end"));
    }

    public async Task<string> ExportAsync(string input, int subsong, int total, string folder, ExportOptions options, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(input);
        if (string.IsNullOrWhiteSpace(name)) name = "audio";
        if (name.Length > 160) name = name[..(char.IsHighSurrogate(name[159]) ? 159 : 160)];
        if (total > 1) name += $"_{subsong:D3}";
        return await DecodeAsync(input, subsong, folder, name, options, false, token).ConfigureAwait(false);
    }

    public Task<string> PreviewAsync(string input, int subsong, string folder, CancellationToken token) =>
        DecodeAsync(input, subsong, folder, Guid.NewGuid().ToString("N"), new(LoopMode.Once), true, token);

    private async Task<string> DecodeAsync(string input, int subsong, string folder, string name, ExportOptions options, bool preview, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        var temporary = Path.Combine(folder, $".vgmdesk-{Guid.NewGuid():N}.part");
        try
        {
            var args = new List<string> { "-s", subsong.ToString(CultureInfo.InvariantCulture), "-o", temporary };
            args.AddRange(options.Flags());
            if (preview) args.AddRange(["-D", "2"]);
            args.Add(Path.GetFullPath(input));
            var (code, output, error) = await RunAsync(args, token).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temporary)) throw CliError(code, output, error);
            ValidateWave(temporary);
            for (int n = 1; ; n++)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(folder, (n == 1 ? name : $"{name} ({n})") + ".wav");
                try { File.Move(temporary, destination); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        finally { TryDelete(temporary); }
    }

    private static void ValidateWave(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        var length = reader.BaseStream.Length;
        if (length < 44 || reader.ReadUInt32() != 0x46464952) throw new IOException("生成的 WAV 不完整，未导出。");
        var end = reader.ReadUInt32() + 8L;
        if (reader.ReadUInt32() != 0x45564157 || end > length || end < 44 || length > uint.MaxValue + 8L)
            throw new IOException("生成的 WAV 不完整，或超出了标准 WAV 的大小限制。");
        while (reader.BaseStream.Position + 8 <= end)
        {
            var id = reader.ReadUInt32(); var size = reader.ReadUInt32(); var next = reader.BaseStream.Position + size;
            if (next > end) break;
            if (id == 0x61746164 && size > 0) return;
            reader.BaseStream.Position = next + (size & 1);
        }
        throw new IOException("生成的 WAV 没有完整的音频数据，未导出。");
    }

    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
