#!/usr/bin/env bash
# Generates initial EF migrations (once) and applies them. Run from repo root with postgres up.
#   tools/ef.sh add      -> create "Initial" migration in each module (commit the result)
#   tools/ef.sh update   -> apply migrations of all modules
set -euo pipefail
cmd="${1:-update}"
for m in Accounts Orders Trades; do
  p="src/SimpleBroker.$m"; c="${m}DbContext"
  case "$cmd" in
    add)    dotnet ef migrations add Initial -p "$p" -s src/SimpleBroker.Host -c "$c" -o Migrations ;;
    update) dotnet ef database update        -p "$p" -s src/SimpleBroker.Host -c "$c" ;;
    *) echo "usage: $0 add|update"; exit 1 ;;
  esac
done
