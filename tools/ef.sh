#!/usr/bin/env bash
# Generates EF migrations and applies them. Run from repo root with postgres up.
#   tools/ef.sh add <Name> [Module...]   -> create migration <Name> (default Initial) in the listed modules (default: all)
#   tools/ef.sh update                    -> apply migrations of all modules
# Example (stage 2): tools/ef.sh add Stage02 Accounts Orders
set -euo pipefail
cmd="${1:-update}"; name="${2:-Initial}"; modules=("${@:3}")
[ ${#modules[@]} -eq 0 ] && modules=(Accounts Orders Trades)
for m in "${modules[@]}"; do
  p="src/SimpleBroker.$m"; c="${m}DbContext"
  case "$cmd" in
    add)    dotnet ef migrations add "$name" -p "$p" -s src/SimpleBroker.Host -c "$c" -o Migrations ;;
    update) dotnet ef database update        -p "$p" -s src/SimpleBroker.Host -c "$c" ;;
    *) echo "usage: $0 add <Name> [Module...] | update"; exit 1 ;;
  esac
done