# Reading a tape export

The scheduled export (gh#755, `R-10`) writes two files per contract per UTC day, plus the same pair
for closed coverage, and a pair per instrument and resolution for footprint cells. Footprint cells
have no contract column, so those files are not split by contract.

```text
/data/tape-exports/trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.csv.gz
/data/tape-exports/trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.parquet
/data/tape-exports/tape-coverage/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.csv.gz
/data/tape-exports/tape-coverage/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.parquet
/data/tape-exports/footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.vN.csv.gz
/data/tape-exports/footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.vN.parquet
/data/tape-exports/manifests/yyyy-MM-dd.json
```

`N` is the generation named by `manifests/yyyy-MM-dd.json`. Read that manifest. An older `.vN` can sit
beside it and is not the current copy.

CSV timestamps are UTC text `yyyy-MM-ddTHH:mm:ss.fffffffZ`. Fields that contain a comma, a quote, or a
newline are quoted (RFC 4180). Parquet timestamps are timestamp columns (nanoseconds, UTC), not text.
`price` is a decimal. `direction` is `0` unknown, `1` buy, `2` sell. A missing direction stays `0`.

The manifest's `sha256` is the lowercase hex of each file's bytes. A file that does not match is not
a successful export. These examples only read. They do not delete local tape.

## DuckDB

```sql
SELECT venue, instrument, contract_id, trade_time_utc, sequence, price, size, direction
FROM read_parquet(
  '/data/tape-exports/trades/topstepx/ES/CON.F.US.EP.Z26/2026-10-01.v1.parquet');
```

```sql
SELECT *
FROM read_csv(
  '/data/tape-exports/trades/topstepx/ES/CON.F.US.EP.Z26/2026-10-01.v1.csv.gz',
  header = true,
  timestampformat = '%Y-%m-%dT%H:%M:%S.%nZ');
```

Coverage uses `range_start` and `range_end`. Footprint uses `resolution_minutes`, `bucket_start`,
`buy_volume` and `sell_volume`, and has no `contract_id`.

```sql
SELECT venue, instrument, resolution_minutes, bucket_start, price, buy_volume, sell_volume
FROM read_parquet(
  '/data/tape-exports/footprint-cells/topstepx/ES/5/2026-10-01.v1.parquet');
```

## pandas

```python
import pandas as pd

trades = pd.read_parquet(
    "/data/tape-exports/trades/topstepx/ES/CON.F.US.EP.Z26/2026-10-01.v1.parquet")

csv_trades = pd.read_csv(
    "/data/tape-exports/trades/topstepx/ES/CON.F.US.EP.Z26/2026-10-01.v1.csv.gz")
csv_trades["trade_time_utc"] = pd.to_datetime(csv_trades["trade_time_utc"], utc=True, format="ISO8601")
```

`price` stays a decimal-capable column (`object` or `Float64` depending on the engine). Do not cast
it through `float` if the value has to match the tape. The manifest names the generation to read. An
older generation is a different path and is left in place.
