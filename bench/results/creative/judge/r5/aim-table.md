# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| aim | a7-aim3 | 24 | 2.625 | 0.542 |
| aim | a7-aim5 | 24 | 2.708 | 0.417 |
| aim | a7-greedy | 24 | 2.542 | 0.542 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| aim | a7-aim3 - a7-aim5 | 24 | -0.083 | 4 | 5 |
| aim | a7-aim3 - a7-greedy | 24 | 0.083 | 7 | 4 |
| aim | a7-aim5 - a7-greedy | 24 | 0.167 | 6 | 1 |
