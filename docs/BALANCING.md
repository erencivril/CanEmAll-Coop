# Balancing for 2 to 4 players

This document explains how the mod keeps a co-op team at roughly the pace of a solo run, why it does it this way, and which settings let you change it. The short version is in the [README](../README.md#how-the-balancing-works).

Everything here describes the default mode (`PaceMode = Neutral`). Nothing in this file is measured over long play sessions yet; the model comes from analysing how the game's economy is built, and the numbers (exponent, caps) are estimates that the config lets you change. See [Status and open questions](#status-and-open-questions).

## 1. The problem

In a solo run, money per second comes from a chain: a truck delivers enemies, the enemies are killed and carried to a grinder, the grinder turns them into cans, and cans are paid out. Progress is the skill tree, which you buy with that money.

If you drop several players into that factory unchanged, two things go wrong:

- **Early and mid game:** the factory does not get faster. There is still one truck and the same few machines. A second player earns nothing extra, they just have fewer enemies to kill.
- **Late game:** the factory is no longer the limit, your kill rate is. Now every extra player really adds income, and a team could finish in a fraction of the time.

So the same team is too slow at the start and too fast at the end, and a single "multiply prices by N" cannot fix both.

## 2. Three regimes

Income per second is limited by the smallest of three rates:

```
income  ~  min( supply , machines , kills ) * (cans per enemy) * (money per can)
```

| Regime | What limits you | Typical for | Do more players help? |
|---|---|---|---|
| **Truck-bound** | Enemies delivered per truck and how often a truck comes | the first days, a handful of enemies per truck | No. One truck, one cycle. |
| **Machine-bound** | How fast the grinders consume enemies | the middle, tens of enemies per truck | No. A machine is shared, two players do not double it. |
| **Kill-bound** | How fast the players kill, carry and feed enemies | late game, a hundred or more enemies per truck | Yes, roughly with the number of players, but less than linearly (people get in each other's way, overkill the same target). |

The last two columns of "cans per enemy" and "money per can" are plain multipliers that do not depend on the player count.

This split is inferred from how the game's economy is built. Which regime you are in at a given moment depends on your upgrades, so the model does not try to guess it.

## 3. The idea: scale the factory, then divide the pay

> **Since v0.1.3** the division is applied to the prices instead of the pay: every skill tree price is multiplied by `k` and every can pays its full value. Buying power per can is the same as dividing the pay by `k`, so the pace below is unchanged. Only the run total that rebirth points come from is still divided (with the exact carry described in section 4), so selling the factory gives the solo number of points. Permanent upgrades keep their prices.

Instead of looking for one price factor, the mod does two things together, using one number `k`:

1. **Make every limit `k` times bigger.** The truck delivers `k` times as many enemies (supply), and every grinder works `k` times as fast (machines). The players supply the third limit themselves by being `N` people.
2. **Divide every payout by `k`.**

With all three limits `k` times higher and the pay divided by `k`, you get the solo money per second **whichever limit is the binding one**. That is what a price multiplier cannot do. Prices, the skill tree, rebirth point values and the amounts on the screen all stay in solo money units.

The cost of this approach: the team has to be able to deliver the `k` times larger workload in the kill-bound regime, see [section 8](#8-status-and-open-questions).

## 4. The formulas

`N` is the number of players (1 to 4), latched per day. `a` is `WorkloadExponent` (default 0.8). `E` is the number of enemies per truck delivery in a solo game with the skills you currently have.

```
w     = N ^ a                                          workload, 1.00 / 1.74 / 2.41 / 3.03 for N = 1..4
E_eff = clamp( round_half_up(E * w) , E , PerfCap - 100 )     enemies per truck delivery in co-op
k     = E_eff / E                                      the realized pace factor
```

| Thing | Solo | Co-op (host, Neutral mode) |
|---|---|---|
| Enemies per truck | `E` | `E_eff` |
| Field cap (enemies alive or lying around at once) | 750 | `min( ceil(750 * w) , PerfCap )`, never below 750 |
| Grinder process interval | `t` | `t / k` |
| Each payout | `m` | `m / k`, with an exact fractional carry |

With the default `PerfCap = 1000` the field cap is 750 for N=1 and 1000 for N=2 to 4. The cap matters because the truck only comes when the new batch fits under it; the cap is also the performance valve of the host.

`N = 1`, or no session at all, changes nothing: `E_eff = E`, `k = 1`, no field is written, no payout is touched.

### The fractional carry

Dividing a payout by `k` produces fractions. Rounding each one would lose or invent money, and several small payouts per can (normal, shiny, smoked and so on) make that worse. So the mod keeps the remainder: each payout is added to a running sum in integer arithmetic, the whole part is paid, and the remainder stays for the next payout.

Example: `E = 12`, `N = 2`, so `E_eff = 21` and `k = 1.75`. Four payouts of 10 give 5, 6, 6, 5, in total 22. The exact value is 40 / 1.75 = 22.86, and the missing 0.86 is exactly what is still in the carry. Over a day nothing is lost except less than one money unit left in the carry, which is dropped when the factors are recomputed.

## 5. Why the divisor is the realized `k`, not `w`

`w` is the ideal workload, but the factory can only deliver whole enemies. With one enemy per truck and two players you cannot deliver 1.74, you deliver 2. The machines and the supply really get `k = E_eff / E` times faster, so the pay must be divided by exactly that, or the team is paid for work it did not do (or the other way round):

| Case | E | N | w | E_eff | k | If the divisor were `w` instead |
|---|---|---|---|---|---|---|
| Rounding up | 1 | 2 | 1.74 | 2 | 2.00 | team earns 2.00 / 1.74 = 1.15 times the solo pace |
| Rounding up | 3 | 3 | 2.41 | 7 | 2.33 | 2.33 / 2.41 = 0.97, a little too slow |
| Performance cap | 250 | 4 | 3.03 | 500 with `PerfCap = 600` | 2.00 | 2.00 / 3.03 = 0.66, far too slow |

The same `k` is used for the supply, the grinder speed and the payout, so they can never drift apart, even when the cap or rounding changes `E_eff`. This is also why `PerfCap` is safe to lower: the pay follows the supply that actually arrives.

## 6. `E_eff` for every "More Enemies" tier

`E` starts at 1 and the "More Enemies" skill line sets it to 3, 6, 12, 25, 50, 90, 125, 150, 200 and 250 (tiers 1 to 10, the last tier wins). These are the values of the game's skill data; tiers 1 to 5 (3 to 50) were seen in the two-instance tests, the later tiers have not been reached in a test run.
Default settings (`a = 0.8`, `PerfCap = 1000`). The value in brackets is `k`.

| Tier | E (solo) | N = 2 | N = 3 | N = 4 |
|---|---|---|---|---|
| none | 1 | 2 (2.00) | 2 (2.00) | 3 (3.00) |
| 1 | 3 | 5 (1.67) | 7 (2.33) | 9 (3.00) |
| 2 | 6 | 10 (1.67) | 14 (2.33) | 18 (3.00) |
| 3 | 12 | 21 (1.75) | 29 (2.42) | 36 (3.00) |
| 4 | 25 | 44 (1.76) | 60 (2.40) | 76 (3.04) |
| 5 | 50 | 87 (1.74) | 120 (2.40) | 152 (3.04) |
| 6 | 90 | 157 (1.74) | 217 (2.41) | 273 (3.03) |
| 7 | 125 | 218 (1.74) | 301 (2.41) | 379 (3.03) |
| 8 | 150 | 261 (1.74) | 361 (2.41) | 455 (3.03) |
| 9 | 200 | 348 (1.74) | 482 (2.41) | 606 (3.03) |
| 10 | 250 | 435 (1.74) | 602 (2.41) | 758 (3.03) |

Early tiers are rounded a lot (k between 1.67 and 3.00), late tiers are within a few percent of `w`.

The largest cell, 758 enemies per truck at N=4, is below the ceiling of `PerfCap - 100 = 900`, so the cap does not change `k` by default. It does leave little room under the 1000 cap: a truck only arrives when the whole batch fits, so in the late game with four players trucks wait until the field has drained to about 240. This is the point where the field cap is the limiting factor rather than the machines, and whether it makes a four-player late game slower than solo needs real play to say. Raising `PerfCap` loosens it, at the price of more physics objects on the host.

## 7. What is deliberately not scaled

| Not scaled | Why |
|---|---|
| Permanent upgrade prices | Rebirth points stay in solo units (the run total they are computed from grows by `payout / k`). |
| Rebirth points | They come from total money earned, which is already in solo units. A pooled total would otherwise give about the square root of N times too many points. |
| Starting cash after a rebirth | It is a fixed amount of money, and the pace of income was already normalised. It is applied once, on the host. |
| Day length, night shift length, the delivery cut-off before the day ends | Wall-clock pacing. More enemies do not make a day longer. |
| Truck cycle time | One truck, one cycle, real time. |
| Enemy health, can value, speed, shiny and smoked chances | Per-unit values. Damage upgrades are shared, so the team's damage already grows with the number of shooters, and scaling health on top would count it twice. |
| Weapon damage, ammo, crit chances, stamina, movement speed, vacuum range and throw force | Per-player and local. Multipliers are shared and apply to every hit. |
| Conveyor speed | World-bound. Optional through `ScaleConveyor`. |
| Robots and turrets | Automation is world-bound. With `k` times more enemies their share of the work shrinks, which is intended: players matter more in co-op. |
| Physical can limits (how many cans can exist, how many merge into one) | Raising them multiplies host physics and network traffic. Merging already absorbs the extra cans. |
| Slot machine odds | Bets are proportional to the shared pool, not to income, and winnings do not count as earned money. A social safety valve exists instead: `MaxBetFraction`. |

Known and accepted distortion: skills that help every player (movement, ammo, stamina, vacuum) cost one price but benefit all players, while skills that upgrade the shared factory benefit the team once. A team will tend to buy the personal skills early. If this becomes a problem in practice, a price weight per category would be the next step.

Cosmetic side effects: the income numbers on the HUD and the floating "+$" texts are smaller than a solo player would see for the same can, and statistic counters such as kills and cans grow by about `k`.

## 8. Status and open questions

- **`a = 0.8` is a guess.** The reasoning: the team must really deliver `k` times the kills, which needs each player to be at least `w / N` as effective as a soloist, that is 87% at N=2, 80% at N=3 and 76% at N=4. Perfect scaling (`a = 1`) would need every extra player to be a full second soloist, which is not realistic because people interfere with each other.
- **The three regimes are inferred**, not measured. It is not known precisely how far a given set of upgrades is from the next limit.
- **The grinders may have a hidden intake limit** (how many enemies a machine can take in per second regardless of its process speed). If it binds at high `k`, the grinder scaling is partly ineffective at N = 3 or 4 in the late game. This is worth measuring.
- **Belts may jam** with `k` times more bodies on them. `ScaleConveyor` is the valve, off by default, and its effect has not been confirmed in play.
- **What has been checked:** the pure math (including every table above) is unit tested, and in a two-player run on one PC the host logged the pre-division and post-division totals: 764 and 434, a ratio of 0.568, against `1/k = 1/1.76 = 0.568`. A leaving player drops `N` back to 1 and `k` to 1.00 from the next day.
- **What has not:** a multi-hour co-op campaign compared against a solo campaign. Acceptance target for that comparison: team money per day at the same number of purchased skills within about 0.9 to 1.1 of solo, and per-player kills within about 0.75 to 1.0 of solo. The host's per-day `[DAYSTATS]` log line is meant to produce exactly this data (day, `N`, `E_base`, `E_eff`, `k`, trucks, enemies spawned, kills, can units, money in before the divisor and out after it, grinder queue).

Reports with real numbers (day, number of players, skills unlocked, money per day, whether it felt faster or slower) are the most valuable feedback for tuning the defaults.

## 9. The config valves

All keys are in `BepInEx/config/com.eren.canemallcoop.cfg`, section `[Balance]` unless noted. They are read by the host and take effect at the next day start or skill purchase.

| Key | Default | Use it when |
|---|---|---|
| `WorkloadExponent` | `0.8` | The team is faster than solo: raise it towards 1.0 (higher `k`: more enemies per extra player, faster grinders, and more pay division). The team cannot keep up with the enemies: lower it (smaller `k`). It changes supply, grinder speed and pay division together. |
| `PaceMode` | `Neutral` | Set `Free` to keep the extra enemies but skip the grinder scaling and the pay division. The team then earns whatever the shared machines allow. There is no pace guarantee: depending on the regime the team can be faster than solo. |
| `PerfCap` | `1000` | The host stutters: lower it (600 is a reasonable try). The late game feels capped by the field: raise it. It limits the truck batch (to `PerfCap - 100`) and the field cap (to `PerfCap`); `k` follows the batch that really arrives. It can never reduce the solo cap of 750. |
| `MaxSpawnBatch` | `650` | A truck delivery is created in one frame by the game, and a 4-player late game would ask for about 758 enemies at once. This caps the batch (on top of `PerfCap`); `k` follows the batch that really arrives. Raise it if your machine copes, lower it if the host hitches at the truck. |
| `IncomeDivisorExponent` | `1.0` | Skill prices (and the rebirth total) use `k` to this power. 1.0 is solo pace; below 1 the team progresses faster, above 1 slower, 0 switches it off. Keep it the same on every player. |
| `ScaleConveyor` | `false` | Belts jam with the larger supply. Multiplies belt speed by about `sqrt(k)` (for the duration of the speed update, restored afterwards). |
| `ScaleFirstBloodCounters` | `false` | You use the "first hits of the day" and "first kills of the day" rebirth perks and want them to cover the same share of a bigger day. Their counters then advance `1/k` per event. |
| `MaxBetFraction` (section `[Rules]`) | `1.0` | Not a pace setting. Caps the slot machine stake at that fraction of the shared wallet so that one player cannot gamble the team's money away in one spin. |

Combinations that make sense:

- **Relaxed co-op that may be faster than solo:** `PaceMode = Free`, optionally with a lower `WorkloadExponent`.
- **Harder co-op:** `WorkloadExponent = 1.0` (every added player adds a full soloist's workload).
- **Weak host PC:** `PerfCap = 600`.
