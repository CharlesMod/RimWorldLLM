# Judge scores

## Per feature x arm

| feature | arm | n | mean | share <= 2 |
|---|---|---|---|---|
| lore | r2base | 23 | 3.043 | 0.13 |
| lore | r2bo4auto | 24 | 3.458 | 0.083 |
| lore | r2bo4llm | 24 | 3.375 | 0.083 |
| lore | r2cliche | 23 | 3.304 | 0.087 |
| lore | r2ground | 23 | 3.304 | 0.087 |
| lore | r2plan | 23 | 3.783 | 0.0 |

## Paired (same case, first arm minus second)

| feature | arms | n | mean diff | wins | losses |
|---|---|---|---|---|---|
| lore | r2base - r2bo4auto | 23 | -0.435 | 3 | 12 |
| lore | r2base - r2bo4llm | 23 | -0.304 | 3 | 8 |
| lore | r2base - r2cliche | 22 | -0.273 | 3 | 8 |
| lore | r2base - r2ground | 22 | -0.227 | 1 | 5 |
| lore | r2base - r2plan | 22 | -0.727 | 1 | 13 |
| lore | r2bo4auto - r2bo4llm | 24 | 0.083 | 9 | 8 |
| lore | r2bo4auto - r2cliche | 23 | 0.13 | 6 | 5 |
| lore | r2bo4auto - r2ground | 23 | 0.13 | 8 | 5 |
| lore | r2bo4auto - r2plan | 23 | -0.348 | 3 | 9 |
| lore | r2bo4llm - r2cliche | 23 | 0.087 | 7 | 6 |
| lore | r2bo4llm - r2ground | 23 | 0.043 | 4 | 3 |
| lore | r2bo4llm - r2plan | 23 | -0.391 | 3 | 10 |
| lore | r2cliche - r2ground | 22 | 0.0 | 6 | 5 |
| lore | r2cliche - r2plan | 23 | -0.478 | 3 | 11 |
| lore | r2ground - r2plan | 22 | -0.455 | 1 | 9 |
