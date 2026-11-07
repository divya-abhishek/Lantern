using Lantern.Media.Mirroring;
using Lantern.Server.Services;
using NAudio.Wave;
using Lantern.Server.Security;

var fakeTime = new InvitationTime();
var invitations = new ReceiverInvitations(fakeTime);
var invitation = invitations.Create("receiver", "192.168.1.66");
if (invitations.Consume(invitation, "other", "192.168.1.66") || invitations.Consume(invitation, "receiver", "192.168.1.67"))
    throw new Exception("Invitation accepted for wrong device/address");
if (!invitations.Consume(invitation, "receiver", "::ffff:192.168.1.66") || invitations.Consume(invitation, "receiver", "192.168.1.66"))
    throw new Exception("Invitation one-time redemption failed");
var expired = invitations.Create("receiver", "192.168.1.66");
fakeTime.Now = fakeTime.Now.AddSeconds(61);
if (invitations.Consume(expired, "receiver", "192.168.1.66")) throw new Exception("Expired invitation accepted");
var replaced = invitations.Create("receiver", "192.168.1.66");
invitations.Create("receiver", "192.168.1.66");
if (invitations.Consume(replaced, "receiver", "192.168.1.66")) throw new Exception("Replaced invitation accepted");
Console.WriteLine("PASS invitation device/address binding, one-time redemption, expiry and replacement");

const int rate = 48000, blockFrames = 480;
const long origin = 10_000_000;
var clock = new CaptureAudioClock(rate, origin);
long oldWritten = 0, oldGaps = 0, oldCuts = 0, expectedStart = 0;
// Contiguous PCM, delivered in alternating 15/5ms batches rather than exactly10ms.
for (var i = 0; i < 100; i++)
{
    var arrivalFrames = (i + 1) * blockFrames + (i % 2 == 0 ? 240 : 0);
    var oldStart = Math.Max(0, arrivalFrames - blockFrames);
    oldGaps += Math.Max(0, oldStart - oldWritten);
    oldWritten = Math.Max(oldWritten, oldStart);
    var cut = Math.Clamp(oldWritten - oldStart, 0, blockFrames);
    oldCuts += cut;
    oldWritten += blockFrames - cut;
    // Timestamp jitter here is deliberately irrelevant after the first device/QPC anchor.
    var packetQpc = origin + i * 100_000L + (i > 0 && i % 2 == 0 ? 50_000 : 0);
    var start = clock.Position(i * blockFrames, packetQpc);
    if (start != expectedStart) throw new Exception($"PCM continuity failed at packet {i}: {start} != {expectedStart}");
    expectedStart += blockFrames;
}

Console.WriteLine($"REPRO old callback clock: inserted={oldGaps} cut={oldCuts} samples in 1 second");
if (oldGaps == 0 || oldCuts == 0) throw new Exception("Old corruption was not reproduced");
Console.WriteLine("PASS hardware position clock: 48000 consecutive source samples preserved despite delivery jitter");
var resume = clock.Position(72000, origin + 15_000_000, discontinuity: true);
if (resume != 72000) throw new Exception("Resume/discontinuity did not use measured packet time");
Console.WriteLine("PASS discontinuity uses measured QPC, with a real silence gap");

Directory.CreateDirectory(".verification");
File.WriteAllText(".verification/video-source-clock.txt", MirrorCommandBuilder.VideoClock(0));
File.WriteAllText(".verification/video-anchored-clock.txt", MirrorCommandBuilder.VideoClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000));
if (args.Contains("--capture"))
{
    // Zero-valued PCM activates the device clock without an audible test stimulus.
    using var silentOutput = new WasapiOut();
    silentOutput.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
    silentOutput.Play();
    var (capture, reason) = SystemAudioCapture.TryCreate();
    if (capture is null) throw new Exception(reason);
    using (capture)
    using (var pcm = new MemoryStream())
    {
        capture.BeginCapture();
        capture.AttachTo(pcm);
        // The first device packet may straddle the session origin; trimming that
        // pre-session fragment is legitimate. Check loss after startup separately.
        await Task.Delay(1000);
        var startupDropped = capture.DroppedBytes;
        await Task.Delay(5000);
        capture.Dispose();
        if (capture.Failure is not null) throw new Exception(capture.Failure);
        if (capture.CapturedBytes == 0) throw new Exception("No native packets observed; only idle silence was tested");
        if (capture.DroppedBytes != startupDropped) throw new Exception($"Native PCM lost {capture.DroppedBytes - startupDropped} bytes after startup");
        if (pcm.Length < capture.SampleRate * capture.Channels * 2 * 3) throw new Exception("PCM timeline stalled");
        Console.WriteLine($"PASS native WASAPI lifecycle: rate={capture.SampleRate} ch={capture.Channels} captured={capture.CapturedBytes} output={pcm.Length} startupTrimBytes={startupDropped} subsequentDroppedBytes={capture.DroppedBytes - startupDropped} silenceMs={capture.SilenceMs}");
    }
}

if (args.Contains("--pipeline"))
{
    using var capture = SystemAudioCapture.TryCreate().Capture ?? throw new Exception("No capture device");
    var plan = new MirrorCapturePlan { Source=Lantern.Core.Models.MirrorSource.Desktop, DisplayIndex=0,
        Width=1920, Height=1080, Fps=60, Quality=Lantern.Core.Models.StreamQuality.High,
        Encoder="h264_nvenc", CaptureBackend="ddagrab", Audio=true, AudioSampleRate=capture.SampleRate,
        AudioChannels=capture.Channels, BitrateBps=12000000, GopFrames=30 };
    capture.BeginCapture();
    var command = MirrorCommandBuilder.Build(plan, "unused", capture.CaptureStartedMicros);
    command[command.IndexOf("-loglevel")+1]="debug";
    var outputIndex=command.IndexOf("-flush_packets");
    command.RemoveRange(outputIndex,command.Count-outputIndex);
    command.AddRange(["-t","12","-f","null","-"]);
    var start = new System.Diagnostics.ProcessStartInfo("C:/ffmpeg/bin/ffmpeg.exe") {
        RedirectStandardInput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true };
    foreach(var argument in command) start.ArgumentList.Add(argument);
    using var process=System.Diagnostics.Process.Start(start)!;
    Lantern.Core.ProcessOwnership.Attach(process);
    var log=process.StandardError.ReadToEndAsync();
    capture.AttachTo(process.StandardInput.BaseStream);
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
    var lines=await log;
    File.WriteAllText(".verification/pipeline-current.log",lines);
    foreach(var line in lines.Split('\n').Where(x=>x.Contains("frames in,") || x.Contains("Input stream"))) Console.WriteLine(line);
    if(process.ExitCode!=0) throw new Exception("Pipeline failed; inspect local debug log");
}

sealed class InvitationTime : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
