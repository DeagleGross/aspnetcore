import json
import pathlib
import re
import statistics
import sys


def microseconds(value):
    match = re.fullmatch(r"([\d.]+)(us|ms|s)", value)
    return float(match[1]) * {"us": 1, "ms": 1000, "s": 1000000}[match[2]]


def cpu_values(path):
    values = {}
    in_cpu = False
    for line in path.read_text().splitlines():
        if not line.startswith("Average:"):
            continue
        fields = line.split()
        if "UID" in fields:
            in_cpu = "%CPU" in fields
        elif in_cpu:
            name = " ".join(fields[10:])
            if name in ("dotnet", "wrk") or "NetworkProto io" in name:
                values[name] = float(fields[8])
    return values


rows = []
for argument in sys.argv[1:]:
    directory = pathlib.Path(argument)
    for path in sorted(directory.glob("*-measured.txt")):
        text = path.read_text()
        prefix = str(path)[:-4]
        before = json.loads(pathlib.Path(prefix + ".before.json").read_text())
        after = json.loads(pathlib.Path(prefix + ".after.json").read_text())
        row = {
            "directory": directory.name,
            "run": path.stem,
            "backend": after["backend"],
            "scheme": after.get("scheme", "https"),
            "mode": "keepalive" if "keepalive" in path.name else "close",
            "rps": float(re.search(r"Requests/sec:\s+([\d.]+)", text)[1]),
            "latencyMeanUs": microseconds(re.search(r"Latency\s+(\S+)", text)[1]),
            "latencyP50Us": microseconds(re.search(r"^\s+50%\s+(\S+)", text, re.MULTILINE)[1]),
            "latencyP99Us": microseconds(re.search(r"^\s+99%\s+(\S+)", text, re.MULTILINE)[1]),
        }
        requests = after["requests"] - before["requests"]
        if "allocatedBytes" in after:
            row["allocatedBytesPerRequest"] = (after["allocatedBytes"] - before["allocatedBytes"]) / requests
            row["cpuUsPerRequest"] = (after["cpuMilliseconds"] - before["cpuMilliseconds"]) * 1000 / requests
        cpu = pathlib.Path(prefix + ".pidstat.txt")
        if cpu.exists():
            row["cpuPercent"] = cpu_values(cpu)
        rows.append(row)

print(json.dumps(rows, indent=2))
for key in sorted({(row["directory"], row["backend"], row["mode"]) for row in rows}):
    rates = [row["rps"] for row in rows if (row["directory"], row["backend"], row["mode"]) == key]
    cv = statistics.stdev(rates) / statistics.mean(rates) * 100 if len(rates) > 1 else 0
    print(f"{key}: median={statistics.median(rates):.2f}; range={min(rates):.2f}..{max(rates):.2f}; CV={cv:.2f}%; n={len(rates)}", file=sys.stderr)
