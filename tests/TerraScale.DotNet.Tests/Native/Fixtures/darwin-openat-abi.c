// Cross-compile with clang --target=arm64-apple-macos11 -O2 -S.
// Both callers must place the promoted mode value in the first stack slot.
typedef __UINTPTR_TYPE__ uintptr_t;
typedef __UINT64_TYPE__ uint64_t;

extern int openat(int directory, const char *path, int flags, ...);
extern int fixed_model(int directory, const char *path, int flags,
                       uintptr_t x3, uintptr_t x4, uintptr_t x5,
                       uintptr_t x6, uintptr_t x7, uint64_t mode);

int c_variadic_call(int directory, const char *path, int flags, unsigned mode) {
  return openat(directory, path, flags, mode);
}

int fixed_signature_call(int directory, const char *path, int flags,
                         unsigned mode) {
  return fixed_model(directory, path, flags, 0, 0, 0, 0, 0, mode);
}
