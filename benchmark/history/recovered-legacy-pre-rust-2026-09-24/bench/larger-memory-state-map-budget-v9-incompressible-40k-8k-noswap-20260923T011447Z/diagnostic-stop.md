# Incompressible 40k x 8 KiB diagnostic stopped before receipt

- Command: systemd-run --user --wait --collect -p MemoryMax=268435456 -p MemorySwapMax=0 -- nice -n 19 dotnet /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 40000 --payload-size 8192 --payload-mode per-key-incompressible --samples 5 --operations 20 --writes 20 --warmup 100
- Result: stopped during first sample compaction after 16m28.073s wall / 17m56.698s CPU; no raw sample line or receipt was emitted.
- Final cgroup snapshot: memory.max=268435456, memory.peak=268435456, memory.swap.max=0, oom=0, oom_kill=0.
- Final observed storage directory size: 1.1 GiB; WAL was 384 MiB.
- Live stacks showed LZ4 decompression in CompressedFileRandomAccessDevice.ReadBlock from StorageCompactionCurrentRecordIndex.Add.
- Use the pending compaction fastpath before retrying this qualification.
