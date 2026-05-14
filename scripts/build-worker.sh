#!/usr/bin/env bash
set -euo pipefail

head="$(git rev-parse HEAD)"

is_shallow="$(git rev-parse --is-shallow-repository 2>/dev/null || true)"
if [ "$is_shallow" = "true" ]; then
	git fetch --unshallow --no-tags origin "$head"
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

has_dotnet_10=false
if command -v dotnet >/dev/null 2>&1; then
	while IFS= read -r sdk; do
		if [[ "$sdk" == 10.* ]]; then
			has_dotnet_10=true
			break
		fi
	done < <(dotnet --list-sdks)
fi

if [ "$has_dotnet_10" != "true" ]; then
	curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
	bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
fi

origin_url="$(git remote get-url origin)"
repo_url="$origin_url"

if [[ "$repo_url" =~ ^git@github\.com:(.+)$ ]]; then
	repo_url="https://github.com/${BASH_REMATCH[1]}"
elif [[ "$repo_url" =~ ^ssh://git@github\.com/(.+)$ ]]; then
	repo_url="https://github.com/${BASH_REMATCH[1]}"
elif [[ "$repo_url" =~ ^https://[^/@]+(:[^/@]*)?@github\.com/(.+)$ ]]; then
	repo_url="https://github.com/${BASH_REMATCH[2]}"
fi

repo_url="${repo_url%.git}"

dotnet run --project build -- --force --repo-url "$repo_url" --branch "$head"
