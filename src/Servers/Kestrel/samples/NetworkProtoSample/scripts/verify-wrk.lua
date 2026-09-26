local threads = {}
local expected_body = string.rep("x", 1023) .. "\n"

function setup(thread)
    table.insert(threads, thread)
end

function init(args)
    mode = args[1]
    backend = args[2]
    scheme = args[3]
    bad = 0
    seen = 0
    reused = 0
    if mode == "close" then
        wrk.headers["Connection"] = "close"
    end
end

function response(status, headers, body)
    seen = seen + 1
    local number = tonumber(headers["X-Connection-Request"])
    local id = headers["X-Connection-Id"] or ""
    if number and number > 1 then reused = reused + 1 end
    if status ~= 200 or body ~= expected_body or headers["X-Backend"] ~= backend
        or headers["X-Tls"] ~= (scheme == "http" and "none" or "Tls12:TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256")
        or not number or ((string.sub(id, 1, 9) == "io-uring-") ~= (backend == "io_uring"))
        or (mode == "close" and (number ~= 1 or headers["Connection"] ~= "close")) then
        bad = bad + 1
    end
end

function done(summary, latency, requests)
    local count, failures, reuse = 0, 0, 0
    for _, thread in ipairs(threads) do
        count = count + thread:get("seen")
        failures = failures + thread:get("bad")
        reuse = reuse + thread:get("reused")
    end
    print(string.format("VERIFY responses=%d bad=%d reused=%d requests=%d connect=%d read=%d write=%d status=%d timeout=%d",
        count, failures, reuse, summary.requests, summary.errors.connect, summary.errors.read,
        summary.errors.write, summary.errors.status, summary.errors.timeout))
end
