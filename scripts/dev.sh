#!/usr/bin/env sh
# CreatFlow 开发启动器（Linux / macOS）：./scripts/dev.sh [参数...]
# 只依赖 Python 3 标准库；版本判定见 dev.py 顶部说明。
exec python3 "$(dirname "$0")/dev.py" "$@"
