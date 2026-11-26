# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| art | r2base | 24 | 3.042 | 0.083 |
| art | r2bo4auto | 24 | 2.833 | 0.208 |
| art | r2bo4llm | 24 | 2.917 | 0.125 |
| art | r2cliche | 24 | 2.75 | 0.25 |
| art | r2ground | 24 | 2.792 | 0.208 |
| art | r2plan | 24 | 2.917 | 0.083 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| art | r2base - r2bo4auto | 24 | 0.208 | 6 | 2 |
| art | r2base - r2bo4llm | 24 | 0.125 | 5 | 2 |
| art | r2base - r2cliche | 24 | 0.292 | 8 | 1 |
| art | r2base - r2ground | 24 | 0.25 | 7 | 1 |
| art | r2base - r2plan | 24 | 0.125 | 3 | 0 |
| art | r2bo4auto - r2bo4llm | 24 | -0.083 | 3 | 5 |
| art | r2bo4auto - r2cliche | 24 | 0.083 | 6 | 5 |
| art | r2bo4auto - r2ground | 24 | 0.042 | 5 | 4 |
| art | r2bo4auto - r2plan | 24 | -0.083 | 3 | 5 |
| art | r2bo4llm - r2cliche | 24 | 0.167 | 6 | 3 |
| art | r2bo4llm - r2ground | 24 | 0.125 | 5 | 3 |
| art | r2bo4llm - r2plan | 24 | 0.0 | 2 | 3 |
| art | r2cliche - r2ground | 24 | -0.042 | 3 | 4 |
| art | r2cliche - r2plan | 24 | -0.167 | 1 | 5 |
| art | r2ground - r2plan | 24 | -0.125 | 1 | 4 |
