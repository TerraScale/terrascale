import json
import re
import time
import urllib.request

previous = None
while True:
    try:
        with urllib.request.urlopen("http://127.0.0.1:9091/metrics", timeout=2) as response:
            text = response.read().decode()
        cpu = sum(float(value) for value in re.findall(r"^dotnet_process_cpu_time_seconds_total\{[^\n]*\} ([0-9.eE+-]+)$", text, re.M))
        count = re.search(r"^dotnet_process_cpu_count\{[^\n]*\} ([0-9.eE+-]+)$", text, re.M)
        if count is None or not cpu:
            raise ValueError()
        cores = float(count.group(1))
        now = time.monotonic()
        if previous is not None:
            elapsed, used = now - previous[0], cpu - previous[1]
            if used < 0:
                raise ValueError()
            print(json.dumps({"at": time.time(), "cores": cores, "cpu_percent": 100 * used / elapsed / cores, "core_percent": 100 * used / elapsed}), flush=True)
        previous = now, cpu
    except (OSError, ValueError):
        print(json.dumps({"error": "CPU telemetry unavailable or process restarted"}), flush=True)
        break
    time.sleep(1)
