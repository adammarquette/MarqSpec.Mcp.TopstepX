-- staging-seed.sql: a small, SYNTHETIC, real-shaped fixture for the disposable staging database (gh#758).
--
-- STAGING ONLY. scripts/railway/seed-staging.sh refuses to run it anywhere else, and the deploy workflow has
-- no production path to it. IDEMPOTENT: every insert is ON CONFLICT DO NOTHING on the table's primary key, so
-- a re-run adds nothing. DETERMINISTIC: no random(); the same rows every time.
--
-- Shapes follow the EF model snapshot (MarqSpec.Mcp.TopstepX.Data/Migrations/TopstepXDbContextModelSnapshot.cs):
-- "Trades" (Venue, Instrument, ContractId, TradeTimeUtc, Sequence) PK, Direction int, Price numeric(18,8),
-- Size bigint; "Bars" 1-minute OHLCV; "TapeCoverage" ranges. Nothing here is real: venue 'synthetic', no
-- account ids, keys or personal data, prices from a fixed formula. Runs AFTER the app's migrations; if the
-- schema moved, it fails loudly (ON_ERROR_STOP) rather than seeding a stale shape.
-- Run with psql -v ON_ERROR_STOP=1 (seed-staging.sh and the seed check both do).
BEGIN;

-- 2 instruments x 2 hours x 1 trade / 3 s = 4,800 trades, Thursday 2026-10-22 13:30-15:30 UTC (RTH open).
INSERT INTO "Trades" ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence", "Direction", "Price", "Size", "RecordedAt")
SELECT 'synthetic', i.instrument, i.contract,
       timestamptz '2026-10-22 13:30:00+00' + make_interval(secs => n * 3),
       n,
       CASE WHEN n % 7 < 4 THEN 1 ELSE 2 END,  -- TradeDirection: Buy = 1, Sell = 2
       round((i.base + i.tick * ((n % 97) - 48) + i.tick * 4 * sin(n / 120.0))::numeric / i.tick) * i.tick,
       1 + (n % 5) * (n % 3),
       timestamptz '2026-10-22 13:30:00+00' + make_interval(secs => n * 3 + 1)
FROM (VALUES ('MNQ', 'CON.F.US.MNQ.Z26', 21000.0, 0.25),
             ('MES', 'CON.F.US.MES.Z26',  5800.0, 0.25)) AS i(instrument, contract, base, tick),
     generate_series(0, 2399) AS n
ON CONFLICT DO NOTHING;

-- 1-minute bars derived from the synthetic trades, so bars and tape agree.
INSERT INTO "Bars" ("Venue", "Instrument", "ResolutionMinutes", "BucketStart", "ContractId", "Open", "High", "Low", "Close", "Volume", "RecordedAt")
SELECT "Venue", "Instrument", 1, date_trunc('minute', "TradeTimeUtc"), min("ContractId"),
       (array_agg("Price" ORDER BY "TradeTimeUtc", "Sequence"))[1],
       max("Price"), min("Price"),
       (array_agg("Price" ORDER BY "TradeTimeUtc" DESC, "Sequence" DESC))[1],
       sum("Size"), timestamptz '2026-10-22 15:31:00+00'
FROM "Trades" WHERE "Venue" = 'synthetic'
GROUP BY "Venue", "Instrument", date_trunc('minute', "TradeTimeUtc")
ON CONFLICT DO NOTHING;

INSERT INTO "TapeCoverage" ("Venue", "Instrument", "ContractId", "RangeStart", "RangeEnd", "RecordedAt")
VALUES ('synthetic', 'MNQ', 'CON.F.US.MNQ.Z26', '2026-10-22 13:30:00+00', '2026-10-22 15:30:00+00', '2026-10-22 15:31:00+00'),
       ('synthetic', 'MES', 'CON.F.US.MES.Z26', '2026-10-22 13:30:00+00', '2026-10-22 15:30:00+00', '2026-10-22 15:31:00+00')
ON CONFLICT DO NOTHING;

COMMIT;

SELECT 'seed ok' AS status,
       (SELECT count(*) FROM "Trades" WHERE "Venue" = 'synthetic') AS trades,
       (SELECT count(*) FROM "Bars"   WHERE "Venue" = 'synthetic') AS bars;
