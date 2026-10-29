# Eyes: estate internal tally, game-theory probes (2026-10-29)

Fork: Fork Desk question 39, `eureka-body:question:estate-internal-tally` (InForce; options proportional | winner-take-all; Self's recommended: proportional). Context ruling `eureka-body:ruling:governance-estate-blocks`. Session self-2026-10-29-eureka. Facts and measured results only, no recommendation.
Operator additions folded in (her words relayed by the Self): within-bloc vote-power curves ("Patrons vote power scales with donation total, but it's logarithmic... Member vote should scale with contribution... via a lower exponent"); present case ("I'm the only active member"); renormalization ruling ("normalize it by who actually voted").

## Code, outputs, model
Dir `C:\Users\Meta\AppData\Local\Temp\claude\F--Projects-CultLib\c4b0441b-f176-4f06-b58b-75efa6163f29\scratchpad\estate-sims\` (numpy 2.5.4, Python 3.13, `python -I <script>`; each `X.py` writes `X.out.md`, full tables there):
`power.py` exact Banzhaf/Shapley and two-tier individual power; `mc.py` Monte Carlo outcomes; `weights.py` within-bloc curves; `growth.py` present case and growth path; `strat.py` misreport/abstention and logrolling; `noshow.py` no-show and quorum under renormalization; `lone.py` lone-estate bounds (her absent).
- Pass rule as in Bifrost `MotionGovernanceService`: For/(For+Against) >= q, abstain excluded. Every tally renormalizes over estates with at least one For/Against ballot (the ruling); a WTA tie casts nothing, so a tied estate also drops out. Ties at exactly q pass (>=).
- Sizes unless stated: Minds 8, Members 15, Users 30 voting (roll 300, 10% turnout), Patrons 4. Weight seeds M/Me/U/P: equal 25/25/25/25; minds-heavy 40/30/20/10; members-heavy 20/45/20/15.
- "A priori" = Banzhaf coin-flip voters. "Motion model" (mc, weights W3): merit m~N(0,1), bloc taste e~N(0,1), bloc support p_b=logistic(1.7(0.6m+0.8e)); Minds intra-class correlation (ICC) via a shared beta-binomial propensity. ICC 0.44 is Kish-calibrated to Kohli 2026 (9 LLM judges ~ 2 effective votes, cited in the Bifrost prior-art note).
- Assumptions, not data: Member contribution = operator holds share S (0.5/0.8/0.95) of the total, the rest a Pareto(1.16) quantile ladder; Patron donations [10, 50, 200, 5000].
- Bug caught and fixed mid-run: the first mc.py drew per-voter propensities, which gives no correlation; results below are from the fixed shared-propensity version.

## 1. Unitary blocs: a priori power (power.out.md)
| weights | q | Banzhaf M/Me/U/P | Shapley M/Me/U/P | veto blocs |
|---|---|---|---|---|
| equal | .15/.5/.66 | .25 each | .25 each | none |
| minds-heavy | .15 | .33/.33/.33/0 (Patrons dummy) | same | none |
| minds-heavy | .5 | .42/.25/.25/.08 | .42/.25/.25/.08 | none |
| minds-heavy | .66 | .50/.30/.10/.10 | .58/.25/.08/.08 | Minds |
| members-heavy | .5 | .17/.50/.17/.17 | same | none |
| members-heavy | .66 | .20/.40/.20/.20 | .17/.50/.17/.17 | Members |
- Power is not weight: 40/30/20/10 at q=.5 gives Members and Users equal power; 10% Patrons are a dummy at q=.15.
- Meta-vote on weights (the ruling votes on block sizes): a reallocation of weight is a divide-the-dollar game. A simple game's core is nonempty iff it has a veto player (standard; e.g. Osborne & Rubinstein 1994 ch. 13) [M]. With no veto bloc (most rows) every weight vector can be beaten by a winning coalition that moves weight to itself. Where a veto bloc exists (minds-heavy .66, members-heavy .66), that bloc must be in any reweighting coalition. At equal weights and q > .75 every bloc is a veto player.

## 2. Individual power inside blocs, a priori (power.out.md)
P(one voter flips the motion); sizes 8/15/30/4.
| weights | q | rule | Mind | Member | User | Patron | max/min |
|---|---|---|---|---|---|---|---|
| equal | .5 | WTA | .118 | .096 | .064 | .158 | 2.5 |
| equal | .5 | prop | .143 | .077 | .038 | .296 | 7.7 |
| equal | .66 | WTA | .118 | .096 | .064 | .158 | 2.5 |
| equal | .66 | prop | .026 | .015 | .007 | .047 | 6.5 |
| minds-heavy | .5 | WTA | .169 | .108 | .051 | .061 | 3.3 |
| minds-heavy | .5 | prop | .234 | .091 | .030 | .115 | 7.8 |
| members-heavy | .5 | WTA | .037 | .189 | .016 | .058 | 11.5 |
| members-heavy | .5 | prop | .125 | .151 | .033 | .189 | 5.8 |
- Under prop each voter's marginal weight is w_b/n_b, so individual power scales ~1/n. Under WTA it scales ~1/sqrt(n) (Penrose). WTA compresses individual inequality, except where the council game makes a bloc near-dummy (members-heavy WTA: Users .016).
- At q=.66 with coin-flip voters, prop individual power collapses (a bloc share rarely sits near the threshold). WTA is unchanged at equal weights (3 of 4 blocs either way).
- Penrose sqrt shares for 8/15/30/4: 19.9/27.3/38.6/14.1. Using Minds n_eff 1.96 instead of 8: 11.0/30.4/43.0/15.7. Under sqrt weights the WTA individual powers are .093/.084/.096/.122, roughly equal (4 blocs is too few for exactness). Headcount weights make prop exactly equal (.106 each) but make WTA Minds and Patrons dummies (0) and Users a dictator.

## 3. Monte Carlo, motion model (mc.out.md; Minds ICC .44)
| weights | q | P(pass) prop | P(pass) WTA | P(outcomes differ) |
|---|---|---|---|---|
| equal | .15 / .5 / .66 | .934/.501/.265 | .838/.594/.404 | .098/.119/.148 |
| minds-heavy | .15 / .5 / .66 | .913/.500/.299 | .796/.537/.357 | .122/.118/.091 |
| members-heavy | .15 / .5 / .66 | .932/.500/.270 | .838/.500/.342 | .101/.111/.113 |
- The two rules disagree on 9-15% of motions in every configuration. Prop is the weighted popular share, so each disagreement is a WTA referendum paradox (Nurmi 1998) [M]. WTA flattens the effective threshold: passes less at .15 and more at .66, because a bloc that clears its internal majority casts 100%.
- Single-voter pivot (equal, q=.5), prop vs WTA: Mind .050/.021, Member .026/.023, User .013/.012, Patron .100/.076.
- Minds correlation (equal, q=.5):

| Minds ICC | n_eff of 8 | mean abs(Minds share - .5) | Mind pivot prop | Mind pivot WTA | P(Minds 4-4 tie) | P(rules differ) |
|---|---|---|---|---|---|---|
| 0 | 8.0 | .280 | .051 | .040 | .107 | .115 |
| .44 | 1.96 | .379 | .050 | .020 | .054 | .119 |
| .8 | 1.21 | .458 | .049 | .006 | .017 | .124 |
| 1 | 1.0 | .500 | .047 | 0 | 0 | .126 |
  Correlation pushes the Minds split toward 0/8 or 8/0, so prop and WTA deliver nearly the same Minds cast. Under WTA one Persona's flip almost never matters at ICC >= .44; under prop each Persona keeps a 1/8 slice regardless.
- Users turnout (equal, q=.5): at turnout 1/3/10/30%, P(the WTA cast misstates the population majority) = .18/.12/.08/.04 and the mean prop share error = .19/.11/.06/.03. P(motion differs from the full-turnout result) is prop .077/.044/.024/.014 vs WTA .062/.040/.024/.014. The rules are about equal here.
- Faction capture (Users population support .3, 9 random voters + k coordinated): P(WTA Users cast For) for k=0/3/6/9 = .10/.27/.80/.96; prop share .30/.48/.58/.65. Under WTA a 6-voter faction takes the whole Users weight 80% of the time; under prop it moves the share by .28.

## 4. Within-bloc power curves (weights.out.md)
Kish n_eff = (sum w)^2 / sum w^2. "Top" = the operator (Members) or the largest donor (Patrons). Internal Banzhaf = P(top's flip changes the bloc's WTA cast).
| bloc | curve | S | n_eff | top share | top internal Banzhaf |
|---|---|---|---|---|---|
| Members 15 | one-each | - | 15.0 | .067 | .21 |
| Members 15 | k=1 / 2 / 3 / 5 | .5 | 3.6 / 10.1 / 12.9 / 14.3 | .50 / .22 / .15 / .11 | 1.0 / .67 / .48 / .33 |
| Members 15 | k=1 / 2 / 3 / 5 | .8 | 1.6 / 6.1 / 10.4 / 13.5 | .80 / .36 / .23 / .14 | 1.0 / .96 / .70 / .47 |
| Patrons 4 | one-each / k=1 / log(1+d) | - | 4.0 / 1.1 / 3.3 | .25 / .95 / .42 | .75 / 1.0 / .75 |
| Patrons 4 | k=3 / 5 / 10 | - | 2.4 / 3.2 / 3.8 | .59 / .45 / .35 | 1.0 / .75 / .75 |
(The k=1, S=.5 row is an exact 50% tie with the rest, scored pivotal because tie-to-win counts as a change. Internal Banzhaf .75 for 4 one-each Patrons has the same cause.) On this donation ladder log(1+d) flattens like d^(1/5); d^(1/3) still leaves the top Patron dictating the bloc under WTA.
Operator's whole-tally pivot (a priori, Patrons log(1+d); W2):
| seed | q | S | one-each | k=1 | k=2 | k=3 | k=5 | (Mind / User, k=3) |
|---|---|---|---|---|---|---|---|---|
| equal | .5 | .5 prop/WTA | .069/.089 | .50/.42 | .23/.29 | .16/.20 | .12/.14 | .13/.11, .035/.059 |
| equal | .5 | .8 prop/WTA | - | .75/.42 | .38/.40 | .24/.30 | .15/.20 | |
| equal | .66 | .8 prop/WTA | - | .25/.42 | .09/.41 | .06/.30 | .04/.20 | |
| members-heavy | .5 | .8 prop/WTA | .15/.18 | 1.0/.84 | .75/.81 | .50/.59 | .32/.40 | |
- Under WTA her power is capped by the Members bloc's council power: about .42 at equal weights and .84 at members-heavy, reached once she dictates the bloc. Under prop it is uncapped at q=.5 (members-heavy k=1 S=.8: .999, which makes her near-dictator of the whole tally). At q=.66 prop shrinks every individual's power and WTA does not.
- Exponent sensitivity, motion model (W3; equal, q=.5, S=.5, baseline Members k=3 and Patrons log): P(outcome changes vs baseline) for Members one-each/k=1/k=2/k=5 is prop .014/.051/.010/.007, WTA .014/.061/.012/.008. For the Patron curve one-each/k=1/k=3/k=5/k=10 it is prop .027/.080/.026/.006/.013, WTA .032/.033/.033/0/0. Outcomes move by 1-8%. Her pivot moves more: .025 (one-each) to .21 (k=1) prop, and .022 to .30 WTA.

## 5. Present case and growth (growth.out.md, noshow.out.md; renormalized)
Present case: Members = her alone, Minds 8, Users and Patrons empty, so only two estates are live and their weights renormalize. P(pass | she votes For / Against / stays home), Minds coin-flip (N2):
| seed (M,Me) | q | prop | WTA |
|---|---|---|---|
| 25,25 | .5 | 1.00 / .004 / .64 | 1.00 / .36 / .36 |
| 25,25 | .66 | .86 / 0 / .15 | .64 / 0 / .36 |
| 40,30 | .5 | .996 / .035 / .64 | .64 / .36 / .36 |
| 20,45 | any | 1.00 / 0 / home .64 (.5), .15 (.66) | 1.00 / 0 / .36 |
- Equal seed, q=.5: under prop she decides the motion 99.6% of the time (Minds share must hit 8/0 to beat her Against). Under WTA her Against is overridden whenever the Minds majority is For (25 vs 25 ties and passes at >=). Minds-heavy WTA: a unanimous Minds bloc decides alone (her pivot 0.000 with correlated Minds). Members-heavy: she is dictator under both rules.
- With 3 Users + 1 Patron present (G1), equal q=.5: her pivot is .50 prop / .41 WTA.
When her single vote stops deciding the Members bloc under WTA (share <= .5; G2):
| curve | S=.5 | S=.8 | S=.95 |
|---|---|---|---|
| one-each | n=2 (tie), n>=3 | n=2 (tie), n>=3 | n=2 (tie), n>=3 |
| k=5 | n>=2 (tie at 2) | n=3 | n=5 |
| k=3 | n>=2 | n=5 (n=3 at .501) | n=10 |
| k=2 | n>=2 | n=10 | n=50 |
| k=1 | always tied at .5 | never | never |
Growth path, her whole-tally pivot (equal seed, q=.5, Users 10, Patrons 2): alone .72 prop / .43 WTA. n=5, S=.8: k=1 .60/.43, k=3 .31/.38, k=5 .24/.22, one-each .15/.16. n=20, S=.8: k=3 .16/.29, k=5 .10/.18, one-each .04/.08. Members-heavy seed, n=20, S=.8, k=3: .38/.59.

## 5b. Lone estate bounds, she absent (lone.out.md)
Operator ruling relayed: "But they totally should be able to make decisions without me if I'm not there to vote". Under renormalization the Minds estate is then the whole tally.
- Quorum floors a lone, fully turned-out Minds estate meets:
  - Headcount floor: 8/9 = .889 of eligible individuals.
  - Estate-weight floor (Minds weight over live-eligible estate weight): .500 equal, .571 minds-heavy, .308 members-heavy.
  - Any floor above these values requires her turnout. The at-least-two-estates floor (section 6 N1) requires her by construction while Users and Patrons are empty, so it is kept only as a later-membership comparison.
- Category thresholds (Bifrost `MotionCategoryPolicy`), lone Minds, all 8 voting. Columns: Personas needed under prop, then P(pass) in the motion model at Minds ICC 0 / .44 / 1:
  - Bugs .15: 2 needed; .78/.65/.50
  - Cosmetics .30: 3 needed; .66/.59/.50
  - Balance .40: 4 needed; .55/.53/.50
  - Features and NewContent .50: 4 needed; .55/.53/.50
  - FundamentalDesign .66: 6 needed; .34/.42/.50
  - Under WTA a lone estate casts 100% For or 100% Against, so the ratio is 1 or 0 and every category passes on a 5-of-8 majority: .45/.47/.50. Category thresholds are inert for a lone estate under WTA and binding under prop.
  - At ICC 1 (one effective Mind) both rules give .50 at every threshold: the threshold stops discriminating when the estate votes as one.
- Delay-plus-cancel window: P(she is back before effect), absence ~ Exponential(mean A); this measures the chance a cancel opportunity exists, not that it is used.

  | delay D | A=1 day | A=3 days | A=7 days | A=14 days |
  |---|---|---|---|---|
  | 1 day | .63 | .28 | .13 | .07 |
  | 3 days | .95 | .63 | .35 | .19 |
  | 7 days | 1.00 | .90 | .63 | .39 |
  | 14 days | 1.00 | .99 | .86 | .63 |

  Prior-art delays (Bifrost prior-art note, section 5 and section 1):
  - Compound Timelock: 2 days.
  - OpenZeppelin Timelock: exists so users can "exit the system if they disagree" before a decision executes; a canceller role; a guardian may cancel at any stage.
  - Olympus Bravo: veto guardian until execution.
  - Apache: votes run at least 72 h.
  - Rust FCP: 10 days, and a raised concern blocks.
  - Beanstalk had no delay; its governance attack drained ~$76M.

## 6. Strategic probes
S1 (strat.py, exhaustive; sizes 3/3/2/2, sincere profiles, no quorum): 0 profitable misreports or abstentions under either rule at any q or seed. The For/(For+Against) ratio is monotone and participation-safe without a quorum. With a cast-weight quorum (>= 50% of estate weight), WTA produced 144-288 profitable abstentions per 10240 cases: an abstention creates an internal tie, the estate drops out, and quorum fails. Prop produced 0.
N1 (noshow.py, renormalized, quorum floors). Columns: individual profitable deviations / hers / profitable estate stay-home. Present case 8/1/0/0, 512 profiles:
- No quorum: 0 / 0 / 0 in every seed, q and rule.
- Headcount quorum (>= half of eligible): 0 individual deviations. Estate stay-home is profitable in 56-93 profiles, because with only 9 eligible the Minds estate staying home breaks quorum.
- "At least two estates": WTA equal q=.5 gives 93 / 93 / 186; prop gives 1 / 1 / 94. Her staying home kills any motion she opposes, because with Users and Patrons empty she is the second estate. Minds-heavy q=.5: prop 9 / 9 / 101, WTA 93 / 93 / 93. At q=.66 most of these drop to 0 (prop equal still 56 estate cases).
- Mixed 3/1/2/1 with everyone voting: 0 under every floor. The no-show leverage comes from empty estates, not from the rule itself.
S2 logrolling (6-motion agendas, 1500 each, greedy mutually profitable pairwise trades; welfare = sum of bloc utilities). Unitary blocs can deliver their whole weight; proportional blocs' leadership moves the share by delta:
| seed | bloc type | P(>=1 trade) | mean trades | mean welfare change | P(welfare falls) | P(a non-trader loses) | P(a trader ends worse) |
|---|---|---|---|---|---|---|---|
| equal | unitary | .615 | 1.04 | +.168 | .245 | .397 | .234 |
| equal | prop delta .5 | .430 | .66 | -.396 | .338 | .305 | .127 |
| equal | prop delta .2 | .127 | .17 | -.027 | .071 | .093 | .017 |
| minds-heavy | unitary | .615 | .90 | +.016 | .302 | .484 | .184 |
| minds-heavy | prop delta .5 | .324 | .44 | -.306 | .230 | .261 | .076 |
- Unitary blocs find trades on most agendas. Trades raise mean welfare (equal seed) but impose losses on a non-trading bloc in 40-48% of agendas and leave a trader worse than sincere in 18-23%, which is Riker & Brams's paradox of vote trading at bloc scale. Partial-delivery blocs trade far less. The negative mean welfare at delta .5 is a model result (proportional shares near .5 flip low-stakes motions) that I have not cross-checked.

## 7. Literature (one or two lines each; [V] = verified by search this pass, [M] = from memory, not fetched)
- Putnam 1988, "Diplomacy and Domestic Politics: The Logic of Two-Level Games", Int. Org. 42(3):427-460 [M]. A deal must lie in each side's domestic win-set; a smaller win-set gives bargaining advantage (the Schelling conjecture), with the risk of involuntary defection. Mapping: a bloc parliament that ratifies the bloc position is a level-II ratifier. A prop bloc has no ratified position to bargain from.
- Schelling 1956 "An Essay on Bargaining", AER 46(3); Strategy of Conflict 1960 [M]. Binding oneself (burning bridges) is power. A unitary bloc whose position is fixed by an internal vote is a visible commitment device; prop splitting commits nothing beyond the count.
- Tests of the conjecture [V]. Mo 1995 APSR 89(4):914-924: a negotiator may gain by giving an agent veto, but only when the agent's preferences are extreme enough and depending on what the other side knows. Iida 1993 JCR and 1996 Public Choice: incomplete information about domestic preferences drives ratification failure; two-sided constraints give relatively equitable outcomes. Tarar 2001 JCR 45(3):320-340: with both sides constrained and incomplete information, the opposite of the conjecture can occur. All four estates having parliaments is the two-sided case.
- Diermeier & Feddersen 1998 APSR 92(3) [M]: the vote-of-confidence procedure makes coalitions cohesive, and cohesion raises a coalition's value in legislative bargaining. This is the party-discipline analogue: a disciplined unit outbargains an undisciplined one. Krehbiel 1993 "Where's the Party?" and Cox & McCubbins 1993 dispute how much discipline is real.
- Riker & Brams 1973 "The Paradox of Vote Trading", APSR 67(4) [M]: individually rational trades can leave all parties worse off. S2 measures it.
- Snyder, Ting & Ansolabehere 2005 "Legislative Bargaining under Weighted Voting", AER 95(4) [M]: in Baron-Ferejohn bargaining, expected payoffs track minimal-integer-representation weights rather than Banzhaf/Shapley. This applies to the weight meta-vote as distributive bargaining among unitary blocs.
- Penrose 1946 JRSS 109; Banzhaf 1965 Rutgers L. Rev. 19 [M]. Two-tier WTA equalizes individual power with sqrt(n) weights. Banzhaf 1968 "One Man, 3.312 Votes" showed large US states' unit rule over-empowers their voters [M].
- Jagiellonian compromise [V]: Słomczyński & Życzkowski; sqrt-of-population weights with quota ~62% (61.5% for EU-27) (arXiv cond-mat/0405396, 0712.2699). Poland tabled it in 2007; the Council adopted the double majority instead.
- Gelman, Katz & Bafumi 2004, BJPS 34(4):657-674 [V]: the coin-flip model overstates decisiveness in large units; empirically, proportional weights are "basically fair" and sqrt is not. Kirsch 2007 (Homo Oeconomicus) [M]: with correlated voters the optimal two-tier weights move from sqrt(n) toward n. The Minds correlation table above is a local instance: a high-ICC bloc of 8 has n_eff ~ 2.
- Coleman 1971 [M]: separates the power to initiate action from the power to prevent it. The thresholds .15 vs .66 shift power between these two, which is visible in the q rows of section 2.
- Electoral college unit rule [M]. Pathologies: campaigning and policy concentrate in swing states (Strömberg 2008 AER 98(3)); referendum paradox (Nurmi 1998 SCW 15). Faithless electors are bound by state law (Chiafalo v. Washington, 2020); in a bloc parliament the analogue is a Persona breaking the bloc mandate.
- Estates-General 1789 [M]: vote by order (each estate one unitary vote) vs by head; the Third Estate refused the unitary-order rule and declared itself the National Assembly on 17 June 1789. Precedent: a unitary-estate rule is contested when one estate's size outgrows its weight.
- Lijphart 1969 World Politics 21(2); Democracy in Plural Societies 1977 [M]. Consociationalism has four pillars: grand coalition, segmental autonomy, proportionality and mutual veto. Segmental autonomy fits each estate running its own internal process; mutual veto corresponds to the q > .75 equal-weight case in section 1.
- Hirschman 1970 [M]: exit, voice, loyalty. In section 5 no-show acts as exit-as-leverage when an estate is the quorum's second member.

## 8. Candidate articulations of the hunch: what the evidence shows
- Credible commitment / paradox of weakness. Supported in theory by Putnam, Schelling, Mo and Diermeier-Feddersen; qualified by Mo (needs an extreme agent) and reversed under two-sided incomplete information (Tarar). In sim, only unitary blocs can deliver whole-weight trades (S2: 61.5% of agendas vs 12.7% at delta .2).
- Power games in the open. Supported in structure: unitary blocs have 2^4 = 16 coalitions and the trades are pairwise and nameable. Under prop, influence travels through individual cross-bloc switching (prop pivots are per-individual, section 2). Contradicted in part by S2: open bloc trades still harm a non-trading bloc in 40-48% of agendas (Riker-Brams).
- Internal deliberation absorbs correlated noise. For Minds at ICC >= .44, prop and WTA already cast nearly the same weight (section 3), so the internal rule matters little there; a parliament would decide which Persona's argument wins, not the tally. For Users at low turnout, both rules carry the same sampling error (.08 at 10%), and WTA is more capturable by a small faction (k=6: 80%).
- Legibility of who owes whom. Unitary trades are pairwise records; under prop the "debt" is spread over individuals. No source measured this directly. Not found.
- Individual equality cuts the other way. WTA two-tier is Penrose-equalizable with sqrt weights; prop needs headcount weights. Gelman et al. and Kirsch argue that correlation pulls the fair weights back toward proportional.
- Operator dominance. Under WTA her ceiling is the Members bloc's council power (.42 equal, .84 members-heavy). Under prop at q=.5 her influence has no such ceiling. Her internal dictatorship of Members ends at the n shown in the section 5 table.
- Renormalization with no quorum: participation-safe in every exhaustive probe. Adding any quorum floor while estates are empty hands the sole Member, or the Minds estate, a stay-home veto. The two-estates floor conflicts with the "decide without me" ruling today. The bounds that remain on a lone Minds estate are a headcount floor <= .889, category thresholds (binding only under prop, and void at ICC 1), and a delay window whose cancel opportunity depends on her absence length (section 5b).
