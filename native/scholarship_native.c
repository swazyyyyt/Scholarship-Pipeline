#include "scholarship_native.h"

int32_t sp_stipend_amount(int32_t score, int32_t monthly_income)
{
    if (score < SP_MIN_SCORE || score > SP_MAX_SCORE ||
        monthly_income < SP_MIN_INCOME || monthly_income > SP_MAX_INCOME)
    {
        return SP_ERROR_INVALID_INPUT;
    }

    int32_t supplement_percent = 0;
    if (monthly_income <= 500)       supplement_percent = 25;
    else if (monthly_income <= 1000) supplement_percent = 10;

    /* max: 200 * 100 = 20000; 20000 * 125 = 2 500 000 -> fits easily in int32_t */
    int32_t base = score * 100;
    return base * (100 + supplement_percent) / 100;
}
