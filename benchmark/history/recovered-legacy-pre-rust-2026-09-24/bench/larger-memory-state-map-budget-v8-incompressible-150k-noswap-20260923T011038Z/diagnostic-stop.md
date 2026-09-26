# Incompressible 150k diagnostic stopped

- Command: systemd-run --user --wait --collect -p MemoryMax=268435456 -p MemorySwapMax=0 -- nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 150000 --payload-size 2048 --payload-mode per-key-incompressible --samples 5 --operations 20 --writes 20 --warmup 100
- Result: stopped manually during seeding before receipt emission.
- Last observed output size: 695 MiB; WAL reached 256 MiB.
- cgroup peak: 256 MiB; swap peak: 0 B; memory.events showed oom=0 and oom_kill=0 while active.
- This diagnostic confirms disk spill beyond the memory cap; the 40k x 8 KiB gate is the bounded qualification run.
