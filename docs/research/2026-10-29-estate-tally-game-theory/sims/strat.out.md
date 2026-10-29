## S1. Profitable misreport/abstention on a single binary motion (sizes 3/3/2/2, exhaustive)
| weights | q | quorum | rule | profiles x voters | profitable deviations | of which abstention |
|---|---|---|---|---|---|---|
| [25, 25, 25, 25] | 0.15 | 0.0 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.15 | 0.0 | wta | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.15 | 0.5 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.15 | 0.5 | wta | 10240 | 192 | 192 |
| [25, 25, 25, 25] | 0.5 | 0.0 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.5 | 0.0 | wta | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.5 | 0.5 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.5 | 0.5 | wta | 10240 | 192 | 192 |
| [25, 25, 25, 25] | 0.66 | 0.0 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.66 | 0.0 | wta | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.66 | 0.5 | prop | 10240 | 0 | 0 |
| [25, 25, 25, 25] | 0.66 | 0.5 | wta | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.15 | 0.0 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.15 | 0.0 | wta | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.15 | 0.5 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.15 | 0.5 | wta | 10240 | 288 | 288 |
| [40, 30, 20, 10] | 0.5 | 0.0 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.5 | 0.0 | wta | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.5 | 0.5 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.5 | 0.5 | wta | 10240 | 144 | 144 |
| [40, 30, 20, 10] | 0.66 | 0.0 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.66 | 0.0 | wta | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.66 | 0.5 | prop | 10240 | 0 | 0 |
| [40, 30, 20, 10] | 0.66 | 0.5 | wta | 10240 | 0 | 0 |

## S2. Logrolling (greedy best mutually profitable pairwise trade, repeated, max 6 trades)

### weights [25, 25, 25, 25], q=0.5, 6 motions, 1500 agendas; bloc utility u[b,m]~N(0,1) per motion passed
| bloc type | delta | P(>=1 trade) | mean trades | mean welfare change (sum u) | P(welfare falls) | P(a non-trader loses) | P(a trader ends worse than sincere) |
|---|---|---|---|---|---|---|---|
| unitary | 1.0 | 0.615 | 1.04 | +0.168 | 0.245 | 0.397 | 0.234 |
| proportional | 0.5 | 0.430 | 0.66 | -0.396 | 0.338 | 0.305 | 0.127 |
| proportional | 0.2 | 0.127 | 0.17 | -0.027 | 0.071 | 0.093 | 0.017 |

### weights [40, 30, 20, 10], q=0.5, 6 motions, 1500 agendas; bloc utility u[b,m]~N(0,1) per motion passed
| bloc type | delta | P(>=1 trade) | mean trades | mean welfare change (sum u) | P(welfare falls) | P(a non-trader loses) | P(a trader ends worse than sincere) |
|---|---|---|---|---|---|---|---|
| unitary | 1.0 | 0.615 | 0.90 | +0.016 | 0.302 | 0.484 | 0.184 |
| proportional | 0.5 | 0.324 | 0.44 | -0.306 | 0.230 | 0.261 | 0.076 |
| proportional | 0.2 | 0.110 | 0.13 | -0.075 | 0.070 | 0.099 | 0.013 |
