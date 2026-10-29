#ifndef TS_SQL_H
#define TS_SQL_H
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
uint32_t ts_sql_abi_version(void);
int32_t ts_sql_next(const uint8_t *sql, size_t len, size_t offset, uint8_t **out, size_t *out_len);
void ts_sql_free(uint8_t *out, size_t len);
#ifdef __cplusplus
}
#endif
#endif
