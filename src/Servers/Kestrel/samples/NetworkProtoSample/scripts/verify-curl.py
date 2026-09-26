import json
import pathlib
import sys

prefix, backend, mode, scheme = sys.argv[1:]
transfers = [json.loads(line) for line in pathlib.Path(prefix + ".transfers.jsonl").read_text().splitlines()]
headers = []
for block in pathlib.Path(prefix + ".headers").read_text().strip().split("\n\n"):
    lines = block.splitlines()
    assert lines[0] == "HTTP/1.1 200 OK", lines
    headers.append(dict(line.split(": ", 1) for line in lines[1:]))
expected = 1 if mode == "single" else 3
assert len(headers) == len(transfers) == expected
ids = []
for index, (response, transfer) in enumerate(zip(headers, transfers), 1):
    assert transfer["http_code"] == 200 and transfer["exitcode"] == 0, transfer
    assert transfer["ssl_verify_result"] == 0, transfer
    assert response["X-Backend"] == backend, response
    assert transfer["scheme"].lower() == scheme, transfer
    assert response["X-Tls"] == ("none" if scheme == "http" else "Tls12:TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256"), response
    assert pathlib.Path(f"{prefix}-{index}.body").read_bytes() == b"x" * 1023 + b"\n"
    assert response["X-Connection-Id"].startswith("io-uring-") == (backend == "io_uring")
    ids.append(response["X-Connection-Id"])
    count = index if mode == "keepalive" else 1
    assert int(response["X-Connection-Request"]) == count, response
    expected_connects = int(index == 1 or mode != "keepalive")
    assert transfer["num_connects"] == expected_connects, transfer
    if mode == "close":
        assert response["Connection"].lower() == "close", response
assert len(set(ids)) == (expected if mode == "close" else 1), ids
print(f"{backend} {mode}: {expected} {scheme} 200 responses, connection IDs {ids}; TLS={'none' if scheme == 'http' else '1.2 ECDHE-RSA-AES128-GCM-SHA256'}")
