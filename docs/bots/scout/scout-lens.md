# Scout Lens — an AI layer for Scout

**A proposal: a layer after Scout's detector that decides, for each setup the rules find, whether to take it, skip it or trade it smaller.** Five ways to build it, each with its pros and cons and two example charts. Nothing here is decided or built; the owner chooses a starting point (§8). Part of [Scout](scout.md).

| Item | Value |
|---|---|
| Status | **Proposal — waiting for the owner's choice** (07-Nov-2026) |
| Document version | v1.1 (07-Nov-2026) |
| Interactive version | [claude.ai artifact](https://claude.ai/artifact/9ChjnZcXq2w6j7BeomXNet) (private; same content, charts with hover) and [`docs/scout-lens.html`](https://github.com/ziash/Scout/blob/main/docs/scout-lens.html) in the Scout repository |
| Charts | [`docs/bots/scout/images/`](images) — all candle charts use **made-up candles** to show the idea; scores and R values on them are examples, not results |

## 1. Why a layer

Scout's C# rules (`Detector.cs`, the same rules as Scout_Pine) find every pause. They can't weigh the small things the owner sees on the chart: a pause just under last week's high, a move that has already run too far, a messy pause. The layer sits here:

```
1H candles  →  Detector (C# rules, unchanged)  →  candidate: setups.csv row + order picture  →  Scout Lens: take · skip · smaller  →  Buy Stop / Sell Stop, then TM
```

The rules stay the safety net: the layer can only **remove or shrink** an order the rules already wanted; it never makes one up.

**Terms used below**

- **R** — profit or loss measured in units of the risk taken: the distance from entry to stop-loss. +1R = won what was risked; −1R = a full loss.
- **ATR** (Average True Range) — the typical size of one candle's move; Scout already measures pause height and move in ATR.
- **Model** — a formula a program learns from examples, instead of one we write by hand.
- **Overfitting** — a model or setting that fits the past by luck (it learned the noise) and then fails on new data.
- **Backtest / replay** — running Scout over past candles to see what it would have done.
- **Shadow mode** — the layer gives its verdict and it is logged, but it changes no order; we compare afterwards.

## 2. Option 1 — Setup scorer

*A small machine-learning model gives every setup a score: the profit it expects in R. Scout takes high scores, shrinks middling ones and skips low ones.* **Strong fit — recommended.**

Each candidate becomes one row of numbers. Most are already in `setups.csv` (§3 of [scout.md](scout.md)): pause candles, pause height and move in ATR, pull-back, ATR, with or against the 4H trend. We add context the rules don't use: hour and session, which step of the staircase this pause is, distance to the last swing high or low, spread, recent volatility. A **gradient-boosted tree model** (LightGBM — many small yes/no decision trees added together, the usual tool for tables of numbers) is trained in Python on years of replayed setups, saved as an **ONNX** file (a standard model file format) and loaded by `Trader.Scout` through ONNX Runtime (Microsoft's library for running such files from C#). Scoring takes under a millisecond.

| Data to start | Runs | Effort | Main risk |
|---|---|---|---|
| 10,000+ labelled setups from replaying years of 1H history | Inside Trader.Scout (C#), no outside service | Medium; the replay simulator is the big piece | Overfitting; future data leaking into training |

**Pros**
- The standard, proven way to filter the signals of a rule-based system.
- Works with thousands of examples, not millions.
- Shows which inputs pushed each score up or down.
- Same answer every time; can be backtested over years.

**Cons**
- Needs a replay simulator that plays out fills, stop-loss and trailing before any training.
- Learns only caveats that can be turned into a number.
- Needs retraining as markets change.
- The live trial (a few hundred setups so far) is far too small to train on alone.

| A score for every step of the staircase | Why the fourth setup scored −0.30R |
|---|---|
| ![Uptrend with four pauses, each scored](images/1a-staircase-scores.svg) | ![Score breakdown, input by input](images/1b-score-breakdown.svg) |
| The rules see four valid pauses. The scorer rates the fourth low: the move before it is long, it is the fourth step, and the pause is deep. Skipping it avoids the loss when the trend rolls over. | Each bar is one input's push on the score, starting from the average setup (+0.22R). This breakdown is logged with every decision, so the model's reasoning can be checked. |

## 3. Option 2 — Learn your eye

*The owner marks candidates take or skip, with a quick reason; a model learns to predict the owner's call.* **Supporting role.**

Scout's candidates, live or from replayed history, appear on a small labelling screen (for example a dashboard page) with the order picture. The owner taps Take or Skip and picks a reason. The labels train a classifier on the same rows as option 1, or on the chart pictures. The reasons also become a written list of caveats that options 1 and 3 can use.

| Data to start | Runs | Effort | Main risk |
|---|---|---|---|
| 1,000–3,000 of the owner's labels | Like option 1 once trained | Medium–high: a labelling screen plus the owner's time | Copies the owner's habits, the bad ones too |

**Pros**
- Captures the context the owner sees and the rules miss — the "not mathematical" part.
- Turns the owner's judgment into a written caveat list.
- Keeps the owner in the loop while Scout learns.

**Cons**
- Takes a lot of the owner's time: about 15 minutes a day for months.
- Learns what the owner would take, not what pays; must be checked against real outcomes.
- A model that reads chart pictures needs far more labels than one person can make.
- The owner's eye shifts over time, so early and late labels disagree.

| The labelling screen (mockup) | Same numbers, different call |
|---|---|
| ![Buy stop just under an earlier high, labelled Skip](images/2a-labelling-screen.svg) | ![Two setups with the same numbers](images/2b-same-numbers.svg) |
| The buy stop sits just under an earlier high, so the owner skips it and tags the reason "under prior high" (other reasons offered: move too stretched, messy pause, news soon). Each tap is one training example. | Both: pause 3 candles · height 0.4 ATR · move 3.2 ATR. To the rules they are identical. The owner takes the left and skips the right because of the old high just above. The model can learn that only if it sees "distance to prior high", as a number or in the picture. |

## 4. Option 3 — Claude chart reviewer

*When a setup fires, Scout shows Claude (Anthropic's AI model, through its API) the chart and the owner's caveat list. Claude answers approve, smaller size or veto, with a reason.* **Strong fit — recommended, shadow mode first.**

On each `NewStop` or `NewReversal`, Scout sends the order picture it already draws (§3.1 of [scout.md](scout.md), converted to PNG), the setup row, the last 100 candles of 1H and 4H, and the caveats in plain English. Claude replies in a fixed JSON shape. Fixed rules around it: Claude can only veto or shrink; if it is slow or down, Scout falls back to the rules alone; the model version is pinned; every prompt and reply is logged.

| Data to start | Runs | Effort | Main risk |
|---|---|---|---|
| None — the caveat list, in words | API call from Scout, a few seconds; fine on 1H | Low–medium | Inconsistent answers; outside dependency |

**Pros**
- No training: a caveat written today applies at the next candle.
- A reason for every decision, in words that can be checked.
- Handles fuzzy context: nearby levels, stretched moves, messy pauses.
- Reads the same picture the owner looks at.

**Cons**
- Its "confidence" is not a real probability.
- Can answer differently on the same setup.
- Slow and costly to backtest over years of setups.
- Depends on an outside service; model updates can change its behaviour.
- Can sound convincing while wrong — hence shadow mode first.

| Veto: selling into support | Approve: first pause off a turn |
|---|---|
| ![Sell stop just above a support zone](images/3a-veto-support.svg) | ![First pause after a bottom](images/3b-approve-turn.svg) |
| The rules see a clean downtrend pause. Claude sees the zone that bounced price twice. Here the order would have filled and reversed for a full loss. | One of the setups the owner's markups show as wanted: the first small pause right after a bottom. Claude approves it and says why. |

Claude's replies for these two:

```
verdict  veto
caveat   stop next to a tested level
reason   Sell stop is 0.3 ATR above a support zone that held twice
         in the last 20 candles. Little room before the level.
```

```
verdict  approve
caveat   none triggered
reason   First pause after a clear turn with a higher low. About 2.4 ATR
         of room up to the old breakdown level.
```

## 5. Option 4 — Auto-tune the thresholds

*Search for the rule settings (pause length, maximum pause height, minimum move) that made the most R in the past.* **Side tool only.**

An optimiser tries thousands of setting combinations against replayed history and keeps the best set, which then goes into `scout.json`. Nothing new runs inside Scout. Useful as a check that today's settings are reasonable — not as the AI layer.

| Data to start | Runs | Effort | Main risk |
|---|---|---|---|
| Replay results (same as option 1) | Offline; Scout just gets new numbers | Low once replay exists | High overfitting |

**Pros**
- Cheapest option.
- The rules stay readable; no new moving parts in the bot.
- Shows how sensitive the setup is to each setting.

**Cons**
- The easiest way to fit the past and fail live.
- Adds no new context; it only moves the same lines.
- The best settings drift as markets change.

| Total R by setting, 2019–2024 | Tuned settings after tuning stops |
|---|---|
| ![Heatmap of total R by setting](images/4a-settings-heatmap.svg) | ![Equity of tuned vs default settings](images/4b-tuned-vs-default.svg) |
| The single best cell (+41R) is surrounded by weak neighbours, so it is luck. Settings inside the dashed plateau are all good, which makes them a safer pick. | Tuned on 2019–2024, the best-looking settings beat the defaults by a wide margin. On data they never saw (2025–26) they fall back while the defaults keep going. |

## 6. Option 5 — End-to-end deep learning

*A neural network (or reinforcement learning — a program that learns by trial and error from rewards) reads raw candles and decides buy or sell by itself, with no pause rules.* **Not recommended.**

The network replaces the detector instead of helping it. It needs huge amounts of data and GPU training, then runs through ONNX like option 1.

| Data to start | Runs | Effort | Main risk |
|---|---|---|---|
| Hundreds of thousands of examples or more | GPU training; ONNX in Scout | High | Learns noise; can't explain itself |

**Pros**
- Could in theory find patterns nobody wrote down.
- No hand-made inputs needed.

**Cons**
- Throws away the setup the owner has spent weeks teaching Scout.
- Needs far more data than 1H forex and crypto history provide.
- Fits noise easily; reinforcement learning on markets is unstable.
- Gives no reason for any trade.

| Signals with no story | Accuracy while training |
|---|---|
| ![Model buy and sell signals on raw candles](images/5a-signals-no-story.svg) | ![Training vs unseen-data accuracy](images/5b-train-vs-unseen.svg) |
| The network fires buys and sells wherever its output crosses a line. None are tied to a pause, and none come with a reason that can be checked. | Training accuracy climbs toward 90 %. On data it hasn't seen it stays near a coin flip: it is memorising the past. |

## 7. Side by side

| Option | Data to start | Runs in Scout | Explains decisions | Fuzzy caveats | Backtest over years | Effort | Fit |
|---|---|---|---|---|---|---|---|
| 1 · Setup scorer | Replayed history | Yes, ONNX in C# | Input-by-input breakdown | Only if measurable | Yes | Medium | **Strong** |
| 2 · Learn your eye | Owner's labels | Yes, after training | The owner's tagged reasons | Yes | Partly | Medium–high | Supporting |
| 3 · Claude reviewer | None | API call | Yes, in words | Yes | Slow and costly | Low–medium | **Strong** |
| 4 · Auto-tune | Replayed history | Config only | Yes | No | Yes, overfits | Low | Side tool |
| 5 · Deep learning | Huge | Yes, ONNX | No | Maybe | Yes, overfits | High | No |

## 8. Recommendation and the owner's choice

**Options 1 and 3 together, in shadow mode first.** The scorer handles what can be measured; the reviewer handles what can only be described. Each time "the rules miss X", X becomes either a new scorer input (if it can be measured) or a new line in the reviewer's caveat list (if it can't).

1. **Build the dataset.** Add an outcome simulator to `--replay` (§5 of [scout.md](scout.md)) that plays every historical setup forward with Scout's real order rules: stop entry, stop-loss at the far side of the pause, trailing, 16-candle expiry, cancel when the setup ends. Run it on several years of 1H candles across 20+ symbols. Each setup gets its result in R.
2. **Baseline.** Before any machine learning, slice the results by the existing fields: with or against 4H, pause height, staircase step, session. One plain filter is often most of the gain. The trial review (≈ 10-Nov-2026) feeds straight into this.
3. **Train the scorer.** Walk-forward (train on older years, test on the next, step forward), with a gap between training and test periods so overlapping staircase setups don't leak. Judge it by total R and R per trade, not win rate.
4. **Add the reviewer.** Write the caveat list; wire the Claude call with the fixed reply shape, veto-only rights and a fallback.
5. **Shadow mode, 2–4 weeks.** Both write their score or verdict to a new `ai.csv`, linked by order id, but change nothing. Afterwards compare the setups they would have skipped with what really happened.
6. **Switch on** — only if the skipped setups really did worse, and total R goes up, not just win rate.

Two warnings. **Filtering changes the strategy:** the owner takes every pause on purpose and accepts the losers, so a filter that cuts losers will also cut some big staircase winners. **Every input must be known when the candle closes:** the 4H trend tag and the pause edges are where future data most often slips in.

**Starting points to choose from**

| Choice | What happens first |
|---|---|
| **A · Options 1 + 3** (recommended) | Replay dataset, then the scorer, then the reviewer — all in shadow mode |
| B · Option 3 first | Fastest to see something: Claude reviewer in shadow mode next week; the dataset after |
| C · Option 1 only | Measurable inputs only, no outside service |
| D · Wait for the trial review | Decide after the ≈ 10-Nov numbers are in |

Whatever is chosen, nothing changes in the running bots until the owner approves a plan; a rule change still starts in the Scout spec.

## 9. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 07 Nov 2026 | First issue: five options with pros, cons and two example charts each; recommendation (options 1 + 3, shadow mode first). Proposal only |
| v1.1 | 07 Nov 2026 | Documents reorganised (owner, [shared](../../shared.md) v1.13): moved to `docs/bots/scout/`, charts to `docs/bots/scout/images/` |
