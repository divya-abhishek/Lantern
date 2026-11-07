"""Receiver-facing decoded PTS evidence; never claims physical display cadence.
Run against a currently running mirror. Credential URL stays in memory.
"""
import json, subprocess, urllib.request, argparse, statistics, pathlib
p = argparse.ArgumentParser()
p.add_argument('--seconds', type=int, default=12)
p.add_argument('--output', default='.verification/cadence.json')
a = p.parse_args()
s = json.load(urllib.request.urlopen('http://localhost:8787/api/v1/mirror/status'))
cmd = ['C:/ffmpeg/bin/ffprobe.exe', '-v', 'error', '-rtsp_transport', 'tcp',
       '-read_intervals', f'%+{a.seconds}', '-show_frames', '-show_streams',
       '-show_entries', 'frame=media_type,best_effort_timestamp_time,pkt_duration_time:stream=codec_name,width,height,avg_frame_rate,sample_rate',
       '-of', 'json', s['streamUrl']]
r = subprocess.run(cmd, capture_output=True, text=True, timeout=a.seconds+30)
data = json.loads(r.stdout)
video = [float(f['best_effort_timestamp_time']) for f in data['frames'] if f.get('media_type')=='video' and 'best_effort_timestamp_time' in f]
audio = [float(f['best_effort_timestamp_time']) for f in data['frames'] if f.get('media_type')=='audio' and 'best_effort_timestamp_time' in f]
def cadence(v):
    steps = [y-x for x,y in zip(v,v[1:])]
    return dict(frames=len(v), first=v[:6], last=v[-1] if v else None,
                fps=(len(v)-1)/(v[-1]-v[0]) if len(v)>1 else None,
                minStep=min(steps,default=0), maxStep=max(steps,default=0),
                medianStep=statistics.median(steps) if steps else None,
                backwards=sum(x<=0 for x in steps), gaps=sum(x>1.5/s['settings']['fps'] for x in steps))
result=dict(settings=s['settings'], streams=data['streams'], all=cadence(video),
            afterStartup=cadence(video[s['settings']['fps']*2:]),
            audioFrames=len(audio), audioFirst=audio[:3], audioLast=audio[-1] if audio else None,
            publisherClockEstimateMs=s['latencyMs'], publisherDriftMs=s['driftMs'],
            limitation='Decoded timestamps at PC RTSP subscriber; not Android display timing or physical A/V synchronization')
pathlib.Path(a.output).write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
