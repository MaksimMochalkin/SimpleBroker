# ADR-0002: OpenTelemetry + Prometheus + Jaeger + Grafana

**Status:** accepted (stage 1)

## Context
Every later decision must be backed by numbers: latency percentiles, GC, thread pool, DB connections, traces.

## Decision
- The app exports metrics and traces over OTLP to an OpenTelemetry Collector.
- The Collector exposes metrics for Prometheus (scrape every 5 s) and forwards traces to Jaeger.
- Grafana provisions datasources and the `Simple Broker: overview` dashboard from `infra/grafana`.
- Custom signals use `Meter`/`ActivitySource` named `SimpleBroker.<Module>`.

## Alternatives considered
- **Direct Prometheus scraping of the app (no Collector):** rejected, the Collector lets traces and metrics share one pipeline
  and keeps the app unchanged when the backend changes (Azure Monitor later).
- **Logs-only diagnostics:** rejected, percentiles, GC and connection-pool behavior need metrics.

## Evidence (stage 1)
- The stack made the baseline interpretable: runtime counters (GC collections, heap size, exceptions) were read during every run
  with `dotnet-counters`, and the dashboard shows RPS, latency percentiles and GC per run.
- Gap found: k6 itself reports 100k+ unique time series because URLs contain account ids; k6 URLs must be grouped with a `name` tag.
- Gap found: the k6 default summary omits p99; `summaryTrendStats` now includes it.

## Consequences
- The same instrumentation works unchanged on Azure (Azure Monitor / managed Prometheus) in later stages.
- Runtime and Npgsql metric names depend on library versions; the dashboard must be checked against
  Prometheus after upgrades.
