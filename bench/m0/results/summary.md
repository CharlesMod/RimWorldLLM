## Q1 tables

### grid_cram0_swaFULL--swa-full
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 0.80 | 1.10 | 1009 | 838 | 0.790 | 1281 | 2343 |
| (b) warm turn 2 | 5 | 0.31 | 0.60 | 152 | 1721 | 0.303 | 502 | 6214 |
| (b) warm turn 3 | 5 | 0.32 | 0.52 | 153 | 1759 | 0.310 | 497 | 6282 |
| (c) re-lease old slot, new scene | 5 | 0.79 | 1.04 | 1013 | 829 | 0.788 | 1284 | 2335 |
| (c') re-lease old slot, same scene | 5 | 0.79 | 1.06 | 1013 | 829 | 0.787 | 1288 | 2342 |
| (d) re-lease other slot, new scene | 5 | 0.79 | 1.05 | 988 | 829 | 0.780 | 1267 | 2325 |
| (d') re-lease other slot, same scene | 5 | 0.79 | 1.09 | 988 | 829 | 0.783 | 1265 | 2314 |

### grid_cram0_swaOFF
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 1.58 | 1.85 | 1847 | 0 | 1.569 | 1172 | 1172 |
| (b) warm turn 2 | 5 | 0.37 | 0.61 | 152 | 1721 | 0.368 | 420 | 5106 |
| (b) warm turn 3 | 5 | 0.37 | 0.64 | 154 | 1761 | 0.365 | 421 | 5240 |
| (c) re-lease old slot, new scene | 5 | 1.57 | 1.83 | 1842 | 0 | 1.565 | 1173 | 1173 |
| (c') re-lease old slot, same scene | 5 | 1.57 | 1.88 | 1842 | 0 | 1.561 | 1180 | 1180 |
| (d) re-lease other slot, new scene | 5 | 1.57 | 1.81 | 1817 | 0 | 1.568 | 1168 | 1168 |
| (d') re-lease other slot, same scene | 5 | 1.57 | 1.84 | 1817 | 0 | 1.568 | 1162 | 1162 |

### grid_cram8192_swaFULL--swa-full
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 1.54 | 1.82 | 1818 | 0 | 1.503 | 1215 | 1215 |
| (b) warm turn 2 | 5 | 0.31 | 0.54 | 151 | 1721 | 0.299 | 506 | 6246 |
| (b) warm turn 3 | 5 | 0.31 | 0.54 | 154 | 1759 | 0.300 | 516 | 6455 |
| (c) re-lease old slot, new scene | 5 | 0.41 | 0.63 | 383 | 1464 | 0.367 | 1027 | 5039 |
| (c') re-lease old slot, same scene | 5 | 0.06 | 0.34 | 1 | 1841 | 0.017 | 60 | 109668 |
| (d) re-lease other slot, new scene | 5 | 0.41 | 0.66 | 353 | 1482 | 0.363 | 970 | 5034 |
| (d') re-lease other slot, same scene | 5 | 0.06 | 0.38 | 1 | 1816 | 0.017 | 57 | 104096 |

### grid_cram8192_swaOFF
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 1.70 | 1.92 | 1847 | 0 | 1.582 | 1160 | 1160 |
| (b) warm turn 2 | 5 | 0.37 | 0.63 | 152 | 1721 | 0.363 | 419 | 5107 |
| (b) warm turn 3 | 5 | 0.38 | 0.65 | 154 | 1761 | 0.369 | 419 | 5200 |
| (c) re-lease old slot, new scene | 5 | 0.59 | 0.88 | 520 | 1330 | 0.507 | 994 | 3659 |
| (c') re-lease old slot, same scene | 5 | 0.20 | 0.41 | 5 | 1837 | 0.104 | 48 | 17644 |
| (d) re-lease other slot, new scene | 5 | 0.58 | 0.84 | 488 | 1333 | 0.464 | 1041 | 3911 |
| (d') re-lease other slot, same scene | 5 | 0.20 | 0.47 | 5 | 1812 | 0.104 | 48 | 17648 |

### iq4xs_cramON_swaOFF
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 3 | 0.56 | 0.70 | 1822 | 0 | 0.343 | 5309 | 5309 |
| (b) warm turn 2 | 3 | 0.14 | 0.31 | 143 | 1714 | 0.129 | 1155 | 14339 |
| (b) warm turn 3 | 3 | 0.15 | 0.32 | 142 | 1752 | 0.144 | 987 | 13163 |
| (c) re-lease old slot, new scene | 3 | 0.34 | 0.48 | 537 | 1305 | 0.181 | 2809 | 10191 |
| (c') re-lease old slot, same scene | 3 | 0.32 | 0.44 | 5 | 1837 | 0.124 | 40 | 14794 |
| (d) re-lease other slot, new scene | 3 | 0.42 | 0.58 | 487 | 1325 | 0.189 | 2446 | 9618 |
| (d') re-lease other slot, same scene | 3 | 0.33 | 0.47 | 5 | 1808 | 0.135 | 37 | 13385 |

### tgt_cramOFF_swaOFF
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 1.08 | 1.27 | 1847 | 0 | 1.072 | 1720 | 1720 |
| (b) warm turn 2 | 5 | 0.26 | 0.41 | 154 | 1721 | 0.253 | 570 | 7412 |
| (b) warm turn 3 | 5 | 0.27 | 0.47 | 154 | 1759 | 0.260 | 576 | 7320 |
| (c) re-lease old slot, new scene | 5 | 1.07 | 1.30 | 1842 | 0 | 1.066 | 1728 | 1728 |
| (c') re-lease old slot, same scene | 5 | 1.06 | 1.26 | 1842 | 0 | 1.056 | 1744 | 1744 |
| (d) re-lease other slot, new scene | 5 | 1.08 | 1.30 | 1817 | 0 | 1.073 | 1720 | 1720 |
| (d') re-lease other slot, same scene | 5 | 1.07 | 1.29 | 1817 | 0 | 1.059 | 1723 | 1723 |

### tgt_cramON_swaOFF
| case | n | TTFT s | full line s | prompt_n | cache_n | prompt s | raw prefill t/s | effective prefill t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold lease | 5 | 1.19 | 1.46 | 1847 | 0 | 1.069 | 1705 | 1705 |
| (b) warm turn 2 | 5 | 0.26 | 0.45 | 154 | 1721 | 0.257 | 600 | 7314 |
| (b) warm turn 3 | 5 | 0.27 | 0.47 | 153 | 1759 | 0.261 | 585 | 7348 |
| (c) re-lease old slot, new scene | 5 | 0.44 | 0.66 | 520 | 1330 | 0.361 | 1382 | 5140 |
| (c') re-lease old slot, same scene | 5 | 0.19 | 0.36 | 5 | 1837 | 0.091 | 55 | 20289 |
| (d) re-lease other slot, new scene | 5 | 0.45 | 0.66 | 488 | 1333 | 0.336 | 1438 | 5410 |
| (d') re-lease other slot, same scene | 5 | 0.19 | 0.42 | 5 | 1812 | 0.091 | 55 | 20006 |

## Q2 tables
| run | n | median tok/s | pooled tok/s | drafted / accepted | median line s | median TTFT s |
|---|---|---|---|---|---|---|
| small_draftE4B | 58 | 23.3 | 24.6 | 2080 / 508 | 1.710 | 0.839 |
| small_draftE4B_r2 | 58 | 22.2 | 23.8 | 2136 / 485 | 1.600 | 0.808 |
| small_none | 58 | 47.9 | 50.5 | 0 / 0 | 1.161 | 0.735 |
| small_none_r2 | 58 | 48.0 | 50.9 | 0 / 0 | 1.114 | 0.727 |
| tgt_ngram-map-k | 58 | 82.3 | 86.6 | 0 / 0 | 0.673 | 0.435 |
| tgt_ngram-map-k4v | 58 | 82.2 | 86.8 | 0 / 0 | 0.679 | 0.435 |
| tgt_ngram-map-k4v_r2 | 58 | 82.0 | 86.8 | 0 / 0 | 0.665 | 0.435 |
| tgt_ngram-map-k_r2 | 58 | 82.4 | 86.7 | 0 / 0 | 0.653 | 0.439 |
| tgt_ngram-mod | 58 | 82.0 | 86.6 | 0 / 0 | 0.677 | 0.434 |
| tgt_ngram-mod_r2 | 58 | 82.2 | 86.5 | 0 / 0 | 0.657 | 0.439 |
| tgt_ngram-simple | 58 | 82.7 | 87.2 | 0 / 0 | 0.679 | 0.431 |
| tgt_ngram-simple_r2 | 58 | 82.3 | 87.0 | 0 / 0 | 0.660 | 0.438 |
| tgt_none | 58 | 82.3 | 87.1 | 0 / 0 | 0.664 | 0.430 |
| tgt_none_r2 | 58 | 82.2 | 86.4 | 0 / 0 | 0.661 | 0.436 |

## q3_conc_n6
| stream / condition | n | tok/s median | line latency s (median) | p90 | TTFT s | tokens/line |
|---|---|---|---|---|---|---|
| narr_alone | 5 | 84.0 | 2.32 | 2.38 | 0.53 | 153 |
| conv_alone | 30 | 81.8 | 0.63 | 1.41 | 0.36 | 20 |
| indep_alone | 30 | 82.6 | 0.46 | 0.71 | 0.26 | 18 |
| narr_w_conv | 5 | 30.4 | 5.42 | 5.44 | 0.31 | 155 |
| conv_w_narr | 30 | 47.2 | 0.83 | 1.01 | 0.42 | 20 |
| narr_w_indep | 5 | 23.3 | 7.02 | 7.21 | 0.67 | 149 |
| indep_w_narr | 60 | 29.1 | 0.94 | 1.36 | 0.35 | 16 |
| indep_pair | 60 | 34.0 | 0.81 | 1.07 | 0.32 | 16 |

## q3_conc_n11
| stream / condition | n | tok/s median | line latency s (median) | p90 | TTFT s | tokens/line |
|---|---|---|---|---|---|---|
| narr_alone | 5 | 59.5 | 3.13 | 3.23 | 0.69 | 151 |
| conv_alone | 30 | 59.6 | 0.86 | 2.15 | 0.51 | 21 |
| indep_alone | 30 | 58.9 | 0.70 | 0.95 | 0.40 | 17 |
| narr_w_conv | 5 | 20.2 | 7.50 | 7.53 | 0.28 | 147 |
| conv_w_narr | 30 | 33.4 | 1.19 | 1.38 | 0.59 | 20 |
| narr_w_indep | 5 | 16.5 | 10.13 | 10.30 | 0.86 | 148 |
| indep_w_narr | 60 | 18.1 | 1.37 | 2.08 | 0.53 | 16 |
| indep_pair | 60 | 23.5 | 1.17 | 1.81 | 0.47 | 16 |
