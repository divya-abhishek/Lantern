"""Packaged runtime/relay/encoder sanity; no Android smoothness claim or stimulus."""
import json, os, pathlib, subprocess, time, urllib.request

root = pathlib.Path(__file__).resolve().parents[2]
package = root / 'dist/Lantern'
evidence = root / '.verification'
port = 8792
api = f'http://localhost:{port}'

def request(path, body=None):
    req = urllib.request.Request(api + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=120) as response:
        return response.read()

env = dict(os.environ)
env['PATH'] = os.environ['SystemRoot'] + r'\System32;' + os.environ['SystemRoot']
result = {}
with (evidence / 'clock-packaged-server.log').open('w') as log:
    server = subprocess.Popen([str(package/'server/Lantern.Server.exe'),
        '--port', str(port), '--db', str(evidence/'clock-package.db'),
        '--cache', str(evidence/'clock-package-cache'), '--no-scan', '--no-discovery'],
        cwd=package, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        for attempt in range(40):
            try:
                request('/healthz')
                break
            except Exception:
                if server.poll() is not None: raise RuntimeError('Packaged server exited')
                time.sleep(.25)
        else: raise RuntimeError('Packaged server did not become ready')

        # Deliberately omit audio: the normal default must be enabled.
        status = json.loads(request('/api/v1/mirror/start', {'width':1920, 'height':1080, 'fps':60}))
        assert status['active'] and status['audio'], 'Mirror/audio default failed'
        probe = subprocess.run([str(package/'server/ffmpeg/ffprobe.exe'), '-v','error',
            '-rtsp_transport','tcp','-read_intervals','%+5','-show_frames','-show_streams',
            '-show_entries','frame=media_type,best_effort_timestamp_time:stream=codec_name,width,height,sample_rate',
            '-of','json',status['streamUrl']], capture_output=True, timeout=35)
        if probe.returncode: raise RuntimeError('Packaged RTSP probe failed (URL intentionally omitted)')
        data = json.loads(probe.stdout)
        vstream = next(s for s in data['streams'] if s['codec_name'] == 'h264')
        assert (vstream['width'], vstream['height']) == (1920,1080)
        assert any(s['codec_name'] == 'aac' for s in data['streams'])
        pts = [float(f['best_effort_timestamp_time']) for f in data['frames']
            if f['media_type']=='video' and 'best_effort_timestamp_time' in f][120:]
        assert len(pts)>60
        fps = (len(pts)-1)/(pts[-1]-pts[0])
        assert abs(fps-60)<.1 and all(0 < b-a < .026 for a,b in zip(pts,pts[1:]))
        result = dict(packagedRuntime=True, bundledTools=True, audioDefault=True,
            dimensions=[1920,1080], decodedProbeFps=fps, videoFramesAfterStartup=len(pts),
            limitation='Local PC decode, not Android rendering or audible/visible A/V sync')
    finally:
        try: request('/api/v1/mirror/stop', {})
        finally:
            server.terminate()
            server.wait(timeout=15)
(evidence/'clock-package-sanity.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
