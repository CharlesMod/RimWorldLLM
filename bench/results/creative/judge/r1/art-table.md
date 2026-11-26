# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| art | base | 7 | 2.286 | 0.571 |
| art | bo4auto | 12 | 2.167 | 0.833 |
| art | bo4llm | 12 | 2.417 | 0.667 |
| art | cliche | 12 | 2.167 | 0.833 |
| art | ground | 12 | 2.167 | 0.833 |
| art | namefix | 12 | 2.917 | 0.167 |
| art | plan | 12 | 2.5 | 0.667 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| art | base - bo4auto | 7 | 0.143 | 2 | 1 |
| art | base - bo4llm | 7 | -0.429 | 3 | 4 |
| art | base - cliche | 7 | 0.0 | 2 | 2 |
| art | base - ground | 7 | 0.0 | 2 | 2 |
| art | base - namefix | 7 | -0.429 | 1 | 3 |
| art | base - plan | 7 | -0.429 | 2 | 3 |
| art | bo4auto - bo4llm | 12 | -0.25 | 2 | 4 |
| art | bo4auto - cliche | 12 | 0.0 | 2 | 2 |
| art | bo4auto - ground | 12 | 0.0 | 2 | 2 |
| art | bo4auto - namefix | 12 | -0.75 | 0 | 8 |
| art | bo4auto - plan | 12 | -0.333 | 1 | 3 |
| art | bo4llm - cliche | 12 | 0.25 | 3 | 1 |
| art | bo4llm - ground | 12 | 0.25 | 3 | 1 |
| art | bo4llm - namefix | 12 | -0.5 | 2 | 7 |
| art | bo4llm - plan | 12 | -0.083 | 2 | 3 |
| art | cliche - ground | 12 | 0.0 | 0 | 0 |
| art | cliche - namefix | 12 | -0.75 | 1 | 9 |
| art | cliche - plan | 12 | -0.333 | 2 | 4 |
| art | ground - namefix | 12 | -0.75 | 1 | 9 |
| art | ground - plan | 12 | -0.333 | 2 | 4 |
| art | namefix - plan | 12 | 0.417 | 7 | 2 |
