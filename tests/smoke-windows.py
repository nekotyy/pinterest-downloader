import subprocess
import time
import urllib.error
import urllib.request


def request(origin):
    data = urllib.request.Request(
        "http://127.0.0.1:17849/pins", data=b"[]", method="POST",
        headers={"Origin": origin, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(data, timeout=1) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


app = subprocess.Popen(["dist/app/PinterestDownload.exe"])
try:
    for _ in range(30):
        try:
            if request("chrome-extension://alenpleailakepgbibnkkmhcmkoijmch") == 204:
                break
        except (OSError, urllib.error.URLError):
            pass
        if app.poll() is not None:
            raise RuntimeError(f"Application exited with code {app.returncode}")
        time.sleep(1)
    else:
        raise RuntimeError("Application did not accept the browser connection")
    assert request("https://example.com") == 403
    assert request("moz-extension://12345678-1234-1234-1234-123456789abc") == 204
    print("Windows startup and browser connections: OK")
finally:
    app.terminate()
    app.wait(timeout=10)
