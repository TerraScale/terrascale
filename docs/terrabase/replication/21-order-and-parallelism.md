# 21. Slice plan: Order and parallelism

**Order and parallelism:**

1. R1 ∥ R0, starting now.
2. R2a, once 1b lands.
3. R2b ∥ R2c ∥ R3.
4. R4a, once 3a lands.
5. R10a.
6. R4b ∥ R5 ∥ R6 ∥ R8.
7. R9 ∥ R7 ∥ R11.
8. R12.

The critical path is R1 → R2a → R3 → R4a → R10a → R6 → R11. Opus 9 slices, Haiku 7.
