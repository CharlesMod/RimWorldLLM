# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| art | r2base | 24 | 2.917 | 0.167 |
| art | r2ground | 24 | 2.667 | 0.375 |
| art | r3artv2 | 24 | 2.875 | 0.208 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| art | r2base - r2ground | 24 | 0.25 | 10 | 3 |
| art | r2base - r3artv2 | 24 | 0.042 | 6 | 5 |
| art | r2ground - r3artv2 | 24 | -0.208 | 3 | 7 |
