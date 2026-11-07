"""Finite media/auth acceptance against the isolated fixture library (IDs 1/2/3).
Never prints pairing tokens. Run only against the verification DB, not a user's library.
"""
import argparse, json, secrets, time, urllib.request, urllib.error
p = argparse.ArgumentParser()
p.add_argument('--base', default='http://127.0.0.1:8787')
p.add_argument('--lan', required=True, help='Same server via its non-loopback LAN address')
a = p.parse_args()
def request(path, method='GET', body=None, headers=None, lan=False):
    url = (a.lan if lan else a.base).rstrip('/') + path
    h = dict(headers or {})
    if body is not None: h['Content-Type'] = 'application/json'
    req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, headers=h, method=method)
    try: response = urllib.request.urlopen(req, timeout=30)
    except urllib.error.HTTPError as e: response = e
    with response:
        raw = response.read()
        try: data = json.loads(raw)
        except (ValueError, UnicodeDecodeError): data = raw
        return response.status, response.headers, data
def check(ok, name):
    if not ok: raise AssertionError(name)
    print('PASS ' + name, flush=True)
api = '/api/v1'
check(request(api+'/media', lan=True)[0] == 401, 'unpaired LAN media denied')
secret = secrets.token_hex(24)
device_id = 'acceptance-' + secrets.token_hex(6)
status, _, pair = request(api+'/pairing/request', 'POST', {'deviceId':device_id,'name':'Temporary acceptance receiver','pollSecret':secret}, lan=True)
check(status == 202, 'pair request accepted')
pin = pair['requestId']
check(request(api+'/pairing/status/'+pin, lan=True)[0] == 403, 'PIN alone cannot obtain token')
check(request(api+'/pairing/approve/'+pin, 'POST', {}, lan=True)[0] in (401,403), 'remote approval denied')
check(request(api+'/pairing/approve/'+pin, 'POST', {})[0] == 200, 'local PIN approval')
status, _, approved = request(api+'/pairing/status/'+pin, headers={'X-Lantern-Pairing':secret}, lan=True)
check(status == 200 and bool(approved.get('token')), 'private polling secret retrieves approved token')
auth = {'X-Lantern-Token':approved['token'], 'X-Lantern-Device':device_id}
try:
    check(request(api+'/auth/check', headers=auth, lan=True)[0] == 200, 'paired authentication')
    check(request(api+'/devices/receivers', headers=auth, lan=True)[0] == 403, 'trusted receiver cannot administer discovery')
    check(not request(api+'/pairing/status/'+pin, headers={'X-Lantern-Pairing':secret}, lan=True)[2].get('token'), 'token consumed once')
    detail = request(api+'/media/2?plan=true')[2]
    check(detail['durationMs'] > 25000 and detail['plan']['preparationState'] == 'NotStarted' and not detail['plan'].get('playUrl') and not detail['plan'].get('sessionId'), 'AVI details have finite duration and do not start preparation')
    for media_id in (3,2,1):
        body = {'mediaId':media_id}
        status, _, plan = request(api+'/playback/plan', 'POST', body, auth, True)
        end = time.monotonic()+180
        saw_preparing = False
        while plan.get('preparationState') == 'Preparing' and time.monotonic() < end:
            saw_preparing = True
            check(not plan.get('playUrl'), f'{media_id}: partial preparation has no playable URL')
            time.sleep(1)
            status, _, plan = request(api+'/playback/plan','POST',body,auth,True)
        check(status == 200 and plan['preparationState'] == 'Ready' and plan.get('playUrl'), f'{media_id}: Ready finite playback URL')
        if media_id == 3: check(plan['mode'] == 'directPlay', 'ordinary H264 MP4 stays direct')
        else:
            check(plan['mode'] == 'transcode' and plan['mimeType'] == 'video/mp4' and plan['durationMs'] > 5000, f'{media_id}: finite PC fallback')
            if media_id == 1: check(plan['outputWidth'] <= 1920 and plan['outputHeight'] <= 1080, 'incompatible 4K capped at 1080p')
        path = plan['playUrl']
        code, headers, _ = request(path, 'HEAD', headers=auth, lan=True)
        length = int(headers['Content-Length'])
        check(code == 200 and length > 0 and headers.get('Accept-Ranges') == 'bytes', f'{media_id}: finite length and byte seeking')
        code, h, data = request(path, headers={**auth,'Range':'bytes=0-1023'}, lan=True)
        check(code == 206 and len(data) == 1024 and h['Content-Range'] == f'bytes 0-1023/{length}', f'{media_id}: forward byte range')
        code, h, data = request(path, headers={**auth,'Range':'bytes=-512'}, lan=True)
        check(code == 206 and len(data) == 512, f'{media_id}: suffix/EOF byte range')
        check(request(path,'HEAD',headers={**auth,'Range':'bytes=0-'},lan=True)[0] == 206, f'{media_id}: explicit whole-file range is 206')
        check(request(path,headers={**auth,'Range':f'bytes={length+1}-'},lan=True)[0] == 416, f'{media_id}: beyond EOF is 416')
        if media_id != 3:
            request(api+f'/media/{media_id}/transcode/stop?sessionId='+plan['sessionId'],'POST',{},auth,True)
            replay = request(api+'/playback/plan','POST',body,auth,True)[2]
            check(replay['preparationState'] == 'Ready', f'{media_id}: completed cache immediately reused')
            request(api+f'/media/{media_id}/transcode/stop?sessionId='+replay['sessionId'],'POST',{},auth,True)
    check(request(api+'/media/3/stream',headers={**auth,'Range':'bytes=0-nonsense'},lan=True)[0] == 416, 'malformed direct byte range rejected')
finally:
    devices = request(api+'/devices')[2]
    for device in devices:
        if device.get('deviceId') == device_id:
            request(api+f"/devices/{device['id']}/revoke",'POST',{})
print('HTTP ACCEPTANCE PASSED')
