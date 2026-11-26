# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| aim | base | 12 | 2.417 | 0.583 |
| aim | bo4auto | 12 | 3.333 | 0.083 |
| aim | bo4llm | 12 | 3.083 | 0.167 |
| aim | cliche | 12 | 2.917 | 0.25 |
| aim | ground | 12 | 3.0 | 0.25 |
| aim | namefix | 12 | 2.5 | 0.417 |
| aim | plan | 12 | 3.083 | 0.083 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| aim | base - bo4auto | 12 | -0.917 | 0 | 7 |
| aim | base - bo4llm | 12 | -0.667 | 0 | 6 |
| aim | base - cliche | 12 | -0.5 | 0 | 5 |
| aim | base - ground | 12 | -0.583 | 2 | 7 |
| aim | base - namefix | 12 | -0.083 | 2 | 3 |
| aim | base - plan | 12 | -0.667 | 0 | 7 |
| aim | bo4auto - bo4llm | 12 | 0.25 | 3 | 1 |
| aim | bo4auto - cliche | 12 | 0.417 | 3 | 0 |
| aim | bo4auto - ground | 12 | 0.333 | 5 | 2 |
| aim | bo4auto - namefix | 12 | 0.833 | 6 | 0 |
| aim | bo4auto - plan | 12 | 0.25 | 3 | 0 |
| aim | bo4llm - cliche | 12 | 0.167 | 2 | 0 |
| aim | bo4llm - ground | 12 | 0.083 | 2 | 1 |
| aim | bo4llm - namefix | 12 | 0.583 | 6 | 1 |
| aim | bo4llm - plan | 12 | 0.0 | 1 | 1 |
| aim | cliche - ground | 12 | -0.083 | 2 | 3 |
| aim | cliche - namefix | 12 | 0.417 | 4 | 1 |
| aim | cliche - plan | 12 | -0.167 | 0 | 2 |
| aim | ground - namefix | 12 | 0.5 | 7 | 3 |
| aim | ground - plan | 12 | -0.083 | 2 | 3 |
| aim | namefix - plan | 12 | -0.583 | 0 | 5 |
