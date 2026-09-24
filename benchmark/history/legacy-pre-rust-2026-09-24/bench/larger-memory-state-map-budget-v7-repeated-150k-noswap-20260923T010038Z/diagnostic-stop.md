# Repeated payload diagnostic stopped

- Command: systemd-run --user --wait --collect -p MemoryMax=268435456 -p MemorySwapMax=0 -- nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 150000 --payload-size 2048 --payload-mode repeated --samples 5 --operations 20 --writes 20 --warmup 100
- Result: stopped manually after 9m27.657s wall time / 11m05.406s CPU; no benchmark receipt was emitted.
- cgroup memory peak: 256 MiB; swap peak: 0 B.
- Last observed storage directory size: 74 MiB; WAL reached 64 MiB.
- The repeated payload is a diagnostic fixture and was stopped before completion; use the per-key incompressible fixture for qualification.
