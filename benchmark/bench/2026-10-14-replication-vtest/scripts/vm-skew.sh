# Runs inside a ts-vtest-tb machine through vm-run.sh: steps the wall clock by $1 milliseconds (relative to the current
# time, nanoseconds kept) and prints the clock before and after. The VM runs no chronyd/ntpd that would undo it.
now=$(date +%s%N)
new=$((now + $1 * 1000000))
date -s "@$((new / 1000000000)).$(printf %09d $((new % 1000000000)))" > /dev/null || { echo "date -s failed: exit $?"; exit 1; }
echo "skewed $1 ms: before $now after $(date +%s%N)"
