# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| lore | base | 12 | 3.167 | 0.167 |
| lore | bo4auto | 12 | 1.833 | 0.917 |
| lore | bo4llm | 12 | 2.417 | 0.667 |
| lore | cliche | 8 | 2.125 | 0.75 |
| lore | ground | 10 | 1.9 | 0.9 |
| lore | namefix | 11 | 2.818 | 0.182 |
| lore | plan | 9 | 1.778 | 0.889 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| lore | base - bo4auto | 12 | 1.333 | 9 | 1 |
| lore | base - bo4llm | 12 | 0.75 | 8 | 1 |
| lore | base - cliche | 8 | 1.125 | 5 | 0 |
| lore | base - ground | 10 | 1.4 | 8 | 0 |
| lore | base - namefix | 11 | 0.364 | 5 | 2 |
| lore | base - plan | 9 | 1.444 | 6 | 0 |
| lore | bo4auto - bo4llm | 12 | -0.583 | 1 | 5 |
| lore | bo4auto - cliche | 8 | -0.25 | 1 | 2 |
| lore | bo4auto - ground | 10 | -0.1 | 1 | 2 |
| lore | bo4auto - namefix | 11 | -1.0 | 1 | 9 |
| lore | bo4auto - plan | 9 | -0.222 | 0 | 2 |
| lore | bo4llm - cliche | 8 | -0.125 | 0 | 1 |
| lore | bo4llm - ground | 10 | 0.3 | 3 | 0 |
| lore | bo4llm - namefix | 11 | -0.455 | 2 | 7 |
| lore | bo4llm - plan | 9 | 0.556 | 4 | 1 |
| lore | cliche - ground | 8 | 0.25 | 1 | 0 |
| lore | cliche - namefix | 8 | -0.625 | 1 | 5 |
| lore | cliche - plan | 6 | 0.5 | 2 | 0 |
| lore | ground - namefix | 9 | -0.889 | 1 | 7 |
| lore | ground - plan | 7 | 0.143 | 1 | 0 |
| lore | namefix - plan | 9 | 1.111 | 7 | 0 |
