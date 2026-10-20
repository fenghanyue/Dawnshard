#!/usr/bin/env bash
set -euo pipefail
destination=${1:?Usage: fetch-source.sh DESTINATION}
git clone --depth 1 --branch v1.5 https://github.com/niansa/LuxonServer.git "$destination"
test "$(git -C "$destination" rev-parse HEAD)" = e4bab98d8d6b3aee9ecf4035309a78a59b584f93
git -C "$destination" submodule update --init --depth 1 Luxon tracy
test "$(git -C "$destination/Luxon" rev-parse HEAD)" = 81458d75b65df0ace14651d61b5c922f56da3628
test "$(git -C "$destination/tracy" rev-parse HEAD)" = 05cceee0df3b8d7c6fa87e9638af311dbabc63cb
