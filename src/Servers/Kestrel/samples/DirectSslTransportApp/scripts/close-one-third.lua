-- wrk lua script: sends `Connection: close` on roughly 1 of every 3 requests.
-- Mixed workload — partial connection churn, partial keep-alive — matches the
-- "Close 1/3" scenario in the reference benchmark.

local i = 0
request = function()
    i = i + 1
    if (i % 3) == 0 then
        return wrk.format("GET", "/", {["Connection"] = "close"})
    else
        return wrk.format("GET", "/")
    end
end
