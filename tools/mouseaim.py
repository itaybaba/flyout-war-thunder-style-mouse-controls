"""Client for the FlyoutMouseAim file interface (McpBridge/MouseAim).

  python mouseaim.py status
  python mouseaim.py enable on|off
  python mouseaim.py release
  python mouseaim.py aim <yawDeg> <pitchDeg> [nose|target]
  python mouseaim.py record <seconds> [name]
  python mouseaim.py config [key value]
  python mouseaim.py step <yawDeg> <pitchDeg> <seconds> [name]   # recorded reference step
"""
import datetime, json, os, sys, time, uuid

ROOT = os.path.join(os.environ["LOCALAPPDATA"] + "Low", "Stonext Games", "Flyout", "McpBridge", "MouseAim")


def call(req, timeout=10.0):
    rid = str(uuid.uuid4())
    req = dict(req, expiresUtc=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=timeout)).isoformat())
    tmp = os.path.join(ROOT, "requests", rid + ".tmp")
    with open(tmp, "w") as f:
        json.dump(req, f)
    os.replace(tmp, os.path.join(ROOT, "requests", rid + ".json"))
    resp = os.path.join(ROOT, "responses", rid + ".json")
    end = time.time() + timeout
    while time.time() < end:
        if os.path.exists(resp):
            time.sleep(0.02)
            with open(resp) as f:
                data = json.load(f)
            os.remove(resp)
            return data
        time.sleep(0.05)
    raise TimeoutError("no response (is Flyout running with FlyoutMouseAim, and not paused?)")


def parse_value(v):
    for conv in (json.loads,):
        try:
            return conv(v)
        except Exception:
            pass
    return v


def main(a):
    if not a or a[0] == "status":
        r = call({"op": "status"})
    elif a[0] == "test_session":
        r = call({"op": "test_session", "seconds": float(a[1])})
    elif a[0] == "test_launch_setup":
        r = call({"op": "test_launch_setup", "altitude": float(a[1]), "speed": float(a[2])})
    elif a[0] == "enable":
        r = call({"op": "enable", "on": a[1] in ("on", "1", "true")})
    elif a[0] in ("release", "reset_model", "stop_record"):
        r = call({"op": a[0]})
    elif a[0] == "aim":
        r = call({"op": "aim", "yaw": float(a[1]), "pitch": float(a[2]), "from": a[3] if len(a) > 3 else "nose"})
    elif a[0] == "record":
        r = call({"op": "record", "seconds": float(a[1]), "name": a[2] if len(a) > 2 else None})
    elif a[0] == "config":
        r = call({"op": "config", "key": a[1], "value": parse_value(a[2])} if len(a) > 2 else {"op": "config"})
    elif a[0] == "step":
        secs = float(a[3])
        rec = call({"op": "record", "seconds": secs + 1.0, "name": a[4] if len(a) > 4 else None})
        call({"op": "aim", "yaw": 0, "pitch": 0, "from": "nose"})
        time.sleep(1.0)
        call({"op": "aim", "yaw": float(a[1]), "pitch": float(a[2]), "from": "target"})
        time.sleep(secs)
        r = {"recording": rec, "status": call({"op": "status"})}
    else:
        print(__doc__)
        return 2
    print(json.dumps(r, indent=1))
    return 1 if r.get("ok") is False else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
