# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| eulogy | r2base | 12 | 2.167 | 0.833 |
| eulogy | r2bo4auto | 12 | 2.667 | 0.5 |
| eulogy | r2bo4llm | 12 | 2.583 | 0.5 |
| eulogy | r2cliche | 12 | 2.25 | 0.75 |
| eulogy | r2ground | 12 | 2.417 | 0.667 |
| eulogy | r2plan | 11 | 3.182 | 0.273 |
| vows | r2base | 12 | 2.5 | 0.5 |
| vows | r2bo4auto | 12 | 2.5 | 0.5 |
| vows | r2bo4llm | 12 | 2.5 | 0.5 |
| vows | r2cliche | 12 | 2.333 | 0.667 |
| vows | r2ground | 12 | 2.333 | 0.667 |
| vows | r2plan | 12 | 2.25 | 0.667 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| eulogy | r2base - r2bo4auto | 12 | -0.5 | 2 | 6 |
| eulogy | r2base - r2bo4llm | 12 | -0.417 | 2 | 6 |
| eulogy | r2base - r2cliche | 12 | -0.083 | 3 | 3 |
| eulogy | r2base - r2ground | 12 | -0.25 | 1 | 5 |
| eulogy | r2base - r2plan | 11 | -1.0 | 1 | 8 |
| eulogy | r2bo4auto - r2bo4llm | 12 | 0.083 | 5 | 3 |
| eulogy | r2bo4auto - r2cliche | 12 | 0.417 | 5 | 1 |
| eulogy | r2bo4auto - r2ground | 12 | 0.25 | 4 | 3 |
| eulogy | r2bo4auto - r2plan | 11 | -0.545 | 2 | 6 |
| eulogy | r2bo4llm - r2cliche | 12 | 0.333 | 6 | 4 |
| eulogy | r2bo4llm - r2ground | 12 | 0.167 | 4 | 3 |
| eulogy | r2bo4llm - r2plan | 11 | -0.636 | 1 | 5 |
| eulogy | r2cliche - r2ground | 12 | -0.167 | 3 | 4 |
| eulogy | r2cliche - r2plan | 11 | -0.909 | 1 | 7 |
| eulogy | r2ground - r2plan | 11 | -0.818 | 2 | 7 |
| vows | r2base - r2bo4auto | 12 | 0.0 | 3 | 3 |
| vows | r2base - r2bo4llm | 12 | 0.0 | 3 | 4 |
| vows | r2base - r2cliche | 12 | 0.167 | 4 | 2 |
| vows | r2base - r2ground | 12 | 0.167 | 3 | 1 |
| vows | r2base - r2plan | 12 | 0.25 | 2 | 0 |
| vows | r2bo4auto - r2bo4llm | 12 | 0.0 | 4 | 4 |
| vows | r2bo4auto - r2cliche | 12 | 0.167 | 6 | 4 |
| vows | r2bo4auto - r2ground | 12 | 0.167 | 3 | 1 |
| vows | r2bo4auto - r2plan | 12 | 0.25 | 4 | 2 |
| vows | r2bo4llm - r2cliche | 12 | 0.167 | 4 | 3 |
| vows | r2bo4llm - r2ground | 12 | 0.167 | 5 | 3 |
| vows | r2bo4llm - r2plan | 12 | 0.25 | 5 | 3 |
| vows | r2cliche - r2ground | 12 | 0.0 | 3 | 3 |
| vows | r2cliche - r2plan | 12 | 0.083 | 4 | 3 |
| vows | r2ground - r2plan | 12 | 0.083 | 3 | 3 |
