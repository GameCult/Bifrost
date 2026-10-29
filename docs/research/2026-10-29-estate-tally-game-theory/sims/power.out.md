## Unitary blocs: normalized Banzhaf / Shapley-Shubik, veto blocs
| weights | q | Banzhaf M/Me/U/P | Shapley M/Me/U/P | veto blocs |
|---|---|---|---|---|
| equal [25, 25, 25, 25] | 0.15 | 0.25/0.25/0.25/0.25 | 0.25/0.25/0.25/0.25 | none |
| equal [25, 25, 25, 25] | 0.5 | 0.25/0.25/0.25/0.25 | 0.25/0.25/0.25/0.25 | none |
| equal [25, 25, 25, 25] | 0.66 | 0.25/0.25/0.25/0.25 | 0.25/0.25/0.25/0.25 | none |
| minds-heavy [40, 30, 20, 10] | 0.15 | 0.33/0.33/0.33/0.00 | 0.33/0.33/0.33/0.00 | none |
| minds-heavy [40, 30, 20, 10] | 0.5 | 0.42/0.25/0.25/0.08 | 0.42/0.25/0.25/0.08 | none |
| minds-heavy [40, 30, 20, 10] | 0.66 | 0.50/0.30/0.10/0.10 | 0.58/0.25/0.08/0.08 | Minds |
| members-heavy [20, 45, 20, 15] | 0.15 | 0.25/0.25/0.25/0.25 | 0.25/0.25/0.25/0.25 | none |
| members-heavy [20, 45, 20, 15] | 0.5 | 0.17/0.50/0.17/0.17 | 0.17/0.50/0.17/0.17 | none |
| members-heavy [20, 45, 20, 15] | 0.66 | 0.20/0.40/0.20/0.20 | 0.17/0.50/0.17/0.17 | Members |

## Individual power (P one voter flips the motion), sizes {'Minds': 8, 'Members': 15, 'Users': 30, 'Patrons': 4}
| weights | q | rule | Minds | Members | Users | Patrons | max/min |
|---|---|---|---|---|---|---|---|
| equal | 0.15 | WTA | 0.0538 | 0.0525 | 0.0316 | 0.0683 | 2.2 |
| equal | 0.15 | prop | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 2.7 |
| equal | 0.5 | WTA | 0.1184 | 0.0961 | 0.0640 | 0.1584 | 2.5 |
| equal | 0.5 | prop | 0.1426 | 0.0770 | 0.0384 | 0.2956 | 7.7 |
| equal | 0.66 | WTA | 0.1184 | 0.0961 | 0.0640 | 0.1584 | 2.5 |
| equal | 0.66 | prop | 0.0262 | 0.0145 | 0.0073 | 0.0474 | 6.5 |
| minds-heavy | 0.15 | WTA | 0.0782 | 0.0661 | 0.0398 | 0.0293 | 2.7 |
| minds-heavy | 0.15 | prop | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 3.9 |
| minds-heavy | 0.5 | WTA | 0.1686 | 0.1080 | 0.0505 | 0.0609 | 3.3 |
| minds-heavy | 0.5 | prop | 0.2343 | 0.0909 | 0.0302 | 0.1146 | 7.8 |
| minds-heavy | 0.66 | WTA | 0.1769 | 0.0942 | 0.0316 | 0.0511 | 5.6 |
| minds-heavy | 0.66 | prop | 0.0407 | 0.0172 | 0.0058 | 0.0215 | 7.0 |
| members-heavy | 0.15 | WTA | 0.0538 | 0.0525 | 0.0316 | 0.0683 | 2.2 |
| members-heavy | 0.15 | prop | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 3.7 |
| members-heavy | 0.5 | WTA | 0.0366 | 0.1891 | 0.0164 | 0.0583 | 11.5 |
| members-heavy | 0.5 | prop | 0.1246 | 0.1509 | 0.0327 | 0.1888 | 5.8 |
| members-heavy | 0.66 | WTA | 0.0647 | 0.1455 | 0.0324 | 0.0900 | 4.5 |
| members-heavy | 0.66 | prop | 0.0167 | 0.0200 | 0.0045 | 0.0243 | 5.4 |

## Penrose square-root weights (WTA individual-power equalizer, independent voters)
sqrt(n) shares: [19.9, 27.3, 38.6, 14.1]
with Minds n_eff=8/(1+7*0.44)=1.96: [11.0, 30.4, 43.0, 15.7]
headcount shares (prop equalizer): [14.0, 26.3, 52.6, 7.0]
sqrt weights q=0.5: WTA indiv [0.0928, 0.0835, 0.096, 0.1218]  prop indiv [0.1414, 0.1027, 0.0722, 0.2034]
headcount weights q=0.5: WTA indiv [0.0, 0.0303, 0.1445, 0.0]  prop indiv [0.1061, 0.1061, 0.1061, 0.1061]
