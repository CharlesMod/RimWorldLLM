# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| recap | r2base | 18 | 2.556 | 0.444 |
| recap | r2bo4auto | 18 | 3.111 | 0.0 |
| recap | r2bo4llm | 18 | 2.944 | 0.222 |
| recap | r2cliche | 18 | 3.0 | 0.056 |
| recap | r2ground | 18 | 2.833 | 0.222 |
| recap | r2plan | 18 | 2.944 | 0.111 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| recap | r2base - r2bo4auto | 18 | -0.556 | 0 | 8 |
| recap | r2base - r2bo4llm | 18 | -0.389 | 3 | 9 |
| recap | r2base - r2cliche | 18 | -0.444 | 1 | 8 |
| recap | r2base - r2ground | 18 | -0.278 | 2 | 6 |
| recap | r2base - r2plan | 18 | -0.389 | 1 | 8 |
| recap | r2bo4auto - r2bo4llm | 18 | 0.167 | 6 | 3 |
| recap | r2bo4auto - r2cliche | 18 | 0.111 | 3 | 1 |
| recap | r2bo4auto - r2ground | 18 | 0.278 | 6 | 1 |
| recap | r2bo4auto - r2plan | 18 | 0.167 | 4 | 2 |
| recap | r2bo4llm - r2cliche | 18 | -0.056 | 2 | 3 |
| recap | r2bo4llm - r2ground | 18 | 0.111 | 5 | 4 |
| recap | r2bo4llm - r2plan | 18 | 0.0 | 4 | 5 |
| recap | r2cliche - r2ground | 18 | 0.167 | 4 | 2 |
| recap | r2cliche - r2plan | 18 | 0.056 | 3 | 3 |
| recap | r2ground - r2plan | 18 | -0.111 | 2 | 6 |
