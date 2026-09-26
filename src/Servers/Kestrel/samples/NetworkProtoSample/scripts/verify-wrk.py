import json
import pathlib
import re
import sys

prefix, mode, seconds = sys.argv[1:]
seconds = int(seconds)
text = pathlib.Path(prefix + ".txt").read_text()
elapsed = float(re.search(r"requests in ([\d.]+)s", text)[1])
monotonic = float(pathlib.Path(prefix + ".end-monotonic").read_text()) - float(pathlib.Path(prefix + ".start-monotonic").read_text())
assert seconds * 0.95 <= elapsed <= seconds + 1, (seconds, elapsed)
assert abs(monotonic - elapsed) < 0.5, f"Clock mismatch: wrk={elapsed}s monotonic={monotonic}s"
match = re.search(r"VERIFY (.+)", text)
assert match, "wrk response verification did not run"
counts = {key: int(value) for key, value in re.findall(r"(\w+)=(\d+)", match[1])}
assert counts["responses"] == counts["requests"] > 0, counts
assert all(counts[key] == 0 for key in ("bad", "connect", "read", "write", "status", "timeout")), counts
# wrk can omit stalled requests from both its completed-latency histogram and
# timeout count at shutdown. Reject large gaps between active samples and the
# full-interval rate instead of accepting those counters alone.
active = re.search(r"Req/Sec\s+([\d.]+)([kM]?)", text)
threads = int(re.search(r"(\d+) threads", text)[1])
aggregate = float(re.search(r"Requests/sec:\s+([\d.]+)", text)[1])
active_rate = float(active[1]) * {"": 1, "k": 1000, "M": 1000000}[active[2]] * threads
assert aggregate >= active_rate * 0.9, f"Possible stalled traffic: interval {aggregate} req/s versus active samples {active_rate} req/s"
before = json.loads(pathlib.Path(prefix + ".before.json").read_text())
after = json.loads(pathlib.Path(prefix + ".after.json").read_text())
delta = {key: after[key] - before[key] for key in ("accepted", "closed", "handshakes", "requests", "reusedRequests")}
assert before["scheme"] == after["scheme"]
if after["scheme"] == "http":
    assert delta["handshakes"] == 0, delta
else:
    assert delta["handshakes"] > 0, delta
assert delta["requests"] >= counts["responses"], delta
if mode == "close":
    assert counts["reused"] == delta["reusedRequests"] == 0, (counts, delta)
    assert delta["accepted"] >= delta["requests"], delta
    if after["scheme"] == "https":
        assert delta["handshakes"] >= delta["requests"], delta
else:
    assert counts["reused"] > 0 and delta["reusedRequests"] > 0, (counts, delta)
    assert delta["requests"] > 10 * delta["accepted"], delta
print(f"{mode} verified: {counts}; server delta={delta}")
if "allocatedBytes" in after:
    allocated = after["allocatedBytes"] - before["allocatedBytes"]
    cpu = after["cpuMilliseconds"] - before["cpuMilliseconds"]
    print(f"Server allocation={allocated / delta['requests']:.1f} B/request; CPU={cpu * 1000 / delta['requests']:.2f} us/request; GC counts={[after[f'gen{i}'] - before[f'gen{i}'] for i in range(3)]}")
print("Metrics probes add one connection; requests completed by server at wrk cutoff may exceed client responses.")
print("Plaintext: no TLS." if after["scheme"] == "http" else "TLS session resumption unverified: fresh TCP connections are NOT asserted to be full certificate handshakes.")
