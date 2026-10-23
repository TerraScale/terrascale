# Local replication baseline

Host: `mariogk-dev`; profile: `loopback`; commit: `e9ce5587fc9b561f2cd1408d3d019129eda94c22`; TerraBase source tree: `5e4c6be4945bfb98fecad878e7d0bfaed6fade99`.

Started: 2026-10-23T11:58:32Z. Repetitions: 3; source documents per write/feed phase: 1000.

Two serving regions (gru/sjc), two authenticated OS processes, real SQLite commits and gRPC peers through loopback proxies. Each repetition uses a fresh cluster: 16 concurrent individual PUTs; seed 20261022; document mix 256 B 70%, 1 KiB 25%, 8 KiB 5%. Every loaded document is hash-checked on both replicas. The existing test bootstrap also places fra, but no fra process or alternate confirming region exists.

Median [minimum, maximum] across repetitions. Spread is the observed full range, not a confidence interval. Percentiles are per-request within a repetition; table medians do not pool histograms.

| Phase | Metric | Repeats | Median | Min | Max |
| --- | --- | ---: | ---: | ---: | ---: |
| second-region | apply_cleanup.document.count | 3 | 1000.0000 | 1000.0000 | 1000.0000 |
| second-region | apply_cleanup.document.max_ms | 3 | 43.5443 | 23.1321 | 49.8337 |
| second-region | apply_cleanup.document.p50_ms | 3 | 0.5457 | 0.2760 | 0.7278 |
| second-region | apply_cleanup.document.p99_ms | 3 | 17.6326 | 12.2538 | 22.6115 |
| second-region | commit_to_apply.document.count | 3 | 1000.0000 | 1000.0000 | 1000.0000 |
| second-region | commit_to_apply.document.max_ms | 3 | 1660.8334 | 509.4955 | 3802.9474 |
| second-region | commit_to_apply.document.p50_ms | 3 | 154.2634 | 147.0650 | 201.3485 |
| second-region | commit_to_apply.document.p99_ms | 3 | 1660.8334 | 410.9862 | 2902.6933 |
| second-region | commit_to_apply.group.count | 3 | 202.0000 | 200.0000 | 203.0000 |
| second-region | commit_to_apply.group.max_ms | 3 | 1660.8334 | 509.4955 | 3802.9474 |
| second-region | commit_to_apply.group.p50_ms | 3 | 152.9145 | 133.5412 | 210.8625 |
| second-region | commit_to_apply.group.p99_ms | 3 | 1507.8304 | 410.9862 | 2902.6933 |
| second-region | commit_to_sink_entry.document.count | 3 | 1000.0000 | 1000.0000 | 1000.0000 |
| second-region | commit_to_sink_entry.document.max_ms | 3 | 1149.9731 | 295.7602 | 1233.7968 |
| second-region | commit_to_sink_entry.document.p50_ms | 3 | 43.0254 | 33.4952 | 57.5431 |
| second-region | commit_to_sink_entry.document.p99_ms | 3 | 856.1904 | 257.0322 | 1011.1397 |
| second-region | gru->sjc.visibility_max_ms | 3 | 3995.6470 | 632.8310 | 4853.7590 |
| second-region | gru->sjc.visibility_p50_ms | 3 | 371.9670 | 337.6630 | 540.6710 |
| second-region | gru->sjc.visibility_p99_ms | 3 | 2422.7830 | 610.3030 | 4102.1430 |
| second-region | gru.allocated_bytes_per_merged_group | 3 | 7852316.9825 | 6343440.4040 | 8148195.5269 |
| second-region | gru.allocated_bytes_per_source_doc | 3 | 757782.1840 | 628000.6000 | 895164.1360 |
| second-region | gru.cpu_ms_per_merged_group | 3 | 444.3125 | 350.8690 | 455.8072 |
| second-region | gru.cpu_ms_per_source_doc | 3 | 41.3211 | 34.7360 | 51.9620 |
| second-region | gru.feed_selects_per_source_doc | 3 | 2.3210 | 2.3060 | 2.3440 |
| second-region | gru.merged_groups | 3 | 99.0000 | 93.0000 | 114.0000 |
| second-region | gru.partition_refusal_ms | 3 | 9.3920 | 4.5060 | 18.8750 |
| second-region | gru.refused_without_execution | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | gru.remote_durable_probe | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | gru.unknown_copy_present | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | local_ack.capped | 3 | 0.0000 | 0.0000 | 0.0000 |
| second-region | local_ack.count | 3 | 1062.0000 | 1061.0000 | 1063.0000 |
| second-region | local_ack.p50_ms | 3 | 171.3000 | 159.4000 | 249.3000 |
| second-region | local_ack.p99_ms | 3 | 1143.5000 | 428.4000 | 4446.4000 |
| second-region | marker_identification.count | 3 | 202.0000 | 200.0000 | 203.0000 |
| second-region | marker_identification.max_ms | 3 | 0.7778 | 0.7539 | 21.7578 |
| second-region | marker_identification.p50_ms | 3 | 0.1617 | 0.1423 | 0.1794 |
| second-region | marker_identification.p99_ms | 3 | 0.5481 | 0.3683 | 3.0781 |
| second-region | partition.confirmation_resumed_ms | 3 | 989.7670 | 976.8010 | 1111.0360 |
| second-region | partition.post_commit_response_ms | 3 | 89.2140 | 52.5280 | 132.3390 |
| second-region | partition.unknown | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | readers.admitted | 3 | 0.0000 | 0.0000 | 0.0000 |
| second-region | readers.rejected | 3 | 0.0000 | 0.0000 | 0.0000 |
| second-region | readers.requested | 3 | 0.0000 | 0.0000 | 0.0000 |
| second-region | remote_ack.p50_ms | 3 | 371.4550 | 370.1750 | 593.9190 |
| second-region | remote_ack.p99_ms | 3 | 2539.5190 | 636.9270 | 4845.5670 |
| second-region | sink_apply.document.count | 3 | 1000.0000 | 1000.0000 | 1000.0000 |
| second-region | sink_apply.document.max_ms | 3 | 1121.5628 | 332.5263 | 2866.0926 |
| second-region | sink_apply.document.p50_ms | 3 | 89.5396 | 79.2170 | 125.9036 |
| second-region | sink_apply.document.p99_ms | 3 | 1121.5628 | 332.5263 | 2775.3953 |
| second-region | sjc->gru.visibility_max_ms | 3 | 3989.5030 | 511.9990 | 4698.1110 |
| second-region | sjc->gru.visibility_p50_ms | 3 | 216.8310 | 195.3270 | 259.4550 |
| second-region | sjc->gru.visibility_p99_ms | 3 | 1286.1430 | 452.8630 | 3747.8390 |
| second-region | sjc.allocated_bytes_per_merged_group | 3 | 2681455.8095 | 2203509.6114 | 3164106.3379 |
| second-region | sjc.allocated_bytes_per_source_doc | 3 | 563105.7200 | 464940.5280 | 692939.2880 |
| second-region | sjc.cpu_ms_per_merged_group | 3 | 163.0070 | 142.3622 | 209.9338 |
| second-region | sjc.cpu_ms_per_source_doc | 3 | 34.2315 | 30.0384 | 45.9755 |
| second-region | sjc.feed_selects_per_source_doc | 3 | 2.3160 | 2.3040 | 2.3420 |
| second-region | sjc.merged_groups | 3 | 211.0000 | 210.0000 | 219.0000 |
| second-region | sjc.partition_refusal_ms | 3 | 12.1480 | 5.1500 | 82.9730 |
| second-region | sjc.refused_without_execution | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | sjc.remote_durable_probe | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | sjc.unknown_copy_present | 3 | 1.0000 | 1.0000 | 1.0000 |
| second-region | throughput.docs_per_second | 3 | 27.1902 | 16.9389 | 42.2012 |
| second-region | wire.transport_stream_bytes_per_source_doc | 3 | 1017.1830 | 1014.5950 | 1077.5040 |

Local ACK measures server request dispatch through local durable commit, before remote confirmation starts. Remote ACK measures full HTTP request latency, including admission, local commit, replication, receiver commit and response. Dispatch-to-visible probes include polling and GET latency. None isolates fsync or WAN latency. See JSON for retries and all sample counts; no histogram pooling.

Commit-to-apply pairs each source CDC commit marker at the existing post-native-COMMIT checkpoint with that exact group's receiver post-native-FULL-COMMIT hook. Same-boot, same-time-namespace Linux CLOCK_MONOTONIC nanoseconds only; no wall-clock/HLC subtraction. All workload documents must match once. Document percentiles weight every document; group percentiles weight each commit once. Receiver transaction chunks are correlated before releasing their exclusive writer. Apply cleanup until sink return is reported separately; ACK return is excluded. Source marker-identification SELECT overhead remains inside lag and is reported separately. Exact timestamps and IDs are retained in JSON. Percentiles use nearest rank; component percentiles must not be added or subtracted.

Partition probes send each PUT once. After both-direction remote_durable probes, a test-only receiver gate holds ACK delivery after real apply commits. Both peer proxies are suspended; the pending write must answer 503 write_outcome_unknown without Retry-After, and its exact value must remain on both replicas. Both isolated regions must refuse new writes as 503 remote_region_unavailable with Retry-After, with no document on either replica. Healing must restore remote_durable success; only explicit pre-execution refusals are retried during healing. Gate/control files and tokens remain private and are never published.

This is local process evidence, not a physical power-loss experiment, WAN latency result, backup-commit proof or production guarantee. No shared network settings or production resources change.

```text
driver=go1.27.3 cpus=46
uname -srmo
Linux 7.3.0 x86_64 GNU/Linux
lscpu
Architecture:                            x86_64
CPU op-mode(s):                          32-bit, 64-bit
Address sizes:                           46 bits physical, 48 bits virtual
Byte Order:                              Little Endian
CPU(s):                                  46
On-line CPU(s) list:                     0-45
Vendor ID:                               GenuineIntel
Model name:                              Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz
CPU family:                              6
Model:                                   79
Thread(s) per core:                      1
Core(s) per socket:                      46
Socket(s):                               1
Stepping:                                1
Microcode version:                       0xb000040
BogoMIPS:                                3990.75
Flags:                                   fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon rep_good nopl xtopology nonstop_tsc cpuid tsc_known_freq pni pclmulqdq vmx ssse3 fma cx16 pdcm pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch cpuid_fault pti ssbd ibrs ibpb stibp tpr_shadow ept vpid ept_ad fsgsbase tsc_adjust bmi1 hle avx2 smep bmi2 erms invpcid rtm rdseed adx smap xsaveopt arat vnmi umip md_clear flush_l1d arch_capabilities
Virtualization:                          VT-x
L1d cache:                               1.4 MiB (46 instances)
L1i cache:                               1.4 MiB (46 instances)
L2 cache:                                184 MiB (46 instances)
L3 cache:                                16 MiB (1 instance)
NUMA node(s):                            2
NUMA node0 CPU(s):                       0-27
NUMA node1 CPU(s):                       28-45
Vulnerability Gather data sampling:      Not affected
Vulnerability Ghostwrite:                Not affected
Vulnerability Indirect target selection: Not affected
Vulnerability Itlb multihit:             Not affected
Vulnerability L1tf:                      Mitigation; PTE Inversion; VMX conditional cache flushes, SMT disabled
Vulnerability Mds:                       Mitigation; Clear CPU buffers; SMT Host state unknown
Vulnerability Meltdown:                  Mitigation; PTI
Vulnerability Mmio stale data:           Mitigation; Clear CPU buffers; SMT Host state unknown
Vulnerability Old microcode:             Not affected
Vulnerability Reg file data sampling:    Not affected
Vulnerability Retbleed:                  Not affected
Vulnerability Spec rstack overflow:      Not affected
Vulnerability Spec store bypass:         Mitigation; Speculative Store Bypass disabled via prctl
Vulnerability Spectre v1:                Mitigation; usercopy/swapgs barriers and __user pointer sanitization
Vulnerability Spectre v2:                Mitigation; Retpolines; IBPB conditional; IBRS_FW; STIBP disabled; RSB filling; PBRSB-eIBRS Not affected; BHI Retpoline
Vulnerability Srbds:                     Not affected
Vulnerability Tsa:                       Not affected
Vulnerability Tsx async abort:           Mitigation; Clear CPU buffers; SMT Host state unknown
Vulnerability Vmscape:                   Not affected
dotnet --version
11.0.100-rc.1.26425.128
git status --porcelain -- src tests/TerraBase.TestHarness
```
