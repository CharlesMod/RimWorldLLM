# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| recap | base | 12 | 2.667 | 0.5 |
| recap | bo4auto | 12 | 2.583 | 0.583 |
| recap | bo4llm | 12 | 2.75 | 0.5 |
| recap | cliche | 12 | 3.0 | 0.333 |
| recap | ground | 12 | 2.25 | 0.667 |
| recap | namefix | 12 | 2.333 | 0.5 |
| recap | plan | 12 | 3.0 | 0.333 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| recap | base - bo4auto | 12 | 0.083 | 3 | 2 |
| recap | base - bo4llm | 12 | -0.083 | 4 | 3 |
| recap | base - cliche | 12 | -0.333 | 4 | 6 |
| recap | base - ground | 12 | 0.417 | 5 | 3 |
| recap | base - namefix | 12 | 0.333 | 5 | 2 |
| recap | base - plan | 12 | -0.333 | 3 | 6 |
| recap | bo4auto - bo4llm | 12 | -0.167 | 4 | 5 |
| recap | bo4auto - cliche | 12 | -0.417 | 4 | 7 |
| recap | bo4auto - ground | 12 | 0.333 | 5 | 3 |
| recap | bo4auto - namefix | 12 | 0.25 | 6 | 5 |
| recap | bo4auto - plan | 12 | -0.417 | 3 | 6 |
| recap | bo4llm - cliche | 12 | -0.25 | 2 | 5 |
| recap | bo4llm - ground | 12 | 0.5 | 7 | 1 |
| recap | bo4llm - namefix | 12 | 0.417 | 5 | 0 |
| recap | bo4llm - plan | 12 | -0.25 | 2 | 5 |
| recap | cliche - ground | 12 | 0.75 | 7 | 1 |
| recap | cliche - namefix | 12 | 0.667 | 8 | 2 |
| recap | cliche - plan | 12 | 0.0 | 2 | 3 |
| recap | ground - namefix | 12 | -0.083 | 3 | 4 |
| recap | ground - plan | 12 | -0.75 | 2 | 7 |
| recap | namefix - plan | 12 | -0.667 | 2 | 7 |
