using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lantern.Core;
using Lantern.Core.Compatibility;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Mirroring;
using Lantern.Media.Processes;
using Lantern.Media.Probing;
using Lantern.Media.Tooling;
using Lantern.Media.Transcoding;
using Lantern.Network.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--owned-child"))
{
    var child = ProcessRunner.StartStreaming("C:/ffmpeg/bin/ffmpeg.exe",
        ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=1", "-f", "null", "-"]);
    Console.WriteLine(child.Id);
    await Task.Delay(Timeout.Infinite);
    return;
}
void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
}
async Task Until(Func<bool> condition, int seconds = 30)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
    Check(condition(), "operation completed within deadline");
}
var root = Path.GetFullPath(".verification");
var settings = new LanternSettings { FfmpegPath = "C:/ffmpeg/bin", CachePath = Path.Combine(root, "manager-tests"), TranscodeEncoder = "auto" };
var locator = new FfmpegLocator(NullLogger<FfmpegLocator>.Instance);
var probe = new FfprobeRunner(locator, NullLogger<FfprobeRunner>.Instance);
var source = Path.Combine(root, "media", "Lantern AVI.avi");
var info = await probe.ProbeAsync(source, settings);
Check(info.Succeeded && info.AudioCodec == "mp3" && info.AudioTracks.Count == 2, "probe preserves audio summary and both tracks");
var file = info.ToRecord(101, source, File.GetLastWriteTimeUtc(source), DateTime.UtcNow);
file.Id = 101;
var engine = new CompatibilityEngine();
var device = DeviceCapabilities.Unknown("test-client");
var plan = engine.Evaluate(file, device);
Check(plan.RequiresSession && plan.OutputVideoCodec == "h264", "AVI requires PC preparation");
var big = new MediaFileRecord { Path = source, VideoCodec = "av1", Container = "mkv", Width = 3840, Height = 2160 };
var bigPlan = engine.Evaluate(big, device);
Check(bigPlan.OutputWidth <= 1920 && bigPlan.OutputHeight <= 1080, "unsupported 4K/AV1 has bounded H264 fallback");
var original = new MediaFileRecord { Path = source + ".mp4", VideoCodec = "h264", Container = "mp4", Width = 1280, Height = 720, AudioCodec = "aac" };
Check(!engine.Evaluate(original, device).RequiresSession, "compatible MP4 direct plays");
Check(!device.SupportsVideo("hevc"), "unreported HEVC hardware never direct plays");
var key = TranscodeSessionManager.CachePath(file, plan, root);
var changedPlan = engine.Evaluate(file, device, requestedQuality: StreamQuality.Low);
Check(key != TranscodeSessionManager.CachePath(file, changedPlan, root), "different output profile changes cache identity");
var identityPath = Path.Combine(root, "cache-identity-fixture.bin");
File.WriteAllText(identityPath, "first");
var identityFile = new MediaFileRecord { Path = identityPath };
var before = TranscodeSessionManager.CachePath(identityFile, plan, root);
File.AppendAllText(identityPath, " modified");
Check(before != TranscodeSessionManager.CachePath(identityFile, plan, root), "modified source changes cache identity");

await using (var manager = new TranscodeSessionManager(locator, NullLogger<TranscodeSessionManager>.Instance))
{
    var session = await manager.StartAsync(file, plan, settings, "client-a");
    if (session.IsRunning) Check(!session.Ready, "partial encode cannot be ready");
    await Until(() => !session.IsRunning);
    Check(session.Ready, "finite preparation completes");
    var prepared = await probe.ProbeAsync(session.CompletedPath, settings);
    Check(prepared.DurationMs >= 25000 && prepared.AudioTracks.Count == 2 && prepared.AudioTracks.All(t => t.Codec == "aac"), "completed MP4 finite duration and all AAC audio tracks");
    var completedPath = session.CompletedPath;
    var size = new FileInfo(completedPath).Length;
    await manager.StopAsync(session.Id);
    Check(!Directory.Exists(session.OutputDirectory), "stopped session removes work directory");
    manager.MaxConcurrentSessions = 0; // No encoder slot: a ready cache hit must still play.
    var replay = await manager.StartAsync(file, plan, settings, "client-a");
    Check(replay.Ready && replay.CompletedPath == completedPath && replay.Encoder == null && new FileInfo(completedPath).Length == size, "completed cache replay starts without encoder");
    await manager.StopAsync(replay.Id);
    manager.MaxConcurrentSessions = 2;

    var invalidPath = Path.Combine(root, "invalid-media.avi");
    File.WriteAllText(invalidPath, "Deliberately invalid media for failure verification.");
    var invalid = new MediaFileRecord { Id = 102, MediaId = 102, Path = invalidPath, VideoCodec = "mpeg4", Container = "avi" };
    var invalidPlan = engine.Evaluate(invalid, device);
    var failed = await manager.StartAsync(invalid, invalidPlan, settings, "client-a");
    await Until(() => !failed.IsRunning);
    Check(failed.State == TranscodeState.Failed && !failed.Ready, "failed encode cannot publish partial file");
    var polled = await manager.StartAsync(invalid, invalidPlan, settings, "client-a");
    Check(polled.Id == failed.Id && polled.State == TranscodeState.Failed, "failure polling does not restart encoder");
    await manager.StopAsync(failed.Id);
    var cancelPlan = engine.Evaluate(file, device, requestedQuality: StreamQuality.Low);
    var cancelled = await manager.StartAsync(file, cancelPlan, settings, "client-cancel");
    await manager.StopAsync(cancelled.Id);
    Check(!cancelled.IsRunning && !Directory.Exists(cancelled.OutputDirectory), "cancel joins encoder and clears partial work");
    manager.IdleTimeout = TimeSpan.Zero;
    manager.MaxCacheBytes = 1;
    await manager.CleanupOnceAsync();
    Check(!File.Exists(completedPath), "cache limit prunes inactive completed outputs");
}

await using (var discovery = new UdpBeaconListener { Port = 18788, JoinMulticast = false, ProbeOnStart = false })
{
    var directory = new ReceiverDirectory();
    discovery.ReceiverAnnounced += (_, receiver) => directory.Update(receiver);
    await discovery.StartAsync();
    using var udp = new UdpClient();
    var endpoint = new IPEndPoint(IPAddress.Loopback, 18788);
    await udp.SendAsync(BeaconCodec.Encode(new DiscoveryPacket { Server = new DiscoveryServerDto { ServerId = "pc-self", Name = "This PC", Port = 8787 } }), endpoint);
    await Task.Delay(300);
    Check(directory.Snapshot().All(r => r.DeviceId != "pc-self"), "PC announcement never becomes receiver");
    var packet = new DiscoveryPacket { Receiver = new DiscoveryReceiverDto { DeviceId = "test-phone", Name = "Test phone", DeviceType = "Phone", Address = "untrusted-payload-address" } };
    await udp.SendAsync(BeaconCodec.Encode(packet), endpoint);
    await Until(() => directory.Snapshot().Any(r => r.DeviceId == "test-phone"), 5);
    await udp.SendAsync(BeaconCodec.Encode(packet), endpoint);
    await Task.Delay(200);
    var receiver = directory.Snapshot().Single(r => r.DeviceId == "test-phone");
    Check(receiver.Address == "127.0.0.1" && receiver.DeviceType == "Phone", "receiver deduplicated and source address authoritative");
    receiver.LastSeenUtc = DateTime.UtcNow.AddSeconds(-16);
    Check(directory.Snapshot().All(r => r.DeviceId != "test-phone"), "stale receiver no longer available");
}
Check(!MirrorCommandBuilder.FormatArguments(["rtsp://user:secret@192.168.0.2/live"]).Contains("secret"), "mirror command redacts credentials");

var own = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet", StringComparison.OrdinalIgnoreCase)) own.ArgumentList.Add(typeof(ProcessOwnership).Assembly.Location.Replace("Lantern.Core.dll", "Lantern.Verification.dll"));
own.ArgumentList.Add("--owned-child");
using var parent = Process.Start(own)!;
var childPid = int.Parse((await parent.StandardOutput.ReadLineAsync())!);
using var ownedChild = Process.GetProcessById(childPid);
parent.Kill(); // Deliberately NOT entireProcessTree: the OS job must clean the child.
await parent.WaitForExitAsync();
await Until(() => ownedChild.HasExited, 5);
Check(ownedChild.HasExited, "abrupt parent death cleans owned FFmpeg");
Console.WriteLine("ALL PRODUCT CHECKS PASSED (physical Android behavior is a separate acceptance test)");
