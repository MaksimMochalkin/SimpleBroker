# ADR-0002: OpenTelemetry + Prometheus + Jaeger + Grafana

**Status:** accepted (stage 1)

## Context
Every later decision must be backed by numbers: latency percentiles, GC, thread pool, DB connections, traces.

## Decision
- The app exports metrics and traces over OTLP to an OpenTelemetry Collector.
- The Collector exposes metrics for Prometheus (scrape every 5 s) and forwards traces to Jaeger.
- Grafana provisions datasources and the `Simple Broker: overview` dashboard from `infra/grafana`.
- Custom signals use `Meter`/`ActivitySource` named `SimpleBroker.<Module>`.

## Consequences
- The same instrumentation works unchanged on Azure (Azure Monitor / managed Prometheus) in later stages.
- Runtime and Npgsql metric names depend on library versions; the dashboard must be checked against
  Prometheus after upgrades.
