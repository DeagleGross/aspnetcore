import pathlib
import re
import sys

prefix, connections = sys.argv[1:]
connections = int(connections)
text = pathlib.Path(prefix + "-server.log").read_text()
assert not re.search(r"fail:|crit:|Unhandled exception|completion pump failed", text), text
resets = re.findall(r"io_uring peer reset (\S+) errno=104 time_ms=(\d+)", text)
assert text.count("warn:") == len(resets), "Unclassified server warning"
windows = []
for phase in ("warmup", "measured"):
    end = int(pathlib.Path(prefix + f"-{phase}.end-ms").read_text())
    windows.append((end - 250, end + 250))
phase_counts = [0, 0]
ids = set()
for connection_id, timestamp in resets:
    timestamp = int(timestamp)
    assert connection_id not in ids, f"Duplicate reset accounting: {connection_id}"
    ids.add(connection_id)
    matches = [index for index, (begin, end) in enumerate(windows) if begin <= timestamp <= end]
    assert len(matches) == 1, f"Peer reset outside wrk termination window: {connection_id}, {timestamp}, {windows}"
    phase_counts[matches[0]] += 1
assert all(count <= connections for count in phase_counts), phase_counts
print(f"Server unexpected errors=0; peer resets warmup={phase_counts[0]}, measured={phase_counts[1]}.")
print("Every logged ECONNRESET is within 250 ms of wrk process exit, at most one per remaining client connection.")
print("Timing/count is a screening heuristic, not proof of cause. A separate real-client SO_LINGER/RST check verifies this exact errno path; warnings remain explicit.")
