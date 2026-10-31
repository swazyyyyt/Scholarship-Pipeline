#ifndef SCHOLARSHIP_NATIVE_H
#define SCHOLARSHIP_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#  define SP_EXPORT __declspec(dllexport)
#else
#  define SP_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Valid input ranges (see README, "Native function contract"). */
#define SP_MIN_SCORE 0
#define SP_MAX_SCORE 200
#define SP_MIN_INCOME 0
#define SP_MAX_INCOME 1000000

/* Error value returned for any input outside the valid ranges. */
#define SP_ERROR_INVALID_INPUT (-1)

/*
 * Monthly stipend in WHOLE currency units (no fractions) for an awarded applicant.
 *   base       = score * 100
 *   supplement = 25 % if monthly_income <= 500, 10 % if monthly_income <= 1000, else 0 %
 *   result     = base + base * supplement / 100     (integer arithmetic, fraction dropped = round down)
 * Returns SP_ERROR_INVALID_INPUT (-1) when score or monthly_income is outside its valid range.
 * Pure function: no global state, no allocation, no pointers.
 */
SP_EXPORT int32_t sp_stipend_amount(int32_t score, int32_t monthly_income);

#ifdef __cplusplus
}
#endif

#endif /* SCHOLARSHIP_NATIVE_H */
