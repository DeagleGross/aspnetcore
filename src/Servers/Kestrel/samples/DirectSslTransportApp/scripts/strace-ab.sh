#!/usr/bin/env bash
# Compare syscall counts between TlsSession and OpenSslDirect engines under
# persistent (keep-alive) load. Uses `strace -c -f -p <pid>` to attach to the
# already-running dotnet process and summarise syscall counts over a fixed window.
#
# Requires: sudo (for strace -p), wrk, dotnet, the DirectSslTransportApp project.
# Usage:    ./strace-ab.sh [duration_seconds] [threads] [connections] [strace_seconds]
#
# Note: strace adds ~15-30% overhead while attached. We attach for the MIDDLE
# of the load window so warm-up + tear-down aren't counted. RPS numbers in this
# script are NOT comparable to run-ab.sh — strace slows the server down. The
# meaningful output is the *ratio* of syscalls to requests during the strace
# window.

set -euo pipefail

DUR=${1:-30}
THREADS=${2:-4}
CONNS=${3:-200}
STRACE_DUR=${4:-10}        # seconds to keep strace attached
WARMUP=5                   # delay before strace attach
PORT=5001

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_BASE="/tmp/directssl-strace-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT_BASE"

echo "Output:  $OUT_BASE"
echo "App:     $APP_DIR"
echo "Load:    ${DUR}s, $THREADS threads, $CONNS connections"
echo "Strace:  attach at +${WARMUP}s, run for ${STRACE_DUR}s"
echo "Syscalls traced: read, recvfrom, recvmsg, write, sendto, sendmsg, epoll_wait, epoll_ctl"
echo ""

# Require sudo cached — fail fast if user can't run strace
if ! sudo -n true 2>/dev/null; then
  echo "This script needs sudo for strace -p. You may be prompted now."
  sudo -v || { echo "sudo unavailable — aborting"; exit 1; }
fi

SERVER_PID=
STRACE_PID=
WRK_PID=

cleanup() {
  set +e
  [ -n "${WRK_PID:-}" ]    && kill "$WRK_PID"    2>/dev/null
  [ -n "${STRACE_PID:-}" ] && sudo kill "$STRACE_PID" 2>/dev/null
  [ -n "${SERVER_PID:-}" ] && kill "$SERVER_PID" 2>/dev/null
  wait 2>/dev/null
}
trap cleanup EXIT INT TERM

run_one() {
  local engine="$1"
  local label="$engine"
  local srv_log="$OUT_BASE/$label.srv"
  local wrk_log="$OUT_BASE/$label.wrk"
  local strace_log="$OUT_BASE/$label.strace"

  echo "===================================="
  echo "== Engine: $engine"
  echo "===================================="

  # 1. Boot server (suppress pump counter spam if you want, but easier to keep it)
  ( cd "$APP_DIR" && DIRECTSSL_ENGINE="$engine" dotnet run -c Release ) \
      >"$srv_log" 2>&1 &
  SERVER_PID=$!
  echo "  server pid: $SERVER_PID"

  # 2. Wait for the port to open (max 30s)
  for i in $(seq 1 60); do
    if ss -tln "sport = :$PORT" 2>/dev/null | grep -q LISTEN; then break; fi
    sleep 0.5
  done
  if ! ss -tln "sport = :$PORT" 2>/dev/null | grep -q LISTEN; then
    echo "  ERROR: server failed to listen on :$PORT (see $srv_log)"
    kill "$SERVER_PID" 2>/dev/null
    SERVER_PID=
    return 1
  fi
  echo "  server listening on :$PORT"

  # 3. Start wrk under persistent (keep-alive) load in the background
  wrk -t "$THREADS" -c "$CONNS" -d "${DUR}s" --latency \
      "https://localhost:$PORT/" >"$wrk_log" 2>&1 &
  WRK_PID=$!

  # 4. Wait for warmup, then strace -c the server for STRACE_DUR seconds.
  #    -f follows threads.  -c gives a syscall summary at exit (when strace
  #    terminates). We use timeout so strace detaches automatically.
  sleep "$WARMUP"
  echo "  attaching strace for ${STRACE_DUR}s ..."
  sudo timeout -s INT "$STRACE_DUR" \
       strace -c -f -p "$SERVER_PID" \
       -e trace=read,recvfrom,recvmsg,write,sendto,sendmsg,epoll_wait,epoll_pwait,epoll_ctl \
       2>"$strace_log" || true
  echo "  strace detached"

  # 5. Wait for wrk to finish
  wait "$WRK_PID" 2>/dev/null || true
  WRK_PID=

  # 6. Stop server (will print counter dump on shutdown)
  kill -INT "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=
  sleep 1

  echo ""
}

run_one TlsSession
run_one OpenSslDirect

echo "===================================="
echo "== Summary"
echo "===================================="

# Parse wrk Requests/sec
get_rps() { grep -m1 'Requests/sec' "$1" | awk '{print $2}'; }
# Parse strace -c summary; rows look like: "% time     seconds  usecs/call     calls    errors syscall"
get_syscall_count() {
  local file="$1"
  local sc="$2"
  awk -v sc="$sc" '$NF==sc { sum += $(NF-2) } END { printf "%.0f", sum+0 }' "$file"
}

printf "\n%-22s %16s %16s\n" "Metric" "TlsSession" "OpenSslDirect"
printf "%s\n" "------------------------------------------------------------"

TS_RPS=$(get_rps "$OUT_BASE/TlsSession.wrk")
OD_RPS=$(get_rps "$OUT_BASE/OpenSslDirect.wrk")
printf "%-22s %16s %16s\n" "wrk Requests/sec (load-window avg, includes strace slowdown)" "$TS_RPS" "$OD_RPS"
printf "\n%-22s %16s %16s\n" "Syscall (strace window)" "TlsSession" "OpenSslDirect"
printf "%s\n" "------------------------------------------------------------"
for sc in read recvfrom recvmsg write sendto sendmsg epoll_wait epoll_pwait epoll_ctl; do
  TS_C=$(get_syscall_count "$OUT_BASE/TlsSession.strace" "$sc")
  OD_C=$(get_syscall_count "$OUT_BASE/OpenSslDirect.strace" "$sc")
  if [ "${TS_C:-0}" -gt 0 ] || [ "${OD_C:-0}" -gt 0 ]; then
    printf "%-22s %16s %16s\n" "$sc" "$TS_C" "$OD_C"
  fi
done

# Estimate syscalls-per-request *during* the strace window: requests in that
# window ≈ RPS * STRACE_DUR (RPS is window-averaged; close enough for ratio).
estimate_reqs_during_strace() {
  local rps="$1"
  awk -v r="$rps" -v d="$STRACE_DUR" 'BEGIN { printf "%.0f", r*d }'
}
TS_REQ=$(estimate_reqs_during_strace "$TS_RPS")
OD_REQ=$(estimate_reqs_during_strace "$OD_RPS")

printf "\n%-22s %16s %16s\n" "Approx requests in strace window" "$TS_REQ" "$OD_REQ"

# Per-request ratios for the most informative syscalls
ratio() {
  local c="$1"; local r="$2"
  awk -v c="$c" -v r="$r" 'BEGIN { if (r>0) printf "%.2f", c/r; else printf "n/a" }'
}
printf "\n%-22s %16s %16s\n" "Per-request (strace window)" "TlsSession" "OpenSslDirect"
printf "%s\n" "------------------------------------------------------------"
for sc in recvfrom recvmsg read sendto sendmsg write epoll_wait epoll_pwait epoll_ctl; do
  TS_C=$(get_syscall_count "$OUT_BASE/TlsSession.strace" "$sc")
  OD_C=$(get_syscall_count "$OUT_BASE/OpenSslDirect.strace" "$sc")
  if [ "${TS_C:-0}" -gt 0 ] || [ "${OD_C:-0}" -gt 0 ]; then
    printf "%-22s %16s %16s\n" "$sc/req" "$(ratio "$TS_C" "$TS_REQ")" "$(ratio "$OD_C" "$OD_REQ")"
  fi
done

echo ""
echo "Raw logs in: $OUT_BASE"
echo ""
echo "Interpretation:"
echo "  If recvfrom/req is ~1.0 on TlsSession and ~0.1-0.3 on OpenSslDirect,"
echo "  TlsSession is doing one recv() per request while OpenSslDirect's BIO"
echo "  drains multiple records per recv() — that's where the throughput gap"
echo "  comes from. Look at the per-request column above."
