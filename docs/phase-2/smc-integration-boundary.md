# Smart Money Concepts Integration Boundary

This pull request delivers the Smart Money Concepts engine as a standalone PAPER intelligence service.

It does **not** yet change the existing Thesis/Fusion weighting policy and does not automatically add SMC evidence to canonical candidate signals.

The current boundary is:

```text
canonical closed 5m candle
  -> standalone SMC service
  -> immutable SMC engine output
  -> SQL evidence and candle lineage
```

The next integration slice will:

1. fan out eligible candle publications to the SMC service;
2. load the latest eligible SMC output at the exact Fusion cutoff;
3. add a versioned `SMC` directional vote;
4. apply explicit contradiction penalties between technical direction, Order Flow, SMC and regime evidence;
5. preserve Thesis/Fusion as the only canonical signal authority.

Until that slice is merged, SMC outputs are diagnostic evidence and cannot affect risk, trade plans, execution or portfolio state.
