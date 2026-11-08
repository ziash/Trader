# Scout Bot audit prompt

**Audit prompt v1.0 (08-Nov-2026).** The owner's brief for the independent review. Used for [review v1.0](2026-11-08-v1.0-independent-review.md). How to re-run it: [reviews/README.md](README.md#re-running-the-audit).

Before a re-run, update "Current operating context" in §1 and raise this document's version. Everything below the line is the brief as given.

---

# Scout Bot — Independent Technical Review & Improvement Assessment

Act as a senior algorithmic trading systems architect, quantitative trading researcher, C#/.NET engineer, broker API specialist and automated trading risk-management expert.

I want you to conduct an independent, critical assessment of the Scout bot in this repository.

Repository: `ziash/Trader`

Primary focus:
- `src/Trader.Scout/`
- `tests/Trader.Scout.Tests/`
- `config/scout.json`
- `config/scout-controls.json`
- `docs/bots/scout/`
- `Trader.Core` components used by Scout
- TM integration where it affects Scout's orders, positions, hedging and risk management
- Rewind integration where it affects historical testing and strategy validation
- Dashboard components where they affect Scout's controls and operational visibility

## 1. Important instructions

1. Inspect the actual current code and documentation before making recommendations.
2. Independently verify every issue. Do not assume that the recommendations in this prompt are necessarily correct.
3. Check the latest implementation against the latest specifications and decision documents.
4. Identify additional issues that I have not mentioned.
5. Distinguish actual bugs from potential weaknesses, design improvements and future enhancements.
6. Give precise file paths, class names, methods and line references for every code-level finding.
7. Explain the practical consequences of each issue in a real trading environment.
8. Consider that Scout and TM operate together. Do not assess Scout as an isolated strategy when discussing financial risk.
9. Do not assume that a theoretically safer or more profitable strategy is necessarily better. Require historical evidence and forward testing.
10. Do not modify source code, configuration, documentation or production settings. This is an assessment-only task. Wait for my approval before implementing any recommendation.

Current operating context:
- Scout currently operates on cTrader demo accounts.
- The current configured trial is Pepperstone demo.
- It monitors 18 symbols on H1.
- Scout identifies the setup and places the initial pending stop order.
- TM takes over Scout-labelled orders and manages mirrors, hedges, break-even and trailing protection.
- The current detector uses closed candles.
- The initial margin budget uses a 50% equity limit and a margin factor of 4.
- Scout Lens, comprising a quantitative scorer and AI chart reviewer, is planned but not fully implemented.
- Rewind is used for historical strategy evaluation.

Do not recommend enabling live trading as part of this exercise.

---

# 2. Execution reliability and order lifecycle

Conduct a detailed review of `AccountScout.cs` and the relevant `Trader.Core` Open API code.

Investigate:

### A. Order submission
- What happens when the broker accepts an order but the response is lost?
- What happens when the request times out?
- Can Scout accidentally duplicate an order?
- Can Scout permanently skip a valid setup because its local memory was updated before the broker confirmed the order?
- Is the client order ID sufficiently deterministic and suitable for idempotency?
- Does reconciliation reliably recover the order after a restart?
- Are broker execution events and reconciliation results handled consistently?

### B. Order cancellation
Specifically examine the `_cancelling` collection and cancellation logic.

Investigate whether a failed cancellation can leave an order ID permanently marked as cancelling.

Check:
- Broker rejection.
- API timeout.
- Network disconnection.
- Cancellation accepted but response lost.
- Order filled while cancellation is being processed.
- Scout restarting during cancellation.
- Dashboard symbol disable or setup skip.
- TM independently cancelling a Scout order.

Propose a robust cancellation state machine with confirmation from actual broker state.

### C. Recovery and reconciliation
Design a reliable recovery process for:
- Pending orders.
- Filled orders.
- Expired orders.
- Cancelled orders.
- Rejected orders.
- Partially closed positions.
- Positions whose originating order is no longer present in local memory.
- Orders placed by an earlier Scout instance.

Determine whether local JSON state, broker reconciliation and CSV logs can ever become inconsistent.

### Expected deliverable

Recommend an idempotent order lifecycle, including state transitions, retry policies, timeout handling, reconciliation and automated tests.

---

# 3. Risk management and position sizing

This is a high-priority area.

Examine:
- `MarginBudget.cs`
- `ScoutConfig.cs`
- `AccountScout.cs`
- TM's margin and hedge management
- `config/scout.json`
- AccountStatus and ExpectedMargin implementations in Trader.Core.

The current approach targets approximately similar monetary movement per 1H ATR across symbols. Investigate whether this provides adequate control over actual trading risk.

### A. Risk-based position sizing

Evaluate introducing a dedicated `PositionSizer` component.

Proposed principle:

Position size = Maximum permitted cash risk / Cash loss per unit of volume at the initial stop-loss.

Account for:
- Entry price.
- Initial stop-loss distance.
- Contract specification.
- Pip or tick value.
- Account currency conversion.
- Broker minimum volume.
- Broker volume steps.
- Maximum volume.
- Commission and estimated execution costs.

Important: If the minimum permitted volume exceeds the maximum permitted cash risk, the system must reject or skip the order rather than automatically increase exposure.

### B. Aggregate portfolio risk

Evaluate introducing an account-level risk manager.

It should consider:
- Existing open Scout positions.
- TM mirror positions.
- Both hedge legs.
- Pending Scout orders.
- Pending TM mirror orders.
- Orders likely to fill together.
- Existing manual positions, where applicable.
- Cross-symbol exposure.

Investigate correlated exposure between:
- EUR/USD, GBP/USD and other dollar pairs.
- JPY crosses.
- Gold, silver and platinum.
- US500 and USTEC.
- BTC, ETH and SOL.
- Oil and natural gas.

Avoid treating the 18 configured symbols as 18 independent risks.

### C. Risk guardrails

Evaluate configurable limits for:
- Maximum cash risk per trade.
- Maximum combined open risk.
- Maximum risk by symbol.
- Maximum risk by correlated group.
- Maximum daily loss.
- Maximum weekly drawdown.
- Maximum portfolio drawdown.
- Maximum margin utilisation.
- Maximum number of simultaneous positions.
- Maximum pending orders.
- Emergency account-level trading halt.

Explain how these controls should interact with TM.

### D. Margin stress

Investigate:
- Margin changes during volatility.
- Spread widening.
- Leverage differences between brokers.
- Margin changes on different asset classes.
- Hedged versus unhedged margin treatment.
- Margin requirements of mirrors and hedges.
- The possibility that protective hedge orders are rejected.

The system must never assume that a pending hedge provides actual protection until it is confirmed by the broker.

---

# 4. Trading strategy and detector improvements

Review `Detector.cs`, `ChartTracker.cs`, `ReplayOrders.cs`, and the Scout strategy specification.

Do not change the strategy simply because an alternative sounds attractive.

Identify weaknesses in the current pause-then-stop strategy and propose measurable experiments.

Investigate:

### A. Trend identification
- Does the current trend definition adapt sufficiently to different market regimes?
- Is ATR-based trend identification robust across forex, indices, commodities and crypto?
- Are there problems with sideways markets?
- Does the detector distinguish strong trends from exhausted trends?
- Are trend reversals identified too late or too early?

### B. Setup quality
Evaluate potential measurable features:
- Distance to previous swing highs and lows.
- Distance to significant support and resistance.
- Trend age.
- Number of previous entries in the same trend.
- Retracement depth.
- Pause duration.
- Pause height relative to ATR.
- Trend strength.
- Current volatility.
- Spread relative to ATR.
- Trading session.
- Distance from major market levels.
- News-event proximity.
- Higher-timeframe direction.
- Lower-timeframe confirmation.

### C. Entry and exit logic
Examine:
- Main and reversal stop pairing.
- Stop-loss placement at the far side of the pause.
- Trailing-stop behaviour.
- Sixteen-candle expiry.
- Orders that become stale while a setup is still technically valid.
- Stop-order trigger prices versus candle prices.
- Broker-side bid/ask differences.
- Slippage and market gaps.
- Orders with invalid or missing stop-loss levels.

Check whether the detector's historical candle-based interpretation can diverge from actual broker execution.

### D. Latest strategy proposals

Review the proposed improvements in the Scout specification:

- Daily direction as the primary directional filter.
- Daily, H4 and H1 entry ladder.
- Fibonacci retracement sizing at 0.382 and 0.5.
- One-candle pauses.
- 30M as the lowest trading timeframe.
- 5M lower-timeframe confirmation.
- Monthly and daily chart context.

Determine which changes are:
1. Already implemented.
2. Partially implemented.
3. Only documented or planned.

Recommend the order in which these should be tested.

---

# 5. Rewind and historical backtesting

Review the integration with the Rewind repository.

The first historical Scout Lens replay showed:
- 11,460 generated orders.
- 6,076 traded orders.
- Approximately -0.20R average per trade.
- 29% winning trades.
- 96% of trades exiting through trailing stops.
- Similar performance with and against the 4H trend.

These are initial six-symbol results and must not be treated as conclusive results for the complete system.

Investigate the limitations of the current replay implementation.

### Areas to investigate

1. Scout orders are simulated without the complete TM trade-management lifecycle.
2. Historical testing uses separate simulated brokers per symbol.
3. Portfolio-level margin is not simulated.
4. Actual configured Scout volumes are not necessarily represented in the outcome replay.
5. Broker costs and contract assumptions need validation against the active Pepperstone account.
6. Swap and other relevant costs should be included.
7. M5 price-path assumptions can affect intrabar execution.
8. Historical fill behaviour must account for bid/ask, spread and gaps.
9. The same strategy rules must be applied in replay and live operation.

### Proposed improvements

Evaluate a complete account-level historical simulation containing:
- Scout entry orders.
- TM mirror orders.
- Main and mirror hedges.
- Hedge fills and re-hedging.
- Break-even at the configured R threshold.
- Trailing stop management.
- TM's 2R and 3R rules.
- Spread guard behaviour.
- Commission.
- Swap.
- Margin requirements.
- Broker rejection.
- Margin calls and stop-outs.
- Simultaneous positions across all symbols.

The objective is to reproduce the complete trading system, not simply the entry strategy.

### Statistical validation

Recommend a testing framework with:
- Chronological training, validation and out-of-sample datasets.
- Walk-forward analysis.
- Multiple market regimes.
- Symbol-by-symbol results.
- Year-by-year results.
- Monte Carlo analysis.
- Parameter sensitivity.
- Maximum drawdown.
- Net expectancy in R.
- Profit factor.
- Maximum consecutive losses.
- MAE and MFE.
- Exposure time.
- Fill rate.
- Slippage.
- Tail-loss analysis.

Identify any potential look-ahead bias, data leakage, survivorship bias or unrealistic execution assumptions.

---

# 6. Scout Lens and AI integration

Review the existing Scout Lens proposal.

Assess the proposed architecture:

1. Rule-based detector.
2. Quantitative setup scorer, potentially LightGBM.
3. AI chart reviewer, potentially Claude API.
4. Final risk gate.
5. Order submission.
6. TM management.

Evaluate whether the architecture is appropriate.

### Quantitative scorer
Investigate:
- Expected R prediction versus binary win/loss classification.
- Feature selection.
- Training sample size.
- Feature importance.
- Model calibration.
- Out-of-sample validation.
- Model drift.
- Retraining frequency.
- ONNX deployment in C#.

### AI reviewer
Investigate:
- How to provide charts and structured market data.
- How to prevent the model from inventing market levels.
- How to ensure reproducible and structured JSON responses.
- Model version pinning.
- API timeouts and fallback behaviour.
- API cost.
- Prompt versioning.
- Audit logging.
- Shadow mode evaluation.

The AI must not be allowed to invent new trading setups or override portfolio risk controls.

Its permitted decisions should initially be:
- Approve.
- Reduce size.
- Veto.

Evaluate whether this separation is technically sound.

---

# 7. Dashboard and operational automation

Review the Scout dashboard and relevant API endpoints.

Suggest improvements to operational monitoring.

I would like to evaluate the following:

### Account monitoring
- Balance.
- Equity.
- Used margin.
- Free margin.
- Margin level.
- Total open cash risk.
- Pending order risk.
- Number of open positions.
- Number of pending orders.
- Daily realised and unrealised P&L.

### Scout monitoring
- Detector activity.
- Last successful candle retrieval.
- Last processed candle.
- Last successful order submission.
- Failed order submissions.
- Pending cancellation confirmations.
- Expired orders.
- Rejected orders.
- Scout/TM ownership and handoff status.

### Risk monitoring
- Daily loss limit.
- Portfolio drawdown.
- Correlated exposure.
- Margin utilisation.
- Per-symbol exposure.
- Risk limit violations.

### Automation controls
Evaluate:
- Global Automatic Orders ON/OFF.
- Individual symbol ON/OFF.
- Emergency stop for new entries.
- Existing position-management protection remains active after new entries are disabled.
- Broker connection-loss alarm.
- Stale market-data alarm.
- TM health/readiness monitoring.

The dashboard should distinguish an action that was requested from an action confirmed by the broker.

---

# 8. Technical architecture and software quality

Review the maintainability and reliability of the current C#/.NET architecture.

Assess:
- Separation of strategy, execution and risk management.
- Dependency injection and testability.
- Shared Trader.Core abstractions.
- Error handling.
- CancellationToken use.
- Concurrency and race conditions.
- Atomic file writes.
- State recovery.
- Logging consistency.
- Configuration validation.
- Broker-specific behaviour.
- API request pacing.
- Connection recovery.
- Service restart behaviour.

Investigate whether SQLite or another transactional persistence mechanism would offer advantages over the current JSON-based state files.

Do not recommend introducing a database just for the sake of modernization. Explain the actual operational benefits and complexity.

### Automated testing

Recommend tests covering:
- Broker API timeout.
- Duplicate execution events.
- Lost placement responses.
- Failed cancellation.
- Restart during order submission.
- Restart during cancellation.
- Order fill while cancellation is pending.
- Partial close.
- Symbol disabled with an open position.
- TM disconnected.
- TM unable to create a protective hedge.
- Margin limit reached.
- Stale candles.
- Abnormal spread.
- Broker disconnection.
- Simultaneous setups on multiple symbols.

Also assess whether GitHub Actions CI should automatically build the solution and run all tests on every pull request.

---

# 9. Security and deployment

Examine the deployment and security arrangements.

Pay attention to:
- API credentials.
- OAuth tokens.
- Dashboard authentication.
- Machine-to-machine authentication.
- File permissions.
- Log retention.
- Secrets accidentally committed to Git.
- Broker account restrictions.
- Service restart policies.
- Monitoring when the laptop sleeps or loses connectivity.

Assess the merits of moving Scout to a Linux VPS while maintaining a separate demo testing environment.

Do not expose secrets or print credential values in your report.

---

# 10. Required output: structured assessment

I want a professional report with the following sections.

## A. Executive assessment

Explain:
- What Scout currently does well.
- Its biggest technical weaknesses.
- Its biggest trading weaknesses.
- The most serious risks.
- Whether the current architecture can scale.

## B. Findings register

Provide a table:

| ID | Priority | Area | Finding | Evidence | Consequence | Recommendation |
|---|---|---|---|---|---|---|

Use these priorities:

- P0: Critical safety defect or potential uncontrolled trading exposure.
- P1: High-priority reliability or financial-risk issue.
- P2: Important strategic or architectural improvement.
- P3: Enhancement or future development.

Clearly identify confirmed bugs versus unverified concerns.

## C. Improvement proposals

For every important recommendation, provide:

1. Problem being addressed.
2. Current implementation.
3. Proposed solution.
4. Relevant source files.
5. Expected benefit.
6. Implementation complexity.
7. Possible side effects.
8. Required automated tests.
9. Acceptance criteria.

## D. Proposed target architecture

Provide a Mermaid architecture diagram showing the proposed relationship between:

- Market data.
- Detector.
- Setup evaluation.
- Scout Lens.
- PositionSizer.
- Portfolio Risk Manager.
- Execution Manager.
- cTrader Open API.
- TM.
- Account reconciliation.
- Dashboard.
- Historical replay and performance analytics.

## E. Prioritised implementation roadmap

Group recommendations into:

- Phase 1: Immediate safety and execution fixes.
- Phase 2: Risk-management improvements.
- Phase 3: Backtesting and strategy validation.
- Phase 4: Strategy improvements.
- Phase 5: Scout Lens and AI.
- Phase 6: Operational maturity and deployment.

For every phase, identify dependencies and completion criteria.

## F. GitHub issue backlog

Produce proposed GitHub issues in a format that can be copied into GitHub.

Each issue must include:
- Title.
- Priority.
- Problem.
- Scope.
- Files likely to be affected.
- Implementation notes.
- Test cases.
- Acceptance criteria.

## G. What NOT to build yet

Identify features that would add complexity without sufficient evidence of value.

Be particularly critical of:
- More technical indicators.
- Unvalidated strategy filters.
- Premature AI automation.
- Excessive parameter optimization.
- Unnecessary architecture changes.

---

# 11. Final assessment

Conclude by answering these questions:

1. Is Scout's current strategy demonstrating a credible trading edge?
2. What is the biggest financial-risk weakness?
3. What is the biggest software-engineering weakness?
4. What should be fixed immediately?
5. What should be tested before changing the strategy?
6. What should be deferred?
7. What conditions should be met before considering a live-account trial?

Give me your top 10 recommendations ranked by expected impact.

**Final instruction:** This is an independent code and architecture review. Do not edit any files, generate code changes, alter configurations, deploy services or commit anything. I want the findings and recommendations first so I can review and approve individual changes.
